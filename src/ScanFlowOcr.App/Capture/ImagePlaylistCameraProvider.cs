using System.Collections.Immutable;
using System.Diagnostics;
using ScanFlowOcr.Contracts;
using ScanFlowOcr.Imaging;

namespace ScanFlowOcr.App.Capture;

/// <summary>Hardware cameras plus a local image playlist using the same session pipeline.</summary>
internal sealed class ImagePlaylistCameraProvider(ICameraProvider hardware) : ICameraProvider
{
    private const string ImageProviderId = "Image";
    private const long MaxFrameBytes = 5120L * 5120 * 4;
    private readonly object _gate = new();
    private ImmutableArray<string> _paths = [];
    private CameraDescriptor? _descriptor;
    private int _width;
    private int _height;
    private int _startIndex;
    private ImageSession? _activeSession;

    public string Id => "Desktop";
    public event Action<int, string>? ActiveImageChanged;
    public static bool IsImageDevice(CameraId id) => id.ProviderId == ImageProviderId;

    public async Task<CameraDescriptor> ImportPlaylistAsync(IEnumerable<string> paths, CancellationToken token = default)
    {
        var files = paths.Select(Path.GetFullPath)
            .Where(static path => File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (files.IsEmpty) throw new ArgumentException("没有可导入的图片。", nameof(paths));

        int width = 0, height = 0;
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            var size = await Task.Run(() => StillImages.ProbeDimensions(file), token).ConfigureAwait(false);
            if (size.Width < 1 || size.Height < 1 || size.Width > 8192 || size.Height > 8192 ||
                (long)size.Width * size.Height * 4 > MaxFrameBytes)
                throw new InvalidDataException($"图片尺寸超出支持范围：{file}");
            width = Math.Max(width, size.Width);
            height = Math.Max(height, size.Height);
        }
        if ((long)width * height * 4 > MaxFrameBytes)
            throw new InvalidDataException("播放列表画布超出单帧内存上限，请选择尺寸相近的图片。");

        var descriptor = new CameraDescriptor(
            new CameraId(ImageProviderId, "playlist:" + Guid.NewGuid().ToString("N")),
            files.Length == 1 ? $"[图片] {Path.GetFileName(files[0])}" : $"[轮播] {files.Length} 张图片",
            DeviceIdentityKind.BackendId, null, null);
        lock (_gate)
        {
            _paths = files;
            _descriptor = descriptor;
            _width = width;
            _height = height;
            _startIndex = 0;
        }
        return descriptor;
    }

    public void ClearImportedImages()
    {
        lock (_gate)
        {
            _paths = [];
            _descriptor = null;
            _width = 0;
            _height = 0;
            _startIndex = 0;
            _activeSession = null;
        }
    }

