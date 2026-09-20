using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ScanFlowOcr.Contracts;
using ScanFlowOcr.Imaging;
using ContractPixelFormat = ScanFlowOcr.Contracts.PixelFormat;

namespace ScanFlowOcr.App;

internal readonly record struct CameraPreviewMetrics(
    long FramesReceived, long FramesDropped, long FramesDecoded);

internal readonly record struct CameraOcrFrame(
    byte[] Bgr, int Width, int Height, FrameStamp Stamp);

/// <summary>
/// FlashCap camera session → JPEG/raw BGRA normalization → Avalonia preview bitmap.
/// Decode runs off the UI thread; all WriteableBitmap / Image.Source / UI callbacks
/// are marshaled to <see cref="Dispatcher.UIThread"/>. Drops frames when the previous
/// decode is still in flight. Ignores in-flight work after stop/dispose.
/// </summary>
internal sealed class CameraPreviewController : IFrameReceiver, IAsyncDisposable
{
    // Raw 4K BGRA input plus one scaled preview can exceed 64 MiB; keep the
    // standalone preview bounded while allowing the camera's negotiated frame.
    private readonly ImageAllocator _allocator = new(128L * 1024 * 1024);
    private readonly object _latestGate = new();
    private readonly object _previewUiGate = new();
    private readonly Action<string> _log;
    private readonly Action<string> _setError;
    private readonly Action<WriteableBitmap?, FrameStamp?> _setPreview;
    private readonly Action _onStopped;
    private int _previewMaxWidth;
    private int _previewMaxHeight;

    private JpegDecoder? _decoder;
    private string? _jpegLibraryPath;
    private ICameraSession? _session;
    private int _decodeBusy;
    private int _disposed;
    /// <summary>1 while the session should accept frames; cleared on stop before dispose races.</summary>
    private int _acceptFrames;
    private int _previewEpoch;
    private IImageLease? _latestSourceLease;
    private WriteableBitmap? _bitmap;
    private byte[]? _rowScratch;
    private PendingPreview? _pendingPreview;
    private int _previewUiScheduled;
    private long _framesReceived;
    private long _framesDropped;
    private long _framesDecoded;

    private sealed class PendingPreview(ImageLease frame, int width, int height, FrameStamp stamp, int epoch)
    {
        public ImageLease Frame { get; } = frame;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public FrameStamp Stamp { get; } = stamp;
        public int Epoch { get; } = epoch;
    }

    public CameraPreviewController(
        Action<string> log,
        Action<string> setError,
        Action<WriteableBitmap?, FrameStamp?> setPreview,
        Action onStopped,
        int previewMaxWidth = 1280,
        int previewMaxHeight = 720)
    {
        _log = log;
        _setError = setError;
        _setPreview = setPreview;
        _onStopped = onStopped;
        _previewMaxWidth = previewMaxWidth > 0 && previewMaxHeight > 0 ? previewMaxWidth : 0;
        _previewMaxHeight = previewMaxWidth > 0 && previewMaxHeight > 0 ? previewMaxHeight : 0;
    }

    public bool IsRunning => _session is { State: SourceState.Running or SourceState.Starting };

    public CameraPreviewMetrics GetMetrics() => new(
        Interlocked.Read(ref _framesReceived),
        Interlocked.Read(ref _framesDropped),
        Interlocked.Read(ref _framesDecoded));

    public void SetPreviewBounds(int maxWidth, int maxHeight)
    {
        if (maxWidth < 0 || maxHeight < 0 || (maxWidth == 0) != (maxHeight == 0)) return;
        _allocator.TrimExcess();
        Volatile.Write(ref _previewMaxWidth, maxWidth);
        Volatile.Write(ref _previewMaxHeight, maxHeight);
    }

