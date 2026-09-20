using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ScanFlowOcr.Contracts;
using ScanFlowOcr.Imaging;

namespace ScanFlowOcr.Runtime;

/// <summary>
/// OCR-only continuous scan session (camera frames → OCR → dedupe → outputs).
/// Ported from private ScanFlow.Runtime.ScanSession with barcode paths removed.
/// </summary>
public sealed class ScanSession : IScanSession, IFrameReceiver
{
    private readonly ICameraProvider _cameras;
    private readonly IOcrReaderFactory _ocrReaders;
    private readonly string _jpegPath;
    private readonly IRecordDelivery? _delivery;
    private readonly SemaphoreSlim _lifecycle = new(1);
    private readonly object _stateGate = new();
    private readonly object _dedupeGate = new();
    private readonly object _frameGate = new();
    private long _lastOcrObservation;
    private SessionProfile _profile;
    private SessionState _state = SessionState.Created;
    private string? _fault;
    private Guid _epoch;
    private long _revision = 1, _received, _processed, _dropped, _lastPreview, _skippedEvents, _lastDedupeWarningTimestamp, _lastOutputWarningTimestamp;
    private volatile bool _accepting;
    private int _eventReader, _previewReader;
    private ICameraSession? _camera;
    private IOcrReader? _ocrReader;
    private Channel<IImageLease>? _pending, _previewPending;
    private Task? _worker, _previewWorker;
    private readonly ImageAllocator _allocator;
    private readonly Channel<SessionEvent> _events;
    private readonly Channel<IImageLease> _preview = Latest();
    private readonly Dictionary<string, long> _seenOcr = [];
    private readonly Dictionary<string, long> _absentOcr = [];