    public void SetStartIndex(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _paths.Length) return;
            _startIndex = index;
            _activeSession?.JumpTo(index);
        }
    }

    public async ValueTask<ImmutableArray<CameraDescriptor>> EnumerateAsync(CancellationToken token)
    {
        var hardwareDevices = await hardware.EnumerateAsync(token).ConfigureAwait(false);
        lock (_gate)
            return _descriptor is null ? hardwareDevices : hardwareDevices.Add(_descriptor);
    }

    public ValueTask<ImmutableArray<CaptureMode>> GetModesAsync(CameraId id, CancellationToken token)
    {
        if (!IsImageDevice(id)) return hardware.GetModesAsync(id, token);
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_descriptor?.Id != id) throw new InvalidOperationException("图片源已失效，请重新导入。");
            return ValueTask.FromResult(ImmutableArray.Create(1000, 500, 2000, 5000)
                .Select(ms => new CaptureMode("slideshow_" + ms, _width, _height,
                    1000, ms, FrameEncoding.Raw, PixelFormat.Bgra32)).ToImmutableArray());
        }
    }

    public ValueTask<ICameraSession> OpenAsync(CameraOpenOptions options, CancellationToken token)
    {
        if (!IsImageDevice(options.Device)) return hardware.OpenAsync(options, token);
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_descriptor?.Id != options.Device || _paths.IsEmpty)
                throw new InvalidOperationException("图片源已失效，请重新导入。");
            if (!options.ModeId.StartsWith("slideshow_", StringComparison.Ordinal) ||
                !int.TryParse(options.ModeId.AsSpan("slideshow_".Length), out int intervalMs) ||
                intervalMs is not (500 or 1000 or 2000 or 5000))
                throw new ArgumentException("无效的播放间隔。", nameof(options));
            var mode = new CaptureMode(options.ModeId, _width, _height, 1000, intervalMs,
                FrameEncoding.Raw, PixelFormat.Bgra32);
            var session = new ImageSession(_descriptor, mode, _paths, _startIndex,
                (index, path) => ActiveImageChanged?.Invoke(index, path));
            _activeSession = session;
            return ValueTask.FromResult<ICameraSession>(session);
        }
    }

    private sealed class ImageSession(
        CameraDescriptor device, CaptureMode mode, ImmutableArray<string> files, int startIndex,
        Action<int, string> activeChanged) : ICameraSession
    {
        private CancellationTokenSource? _stop;
        private Task? _loop;
        private int _requestedIndex = -1;
        private long _sequence;

        public string SourceId => device.Id.DeviceKey;
        public SourceState State { get; private set; } = SourceState.Open;
        public CameraDescriptor Device => device;
        public CaptureMode NegotiatedMode => mode;
        public ICameraControls? Controls => null;

        public void JumpTo(int index)
        {
            if (index >= 0 && index < files.Length)
                Interlocked.Exchange(ref _requestedIndex, index);
        }

        public ValueTask StartAsync(Guid streamEpoch, IFrameReceiver receiver, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_loop is not null) throw new InvalidOperationException("图片源已启动。");
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            State = SourceState.Starting;
            _loop = Task.Run(() => RunAsync(streamEpoch, receiver, _stop.Token), CancellationToken.None);
            State = SourceState.Running;
            return ValueTask.CompletedTask;
        }

        private async Task RunAsync(Guid epoch, IFrameReceiver receiver, CancellationToken token)
        {
            int index = startIndex;
            byte[]? current = null;
            var layout = new ImageLayout(FrameEncoding.Raw, PixelFormat.Bgra32, mode.Width, mode.Height,
                [new PlaneLayout(0, checked(mode.Width * 4), checked(mode.Width * 4), mode.Height)],
                ColorRange.Full, ColorMatrix.Unspecified);
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 * mode.FpsDenominator / mode.FpsNumerator));
                while (!token.IsCancellationRequested)
                {
                    int requested = Interlocked.Exchange(ref _requestedIndex, -1);
                    if (requested >= 0) index = requested;
                    if (current is null || requested >= 0)
                    {
                        var decoded = StillImages.DecodeBgra(files[index]);
                        current = FitToCanvas(decoded.Pixels, decoded.Width, decoded.Height, mode.Width, mode.Height);
                        activeChanged(index, files[index]);
                    }
                    var stamp = new FrameStamp(new FrameId(epoch, Interlocked.Increment(ref _sequence)),
                        SourceId, mode.Width, mode.Height, Stopwatch.GetTimestamp(), null);
                    receiver.OnFrame(new CapturedFrame(stamp, layout, current));
                    if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) break;
                    if (Volatile.Read(ref _requestedIndex) < 0 && files.Length > 1)
                    {
                        index = (index + 1) % files.Length;
                        current = null;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                State = SourceState.Faulted;
                receiver.OnFault(new CaptureFault("ImageSource", ex.Message, false));
            }
        }

        private static byte[] FitToCanvas(byte[] pixels, int width, int height, int canvasWidth, int canvasHeight)
        {
            if (width == canvasWidth && height == canvasHeight) return pixels;
            var canvas = new byte[checked(canvasWidth * canvasHeight * 4)];
            Array.Fill(canvas, (byte)255);
            int x = (canvasWidth - width) / 2;
            int y = (canvasHeight - height) / 2;
            for (int row = 0; row < height; row++)
                pixels.AsSpan(row * width * 4, width * 4)
                    .CopyTo(canvas.AsSpan(((y + row) * canvasWidth + x) * 4, width * 4));
            return canvas;
        }

        public async ValueTask StopAsync(CancellationToken token)
        {
            if (_loop is null) return;
            State = SourceState.Stopping;
            _stop?.Cancel();
            try { await _loop.ConfigureAwait(false); }
            finally
            {
                _loop = null;
                _stop?.Dispose();
                _stop = null;
                State = SourceState.Stopped;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            State = SourceState.Disposed;
        }
    }
}