    public async Task<CameraOcrFrame?> DecodeLatestBgrAsync(CancellationToken cancellationToken)
    {
        string? jpegLibraryPath = _jpegLibraryPath;
        IImageLease sourceLease;
        ImageInput input;
        lock (_latestGate)
        {
            if (_latestSourceLease is null)
                return null;
            sourceLease = _latestSourceLease.Retain();
            input = sourceLease.Input;
        }

        return await Task.Run(() =>
        {
            using (sourceLease)
            {
                if (input.Layout.Encoding == FrameEncoding.Jpeg)
                {
                    string libraryPath = jpegLibraryPath ?? throw new InvalidOperationException("TurboJPEG 尚未初始化。");
                    using var decoder = new JpegDecoder(libraryPath);
                    byte[] bgr = new byte[checked(input.Layout.Width * input.Layout.Height * 3)];
                    decoder.DecodeBgrInto(input, bgr);
                    return new CameraOcrFrame(bgr, input.Layout.Width, input.Layout.Height, input.Stamp);
                }

                byte[] rawBgr = CopyRawToBgr(input);
                return new CameraOcrFrame(rawBgr, input.Layout.Width, input.Layout.Height, input.Stamp);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(ICameraSession session, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (!TurboJpegNative.TryResolveLibraryPath(out string? libraryPath, out string error))
        {
            PostUi(() => _setError(error));
            await session.DisposeAsync().ConfigureAwait(false);
            throw new FileNotFoundException(error);
        }

        PostUi(() => _setError(""));
        _decoder?.Dispose();
        _decoder = new JpegDecoder(libraryPath);
        _jpegLibraryPath = libraryPath;
        ClearLatestSource();
        ClearPendingPreview();
        Interlocked.Exchange(ref _framesReceived, 0);
        Interlocked.Exchange(ref _framesDropped, 0);
        Interlocked.Exchange(ref _framesDecoded, 0);
        PostUi(() => _log($"TurboJPEG loaded: {libraryPath}"));

        // Take ownership of the session for the lifetime of this controller.
        _session = session;
        Interlocked.Increment(ref _previewEpoch);
        Volatile.Write(ref _acceptFrames, 1);
        try
        {
            var epoch = Guid.NewGuid();
            await session.StartAsync(epoch, this, cancellationToken).ConfigureAwait(false);
            string name = session.Device.DisplayName;
            int w = session.NegotiatedMode.Width;
            int h = session.NegotiatedMode.Height;
            PostUi(() => _log($"Camera started: {name} {w}×{h}"));
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        // Stop accepting frames first so in-flight decode/UI posts become no-ops.
        Volatile.Write(ref _acceptFrames, 0);
        Interlocked.Increment(ref _previewEpoch);
        ClearPendingPreview();

        ICameraSession? session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            await WaitForDecodeIdleAsync().ConfigureAwait(false);
            ClearLatestSource();
            _allocator.TrimExcess();
            return;
        }

        try
        {
            await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
            await WaitForDecodeIdleAsync().ConfigureAwait(false);
            ClearLatestSource();
            _allocator.TrimExcess();
            PostUi(() =>
            {
                ClearPreviewBitmap();
                _onStopped();
            });
        }
    }

    public void OnFrame(in CapturedFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Volatile.Read(ref _acceptFrames) == 0) return;
        Interlocked.Increment(ref _framesReceived);
        JpegDecoder? decoder = _decoder;
        bool supported = frame.Layout.Encoding == FrameEncoding.Jpeg ||
            (frame.Layout.Encoding == FrameEncoding.Raw &&
             (frame.Layout.PixelFormat == ContractPixelFormat.Bgra32 || frame.Layout.PixelFormat == ContractPixelFormat.Bgr24));
        if (!supported || (frame.Layout.Encoding == FrameEncoding.Jpeg && decoder is null))
        {
            Interlocked.Increment(ref _framesDropped);
            return;
        }
        if (Interlocked.CompareExchange(ref _decodeBusy, 1, 0) != 0)
        {
            Interlocked.Increment(ref _framesDropped);
            return;
        }

        var stamp = frame.Stamp;
        var layout = frame.Layout;
        ImageLease captured;
        try
        {
            captured = _allocator.Allocate(stamp, layout, frame.Buffer.Length, ImageTransform.Identity);
            frame.Buffer.CopyTo(captured.WritableBuffer.Span);
        }
        catch
        {
            Interlocked.Increment(ref _framesDropped);
            Interlocked.Exchange(ref _decodeBusy, 0);
            return;
        }
        int epoch = Volatile.Read(ref _previewEpoch);

        _ = Task.Run(() =>
        {
            using (captured)
            try
            {
                if (Volatile.Read(ref _acceptFrames) == 0 ||
                    Volatile.Read(ref _disposed) != 0 ||
                    Volatile.Read(ref _previewEpoch) != epoch)
                {
                    return;
                }

                var input = captured.Input;
                int maxWidth = Volatile.Read(ref _previewMaxWidth);
                int maxHeight = Volatile.Read(ref _previewMaxHeight);
                ImageLease? previewLease = null;
                try
                {
                    previewLease = layout.Encoding == FrameEncoding.Jpeg
                        ? decoder!.DecodePreview(input, _allocator, maxWidth, maxHeight)
                        : CopyRawToBgra(input, _allocator, maxWidth, maxHeight);
                    Interlocked.Increment(ref _framesDecoded);

                    if (Volatile.Read(ref _acceptFrames) == 0 ||
                        Volatile.Read(ref _disposed) != 0 ||
                        Volatile.Read(ref _previewEpoch) != epoch)
                    {
                        return;
                    }

                    IImageLease? previous;
                    lock (_latestGate)
                    {
                        previous = _latestSourceLease;
                        _latestSourceLease = captured.Retain();
                    }
                    previous?.Dispose();

                    // Bitmap create/lock/Source assign must run on the UI thread.
                    // Keep only the newest decoded lease: posting one closure per
                    // camera frame would retain a full pixel array for every queued
                    // dispatcher callback when rendering falls behind.
                    QueuePreview(previewLease, epoch);
                    previewLease = null;
                }
                finally { previewLease?.Dispose(); }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _framesDropped);
                string prefix = layout.Encoding == FrameEncoding.Jpeg ? "TurboJPEG decode failed" : "Camera frame conversion failed";
                PostUi(() => _setError($"{prefix}: {ex.Message}"));
            }
            finally
            {
                Interlocked.Exchange(ref _decodeBusy, 0);
            }
        });
    }

    private void QueuePreview(ImageLease frame, int epoch)
    {
        bool schedule = false;
        PendingPreview? previous = null;
        var input = frame.Input;
        lock (_previewUiGate)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _acceptFrames) == 0 ||
                Volatile.Read(ref _previewEpoch) != epoch)
            {
                frame.Dispose();
                return;
            }

            // Replacing this reference releases the previous native preview
            // buffer instead of retaining every frame in the UI queue.
            previous = _pendingPreview;
            _pendingPreview = new PendingPreview(
                frame, input.Layout.Width, input.Layout.Height, input.Stamp, epoch);
            if (_previewUiScheduled == 0)
            {
                _previewUiScheduled = 1;
                schedule = true;
            }
        }