    private static Channel<IImageLease> Latest() =>
        Channel.CreateBounded<IImageLease>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest }, item => item.Dispose());

    public ScanSession(
        ICameraProvider cameras,
        IOcrReaderFactory ocrReaders,
        string jpegPath,
        SessionProfile profile,
        IRecordDelivery? delivery = null)
    {
        Validate(profile);
        if (!profile.Outputs.IsDefaultOrEmpty && delivery is null)
            throw new ArgumentException("启用外部输出时必须提供可靠交付服务。", nameof(delivery));
        if (profile.Workflow != WorkflowMode.OcrOnly)
            throw new ArgumentException("ScanFlow-OCR 仅支持 OCR 工作流。", nameof(profile));
        if (profile.Ocr is null || profile.Ocr.ProviderId != ocrReaders.ProviderId)
            throw new ArgumentException("配置的 OCR 与实际适配器不一致。", nameof(profile));

        _events = Channel.CreateBounded<SessionEvent>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest },
            _ => Interlocked.Increment(ref _skippedEvents));
        _cameras = cameras;
        _ocrReaders = ocrReaders;
        _jpegPath = jpegPath;
        _profile = profile;
        _delivery = delivery;
        _allocator = new(profile.Scheduling.RetainedByteLimit);
    }

    public SessionSnapshot GetSnapshot()
    {
        lock (_stateGate)
            return new(_state, _profile, _camera?.NegotiatedMode, _epoch == Guid.Empty ? null : _epoch, _revision, _fault,
                Interlocked.Read(ref _received), Interlocked.Read(ref _processed), _allocator.CommittedBytes,
                _delivery is null || _profile.Outputs.IsDefaultOrEmpty ? [] :
                    _delivery.GetStatus().Routes.Where(r => r.Enabled)
                        .Select(r => new OutputRouteSnapshot(r.SinkId, r.PendingCount,
                            r.PendingCount >= r.Capacity, null)).ToImmutableArray());
    }

    // Preview quality can follow viewport zoom without rebuilding the OCR reader
    // or camera session. OCR continues to use the full-resolution pending frame.
    public void SetPreviewBounds(int maxWidth, int maxHeight)
    {
        if (maxWidth < 0 || maxHeight < 0 || (maxWidth == 0) != (maxHeight == 0)) return;
        _allocator.TrimExcess();
        lock (_stateGate)
        {
            _profile = _profile with
            {
                Preview = _profile.Preview with
                {
                    MaxWidth = maxWidth,
                    MaxHeight = maxHeight
                }
            };
        }
    }

    private void SetState(SessionState state, string? reason = null)
    {
        lock (_stateGate) { _state = state; _fault = state == SessionState.Faulted ? reason : null; }
        _events.Writer.TryWrite(new StateChanged(Stopwatch.GetTimestamp(), state, reason));
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StartCore(cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StartCore(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_state == SessionState.Disposed, this);
        if (_state == SessionState.Running) return;
        if (_camera is not null || _ocrReader is not null || _worker is not null)
            await StopCore().ConfigureAwait(false);
        SetState(SessionState.Starting);
        try
        {
            _ocrReader = await Task.Run(async () =>
                await _ocrReaders.CreateAsync(_profile.Ocr!.Settings, token).ConfigureAwait(false), token)
                .ConfigureAwait(false);
            _camera = await _cameras.OpenAsync(((CameraSourceProfile)_profile.Source).Options, token).ConfigureAwait(false);
            _epoch = Guid.NewGuid();
            _lastPreview = 0;
            _pending = Channel.CreateBounded<IImageLease>(
                new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest },
                item => { item.Dispose(); Interlocked.Increment(ref _dropped); });
            _previewPending = Latest();
            var decoder = new JpegDecoder(_jpegPath);
            JpegDecoder previewDecoder;
            try { previewDecoder = new JpegDecoder(_jpegPath); }
            catch { decoder.Dispose(); throw; }
            lock (_dedupeGate) _absentOcr.Clear();
            _worker = Task.Run(() => RecognizeLoop(decoder), CancellationToken.None);
            _previewWorker = Task.Run(() => PreviewLoop(previewDecoder), CancellationToken.None);
            _accepting = true;
            await _camera.StartAsync(_epoch, this, token).ConfigureAwait(false);
            if (_state == SessionState.Faulted) throw new InvalidOperationException(_fault);
            SetState(SessionState.Running);
        }
        catch (Exception ex)
        {
            try { await StopCore().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                SetState(SessionState.Faulted, ex.Message);
                throw new AggregateException("启动失败且清理发生错误。", ex, cleanup);
            }
            SetState(SessionState.Faulted, ex.Message);
            throw;
        }
    }

    public void OnFrame(in CapturedFrame frame)
    {
        lock (_frameGate) OnFrameCore(frame);
    }

    private void OnFrameCore(in CapturedFrame frame)
    {
        if (!_accepting || frame.Stamp.Id.StreamEpoch != _epoch) return;
        Interlocked.Increment(ref _received);
        if (frame.Buffer.Length == 0 || frame.Buffer.Length > _profile.Scheduling.MaxFrameBytes)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        try
        {
            using var owned = _allocator.Allocate(frame.Stamp, frame.Layout, frame.Buffer.Length, ImageTransform.Identity);
            frame.Buffer.CopyTo(owned.WritableBuffer.Span);
            var next = owned.Retain();
            if (_pending is null || !_pending.Writer.TryWrite(next)) next.Dispose();
            var now = Stopwatch.GetTimestamp();
            if (_profile.Preview.Enabled && Stopwatch.GetElapsedTime(_lastPreview, now).TotalSeconds >= 1.0 / _profile.Preview.MaxFps)
            {
                _lastPreview = now;
                var preview = owned.Retain();
                if (_previewPending is null || !_previewPending.Writer.TryWrite(preview)) preview.Dispose();
            }
        }
        catch (InvalidOperationException) { Interlocked.Increment(ref _dropped); }
        catch (Exception ex) { OnFault(new("FrameCopy", ex.Message, false)); }
    }

    public void OnFault(CaptureFault fault)
    {
        lock (_frameGate)
        {
            if (!_accepting) return;
            _accepting = false;
            SetState(SessionState.Faulted, fault.Message);
            _pending?.Writer.TryComplete();
            _previewPending?.Writer.TryComplete();
        }
    }

    private async Task RecognizeLoop(JpegDecoder decoder)
    {
        using (decoder)
        {
            try
            {
                await foreach (var frame in _pending!.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    using (frame)
                    {
                        if (!_accepting) continue;
                        await RecognizeOcrAsync(decoder, frame).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) { OnFault(new("Recognition", ex.Message, false)); }
        }
    }

    private async Task RecognizeOcrAsync(JpegDecoder decoder, IImageLease frame)
    {
        IImageLease? decoded = null;
        IImageLease? cropped = null;
        ImageInput input;
        try
        {
            (decoded, cropped, input) = PrepareOcrInput(decoder, frame, _profile.Ocr!.Region);
        }
        catch (InvalidDataException)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        using (decoded)
        using (cropped)
        {
            var id = new AnalysisId(input.Stamp.Id, _revision);
            var deadline = Stopwatch.GetTimestamp() + (long)(_profile.Scheduling.OcrBudget.TotalSeconds * Stopwatch.Frequency);
            var batch = await _ocrReader!.ReadAsync(input, new(id, deadline), CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref _processed);
            if (!_accepting) return;
            if (batch.Analysis != id) throw new InvalidOperationException("OCR 返回的帧身份与请求不匹配。");
            var items = batch.Items.Select(line => line with { Bounds = Map(line.Bounds, input.ImageToSource) }).ToImmutableArray();
            var analysis = new ScanAnalysis(id, input.Stamp, Stopwatch.GetTimestamp(),
                new(batch.Status, items, batch.EngineTime, batch.Fault));
            _events.Writer.TryWrite(new AnalysisReady(Stopwatch.GetTimestamp(), analysis));
            if (batch.Status == StageStatus.Faulted)
            {
                OnFault(new("Algorithm", batch.Fault?.Message ?? "OCR 失败", false));
                return;
            }
            if (batch.Status != StageStatus.Completed) return;
            await AcceptRecordAsync(id, input.Stamp, items).ConfigureAwait(false);
            // Release idle size-class blocks when they pile up (variable JPEG sizes).
            // Prefer idle-threshold over every-frame trim so steady-state reuse still works.
            MaybeTrimIdle();
        }
    }

    // Trim when idle exceeds ~25% of RetainedByteLimit or 2× MaxFrameBytes.
    private void MaybeTrimIdle()
    {
        long idle = _allocator.IdleBytes;
        if (idle <= 0) return;
        long limit = _profile.Scheduling.RetainedByteLimit;
        long maxFrame = _profile.Scheduling.MaxFrameBytes;
        if (idle > (limit >> 2) || idle > maxFrame * 2)
            _allocator.TrimExcess();
    }

    private (IImageLease? Decoded, IImageLease? Cropped, ImageInput Input) PrepareOcrInput(
        JpegDecoder decoder, IImageLease frame, Rect2? region)
    {
        if (frame.Input.Layout.Encoding == FrameEncoding.Jpeg)
        {
            var decoded = decoder.DecodeBgr(frame.Input, _allocator);
            try { return (decoded, null, CropBgrView(decoded.Input, region)); }
            catch { decoded.Dispose(); throw; }
        }
        if (frame.Input.Layout.PixelFormat == PixelFormat.Bgr24)
        {
            RawImages.ValidateBgr(frame.Input);
            return (null, null, CropBgrView(frame.Input, region));
        }
        RawImages.ValidateBgra(frame.Input);
        if (region is null)
        {
            var converted = RawImages.ToBgr(frame.Input, _allocator);
            return (converted, null, converted.Input);
        }
        var croppedBgra = CropBgraToBgr(frame.Input, region);
        return (null, croppedBgra, croppedBgra?.Input ?? frame.Input);
    }

    private static ImageInput CropBgrView(ImageInput input, Rect2? region)
    {
        if (region is not Rect2 r) return input;
        RawImages.ValidateBgr(input);
        int x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height;
        if (x < 0 || y < 0 || w < 1 || h < 1 || (long)x + w > input.Layout.Width || (long)y + h > input.Layout.Height)
            throw new ArgumentException("识别区域超出图像范围。");
        var plane = input.Layout.Planes[0];
        var transform = input.ImageToSource;
        return input with
        {
            Layout = input.Layout with
            {
                Width = w, Height = h,
                Planes = [new(checked(plane.Offset + y * plane.StrideBytes + x * 3), plane.StrideBytes, checked(w * 3), h)]
            },
            ImageToSource = transform with
            {
                M13 = transform.M11 * x + transform.M12 * y + transform.M13,
                M23 = transform.M21 * x + transform.M22 * y + transform.M23,
                M33 = transform.M31 * x + transform.M32 * y + transform.M33
            }
        };
    }

    private ImageLease? CropBgraToBgr(ImageInput input, Rect2? region)
    {
        if (region is not Rect2 r) return null;
        RawImages.ValidateBgra(input);
        int x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height;
        if (x < 0 || y < 0 || w < 1 || h < 1 || (long)x + w > input.Layout.Width || (long)y + h > input.Layout.Height)
            throw new ArgumentException("识别区域超出图像范围。");
        var plane = input.Layout.Planes[0];
        var result = _allocator.Allocate(input.Stamp, JpegDecoder.BgrLayout(w, h), checked(w * h * 3), new(1, 0, x, 0, 1, y, 0, 0, 1));
        var src = input.Buffer.Span;
        var dest = result.WritableBuffer.Span;
        for (int row = 0; row < h; row++)
        {
            var rowSpan = src.Slice(plane.Offset + (y + row) * plane.StrideBytes + x * 4, w * 4);
            var destRow = dest.Slice(row * w * 3, w * 3);
            for (int col = 0; col < w; col++)
            {
                destRow[col * 3] = rowSpan[col * 4];
                destRow[col * 3 + 1] = rowSpan[col * 4 + 1];
                destRow[col * 3 + 2] = rowSpan[col * 4 + 2];
            }
        }
        return result;
    }

    private static Quad Map(Quad q, ImageTransform t)
    {
        Point2 P(Point2 p) => new(p.X + t.M13, p.Y + t.M23);
        return new(P(q.P0), P(q.P1), P(q.P2), P(q.P3));
    }

    private ImmutableArray<T> FilterCore<T>(
        ImmutableArray<T> items,
        Func<T, string> keySelector,
        Dictionary<string, long> seen,
        Dictionary<string, long> absent,
        ref long lastObservation,
        TimeSpan budget)
    {
        lock (_dedupeGate)
        {
            if (_profile.Dedupe.Mode == DedupeMode.IntraFrame)
            {
                var seenInFrame = new HashSet<string>(StringComparer.Ordinal);
                var intraResult = ImmutableArray.CreateBuilder<T>();
                foreach (var item in items)
                {
                    var key = keySelector(item);
                    if (seenInFrame.Add(key))
                        intraResult.Add(item);
                }
                return intraResult.ToImmutable();
            }

            long now = Stopwatch.GetTimestamp();
            if (_profile.Dedupe.Mode == DedupeMode.Cooldown)
                foreach (var entry in seen.Where(e => Stopwatch.GetElapsedTime(e.Value, now) >= _profile.Dedupe.Interval).ToArray())
                    seen.Remove(entry.Key);
            var keys = items.Select(keySelector).ToHashSet(StringComparer.Ordinal);
            if (_profile.Dedupe.Mode == DedupeMode.UntilAbsent)
            {
                var mode = _camera?.NegotiatedMode;
                double cadence = mode is { FpsNumerator: > 0, FpsDenominator: > 0 }
                    ? (double)mode.FpsDenominator / mode.FpsNumerator : 0;
                var maxGap = TimeSpan.FromSeconds(Math.Max(cadence * 3,
                    (_profile.Scheduling.MaxQueueAge + budget).TotalSeconds));
                if (lastObservation == 0 || Stopwatch.GetElapsedTime(lastObservation, now) > maxGap)
                    absent.Clear();
                lastObservation = now;
                foreach (var key in seen.Keys.ToArray())
                {
                    if (keys.Contains(key)) absent.Remove(key);
                    else if (!absent.TryGetValue(key, out long since)) absent[key] = now;
                    else if (Stopwatch.GetElapsedTime(since, now) >= _profile.Dedupe.Interval)
                    {
                        seen.Remove(key);
                        absent.Remove(key);
                    }
                }
            }
            var result = ImmutableArray.CreateBuilder<T>();
            foreach (var item in items)
            {
                var key = keySelector(item);
                if (seen.TryGetValue(key, out long previous) &&
                    (_profile.Dedupe.Mode != DedupeMode.Cooldown ||
                     Stopwatch.GetElapsedTime(previous, now) < _profile.Dedupe.Interval))
                    continue;
                if (!seen.ContainsKey(key) && seen.Count >= _profile.Dedupe.MaxEntries)
                {
                    long lastWarning = Interlocked.Read(ref _lastDedupeWarningTimestamp);
                    if (lastWarning == 0 || Stopwatch.GetElapsedTime(lastWarning, now).TotalSeconds >= 2.0)
                    {
                        Interlocked.Exchange(ref _lastDedupeWarningTimestamp, now);
                        _events.Writer.TryWrite(new DedupeOverflowWarning(now, "去重池已满，未记录新文本，请手动重置或新建会话。"));
                    }
                    continue;
                }
                seen[key] = now;
                result.Add(item);
            }
            return result.ToImmutable();
        }
    }

    private ImmutableArray<OcrLine> FilterOcr(ImmutableArray<OcrLine> items) =>
        FilterCore(items, static line => line.Text, _seenOcr, _absentOcr, ref _lastOcrObservation, _profile.Scheduling.OcrBudget);

    private async ValueTask AcceptRecordAsync(AnalysisId id, FrameStamp stamp, ImmutableArray<OcrLine> lines)
    {
        Dictionary<string, long> seenOcr, absentOcr;
        long lastOcr;
        lock (_dedupeGate)
        {
            seenOcr = new(_seenOcr);
            absentOcr = new(_absentOcr);
            lastOcr = _lastOcrObservation;
        }
        var acceptedLines = FilterOcr(lines.IsDefault ? ImmutableArray<OcrLine>.Empty : lines);
        if (acceptedLines.IsEmpty) return;
        var record = new ScanRecord(Guid.NewGuid(), id, stamp, DateTimeOffset.UtcNow,
            acceptedLines, ImmutableDictionary<string, string>.Empty);
        if (_delivery is not null && !_profile.Outputs.IsDefaultOrEmpty &&
            !await _delivery.TryAcceptAsync(record, CancellationToken.None).ConfigureAwait(false))
        {
            lock (_dedupeGate)
            {
                Restore(_seenOcr, seenOcr);
                Restore(_absentOcr, absentOcr);
                _lastOcrObservation = lastOcr;
            }
            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Read(ref _lastOutputWarningTimestamp);
            if (previous == 0 || Stopwatch.GetElapsedTime(previous, now).TotalSeconds >= 2)
            {
                Interlocked.Exchange(ref _lastOutputWarningTimestamp, now);
                _events.Writer.TryWrite(new OutputAdmissionWarning(now,
                    _delivery.GetStatus().LastError ?? "输出队列未接纳新记录。"));
            }
            return;
        }
        _events.Writer.TryWrite(new ScanRecordReady(Stopwatch.GetTimestamp(), record));
    }

    private static void Restore(Dictionary<string, long> destination, Dictionary<string, long> source)
    {
        destination.Clear();
        foreach (var item in source) destination.Add(item.Key, item.Value);
    }

    private async Task PreviewLoop(JpegDecoder decoder)
    {
        using (decoder)
        {
            try
            {
                await foreach (var frame in _previewPending!.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    using (frame)
                    {
                        if (!_accepting) continue;
                        if (frame.Input.Layout.Encoding == FrameEncoding.Raw)
                            RawImages.ValidateBgra(frame.Input);
                        var previewProfile = _profile.Preview;
                        IImageLease color;
                        try
                        {
                            color = frame.Input.Layout.Encoding == FrameEncoding.Jpeg
                                ? decoder.DecodePreview(frame.Input, _allocator, previewProfile.MaxWidth, previewProfile.MaxHeight)
                                : frame.Retain();
                        }
                        catch (InvalidDataException)
                        {
                            continue;
                        }

                        using (color)
                        {
                            var input = color.Input;
                            bool nativePreview = previewProfile.MaxWidth == 0 && previewProfile.MaxHeight == 0;
                            double scale = nativePreview ? 1 : Math.Min(1, Math.Min(
                                (double)previewProfile.MaxWidth / input.Layout.Width,
                                (double)previewProfile.MaxHeight / input.Layout.Height));
                            if (scale >= 1)
                            {
                                var full = color.Retain();
                                if (!_accepting || !_preview.Writer.TryWrite(full)) full.Dispose();
                                continue;
                            }
                            int w = Math.Max(1, (int)(input.Layout.Width * scale));
                            int h = Math.Max(1, (int)(input.Layout.Height * scale));
                            var layout = new ImageLayout(FrameEncoding.Raw, PixelFormat.Bgra32, w, h,
                                [new(0, checked(w * 4), checked(w * 4), h)], ColorRange.Full, ColorMatrix.Unspecified);
                            var image = _allocator.Allocate(input.Stamp, layout, checked(w * h * 4),
                                new((double)frame.Input.Layout.Width / w, 0, 0, 0, (double)frame.Input.Layout.Height / h, 0, 0, 0, 1));
                            var destination = image.WritableBuffer.Span;
                            var source = input.Buffer.Span;
                            var plane = input.Layout.Planes[0];
                            for (int y = 0; y < h; y++)
                            {
                                int sourceY = y * input.Layout.Height / h;
                                for (int x = 0; x < w; x++)
                                {
                                    int sourceX = x * input.Layout.Width / w;
                                    source.Slice(plane.Offset + sourceY * plane.StrideBytes + sourceX * 4, 4)
                                        .CopyTo(destination.Slice((y * w + x) * 4, 4));
                                }
                            }
                            if (!_accepting || !_preview.Writer.TryWrite(image)) image.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex) { OnFault(new("Preview", ex.Message, false)); }
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (_state != SessionState.Disposed) await StopCore().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCore()
    {
        lock (_frameGate)
        {
            _accepting = false;
            SetState(SessionState.Stopping);
            _pending?.Writer.TryComplete();
            _previewPending?.Writer.TryComplete();
        }
        var errors = new List<Exception>();
        async Task Attempt(Func<ValueTask> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (_camera is not null) await Attempt(() => _camera.StopAsync(CancellationToken.None)).ConfigureAwait(false);
        if (_worker is not null) await Attempt(() => new ValueTask(_worker)).ConfigureAwait(false);
        if (_previewWorker is not null) await Attempt(() => new ValueTask(_previewWorker)).ConfigureAwait(false);
        _worker = null;
        _previewWorker = null;
        if (_pending is not null) while (_pending.Reader.TryRead(out var pending)) pending.Dispose();
        if (_previewPending is not null) while (_previewPending.Reader.TryRead(out var pending)) pending.Dispose();
        while (_preview.Reader.TryRead(out var lease)) lease.Dispose();
        if (_ocrReader is not null)
            await Attempt(async () => { await _ocrReader.DisposeAsync().ConfigureAwait(false); _ocrReader = null; }).ConfigureAwait(false);
        if (_camera is not null)
            await Attempt(async () => { await _camera.DisposeAsync().ConfigureAwait(false); _camera = null; }).ConfigureAwait(false);
        _allocator.TrimExcess();
        if (errors.Count > 0)
        {
            var error = new AggregateException("停止扫描时发生清理错误。", errors);
            SetState(SessionState.Faulted, error.Message);
            throw error;
        }
        SetState(SessionState.Stopped);
    }

    public async ValueTask ReconfigureAsync(SessionProfile profile, CancellationToken cancellationToken)
    {
        Validate(profile);
        if (profile.Workflow != WorkflowMode.OcrOnly || profile.Ocr is null || profile.Ocr.ProviderId != _ocrReaders.ProviderId)
            throw new ArgumentException("配置的 OCR 与实际适配器不一致。", nameof(profile));
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_state == SessionState.Disposed, this);
            bool restart = _state == SessionState.Running;
            var old = _profile;
            if (profile.Scheduling.RetainedByteLimit != old.Scheduling.RetainedByteLimit)
                throw new NotSupportedException("更改内存上限需要创建新会话。");
            await StopCore().ConfigureAwait(false);
            Dictionary<string, long> oldSeenOcr;
            lock (_dedupeGate) { oldSeenOcr = new(_seenOcr); }
            _profile = profile;
            _revision++;
            lock (_dedupeGate) { _seenOcr.Clear(); _absentOcr.Clear(); }
            try
            {
                if (restart) await StartCore(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception applyError)
            {
                _profile = old;
                _revision++;
                lock (_dedupeGate)
                {
                    _seenOcr.Clear();
                    foreach (var entry in oldSeenOcr) _seenOcr.Add(entry.Key, entry.Value);
                    _absentOcr.Clear();
                }
                try { if (restart) await StartCore(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception restoreError)
                {
                    throw new AggregateException("新配置未应用；旧配置已还原，但恢复扫描失败。", applyError, restoreError);
                }
                throw new InvalidOperationException("新配置未应用，已恢复旧配置。", applyError);
            }
        }
        finally { _lifecycle.Release(); }
    }

    public ValueTask ClearHistoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_dedupeGate) { _seenOcr.Clear(); _absentOcr.Clear(); }
        Interlocked.Exchange(ref _lastDedupeWarningTimestamp, 0);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<SessionEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _eventReader, 1, 0) != 0)
            throw new InvalidOperationException("会话事件仅支持一个读者。");
        try
        {
            await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                long skipped = Interlocked.Exchange(ref _skippedEvents, 0);
                if (skipped > 0) yield return new EventsSkipped(Stopwatch.GetTimestamp(), skipped);
                yield return item;
            }
        }
        finally { Interlocked.Exchange(ref _eventReader, 0); }
    }

    public async IAsyncEnumerable<IImageLease> ReadPreviewAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _previewReader, 1, 0) != 0)
            throw new InvalidOperationException("预览仅支持一个读者。");
        try
        {
            await foreach (var lease in _preview.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return lease;
        }
        finally { Interlocked.Exchange(ref _previewReader, 0); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state == SessionState.Disposed) return;
            try { await StopCore().ConfigureAwait(false); }
            finally
            {
                _allocator.Dispose();
                if (_camera is null && _ocrReader is null)
                {
                    SetState(SessionState.Disposed);
                    _events.Writer.TryComplete();
                    _preview.Writer.TryComplete();
                }
            }
        }
        finally { _lifecycle.Release(); }
    }

    private static void Validate(SessionProfile p)
    {
        if (p.Source is not CameraSourceProfile)
            throw new NotSupportedException("首版仅支持相机来源。");
        if (p.Workflow != WorkflowMode.OcrOnly || p.Ocr is null)
            throw new NotSupportedException("OCR 模式需要 OCR 配置。");
        if (p.SchemaVersion != 1 || !Enum.IsDefined(p.Dedupe.Mode))
            throw new NotSupportedException("配置版本或识别选项不受支持。");
        if (p.Ocr.Region is Rect2 r &&
            (!double.IsFinite(r.X) || !double.IsFinite(r.Y) || !double.IsFinite(r.Width) || !double.IsFinite(r.Height)))
            throw new ArgumentException("识别区域必须为有限像素坐标。", nameof(p));
        if (!p.Outputs.IsDefaultOrEmpty &&
            (p.Outputs.Length > 1 ||
             p.Outputs.Select(o => o.SinkId).Distinct(StringComparer.Ordinal).Count() != p.Outputs.Length ||
             p.Outputs.Any(o => o.SinkId is not ("mqtt" or "tcp" or "keyboard"))))
            throw new NotSupportedException("输出通道配置无效。");
        if (p.Scheduling.PendingFrames != 1 || p.Scheduling.MaxFrameBytes < 1 ||
            p.Scheduling.RetainedByteLimit < p.Scheduling.MaxFrameBytes ||
            p.Scheduling.OcrBudget <= TimeSpan.Zero ||
            p.Scheduling.MaxQueueAge <= TimeSpan.Zero ||
            p.Scheduling.MaxResultAge <= TimeSpan.Zero)
            throw new ArgumentException("调度参数无效。", nameof(p));
        bool nativePreview = p.Preview.MaxWidth == 0 && p.Preview.MaxHeight == 0;
        if (p.Preview.MaxFps is < 1 or > 60 ||
            (!nativePreview && (p.Preview.MaxWidth < 1 || p.Preview.MaxHeight < 1)) ||
            p.Dedupe.MaxEntries < 1 || p.Dedupe.Interval < TimeSpan.Zero)
            throw new ArgumentException("预览或去重参数无效。", nameof(p));
    }
}