        previous?.Frame.Dispose();
        if (schedule)
            PostUi(ProcessPendingPreview);
    }

    private void ProcessPendingPreview()
    {
        PendingPreview? pending;
        lock (_previewUiGate)
        {
            pending = _pendingPreview;
            _pendingPreview = null;
        }

        if (pending is not null)
        {
            try
            {
                if (Volatile.Read(ref _disposed) == 0 &&
                    Volatile.Read(ref _acceptFrames) != 0 &&
                    Volatile.Read(ref _previewEpoch) == pending.Epoch)
                {
                    int stride = checked(pending.Width * 4);
                    if (_bitmap is null ||
                        _bitmap.PixelSize.Width != pending.Width ||
                        _bitmap.PixelSize.Height != pending.Height)
                    {
                        _bitmap?.Dispose();
                        _bitmap = new WriteableBitmap(
                            new PixelSize(pending.Width, pending.Height),
                            new Vector(96, 96),
                            PixelFormats.Bgra8888,
                            AlphaFormat.Opaque);
                    }

                    if (_rowScratch is null || _rowScratch.Length < stride)
                        _rowScratch = new byte[stride];

                    var input = pending.Frame.Input;
                    var source = input.Buffer.Span;
                    var plane = input.Layout.Planes[0];
                    using (var fb = _bitmap.Lock())
                    {
                        for (int y = 0; y < pending.Height; y++)
                        {
                            source.Slice(plane.Offset + y * plane.StrideBytes, stride)
                                .CopyTo(_rowScratch.AsSpan(0, stride));
                            Marshal.Copy(
                                _rowScratch,
                                0,
                                IntPtr.Add(fb.Address, y * fb.RowBytes),
                                stride);
                        }
                    }

                    _setPreview(_bitmap, pending.Stamp);
                }
            }
            catch (Exception ex)
            {
                _setError($"Preview update failed: {ex.Message}");
            }
            finally
            {
                pending.Frame.Dispose();
            }
        }

        bool scheduleNext;
        lock (_previewUiGate)
        {
            scheduleNext = _pendingPreview is not null;
            if (!scheduleNext)
                _previewUiScheduled = 0;
        }

        if (scheduleNext)
            Dispatcher.UIThread.Post(ProcessPendingPreview, DispatcherPriority.Render);
    }

    private void ClearPendingPreview()
    {
        PendingPreview? pending;
        lock (_previewUiGate)
        {
            pending = _pendingPreview;
            _pendingPreview = null;
        }
        pending?.Frame.Dispose();
    }

    private static ImageLease CopyRawToBgra(
        ImageInput input, ImageAllocator allocator, int maxWidth, int maxHeight)
    {
        int width = input.Layout.Width;
        int height = input.Layout.Height;
        int sourcePixelBytes;
        if (input.Layout.PixelFormat == ContractPixelFormat.Bgra32)
        {
            RawImages.ValidateBgra(input);
            sourcePixelBytes = 4;
        }
        else if (input.Layout.PixelFormat == ContractPixelFormat.Bgr24)
        {
            RawImages.ValidateBgr(input);
            sourcePixelBytes = 3;
        }
        else
        {
            throw new ArgumentException("预览只支持 BGR24/BGRA32 raw 相机帧。", nameof(input));
        }

        var plane = input.Layout.Planes[0];
        double scale = maxWidth > 0 && maxHeight > 0
            ? Math.Min(1, Math.Min((double)maxWidth / width, (double)maxHeight / height))
            : 1;
        int destinationWidth = Math.Max(1, (int)Math.Round(width * scale));
        int destinationHeight = Math.Max(1, (int)Math.Round(height * scale));
        int destinationStride = checked(destinationWidth * 4);
        var layout = new ImageLayout(
            FrameEncoding.Raw,
            ContractPixelFormat.Bgra32,
            destinationWidth,
            destinationHeight,
            [new(0, destinationStride, destinationStride, destinationHeight)],
            input.Layout.Range,
            input.Layout.Matrix);
        var result = allocator.Allocate(
            input.Stamp,
            layout,
            checked(destinationStride * destinationHeight),
            input.ImageToSource);
        var source = input.Buffer.Span;
        try
        {
            var destination = result.WritableBuffer.Span;
            for (int y = 0; y < destinationHeight; y++)
            {
                int sourceY = Math.Min(height - 1, y * height / destinationHeight);
                var sourceRow = source.Slice(checked(plane.Offset + sourceY * plane.StrideBytes), checked(width * sourcePixelBytes));
                var destinationRow = destination.Slice(y * destinationStride, destinationStride);
                for (int x = 0; x < destinationWidth; x++)
                {
                    int sourceX = Math.Min(width - 1, x * width / destinationWidth);
                    int sourceOffset = sourceX * sourcePixelBytes;
                    int destinationOffset = x * 4;
                    destinationRow[destinationOffset] = sourceRow[sourceOffset];
                    destinationRow[destinationOffset + 1] = sourceRow[sourceOffset + 1];
                    destinationRow[destinationOffset + 2] = sourceRow[sourceOffset + 2];
                    destinationRow[destinationOffset + 3] = 255;
                }
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static byte[] CopyRawToBgr(ImageInput input)
    {
        int width = input.Layout.Width;
        int height = input.Layout.Height;
        var plane = input.Layout.Planes[0];
        var source = input.Buffer.Span;
        byte[] result = new byte[checked(width * height * 3)];
        if (input.Layout.PixelFormat == ContractPixelFormat.Bgr24)
        {
            RawImages.ValidateBgr(input);
            for (int y = 0; y < height; y++)
                source.Slice(plane.Offset + y * plane.StrideBytes, width * 3)
                    .CopyTo(result.AsSpan(y * width * 3, width * 3));
            return result;
        }

        RawImages.ValidateBgra(input);
        for (int y = 0; y < height; y++)
        {
            var sourceRow = source.Slice(plane.Offset + y * plane.StrideBytes, width * 4);
            var destinationRow = result.AsSpan(y * width * 3, width * 3);
            for (int x = 0; x < width; x++)
            {
                destinationRow[x * 3] = sourceRow[x * 4];
                destinationRow[x * 3 + 1] = sourceRow[x * 4 + 1];
                destinationRow[x * 3 + 2] = sourceRow[x * 4 + 2];
            }
        }
        return result;
    }

    public void OnFault(CaptureFault fault)
    {
        PostUi(() =>
        {
            _setError($"Camera fault: {fault.Code} — {fault.Message}");
            _log($"Camera fault: {fault.Code} {fault.Message} (reconnect={fault.CanReconnect})");
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _acceptFrames, 0);
        Interlocked.Increment(ref _previewEpoch);
        await StopAsync().ConfigureAwait(false);
        await WaitForDecodeIdleAsync().ConfigureAwait(false);
        _decoder?.Dispose();
        _decoder = null;
        _jpegLibraryPath = null;
        _allocator.Dispose();
        PostUi(() =>
        {
            ClearPreviewBitmap();
        });
    }

    private void ClearPreviewBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _setPreview(null, null);
    }

    private void ClearLatestSource()
    {
        IImageLease? previous;
        lock (_latestGate)
        {
            previous = _latestSourceLease;
            _latestSourceLease = null;
        }
        previous?.Dispose();
    }

    private static void PostUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action, DispatcherPriority.Render);
    }

    private async Task WaitForDecodeIdleAsync()
    {
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _decodeBusy) != 0)
        {
            if (sw.ElapsedMilliseconds > 5000)
                break;
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
