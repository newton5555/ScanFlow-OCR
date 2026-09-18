using System.Collections.Immutable;
using System.Diagnostics;
using FlashCap;
using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.Capture.FlashCap;

public sealed class FlashCapProvider : ICameraProvider
{
    public const string BackendEnvironmentVariable = "SCANFLOW_OCR_CAMERA_BACKEND";

    public string Id => "FlashCap";
    private readonly Dictionary<string, CaptureDeviceDescriptor> _devices = [];
    private readonly object _gate = new();
    public ValueTask<ImmutableArray<CameraDescriptor>> EnumerateAsync(CancellationToken cancellationToken) => new(Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _devices.Clear();
            foreach (var device in EnumerateUsableDescriptors())
            {
                _devices[$"{device.DeviceType}:{device.Identity}"] = device;
            }
            return _devices.Select(p => new CameraDescriptor(new(Id, p.Key), p.Value.Name, DeviceIdentityKind.BackendId, null, null)).ToImmutableArray();
        }
    }, cancellationToken));

    private static IEnumerable<CaptureDeviceDescriptor> EnumerateUsableDescriptors()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            return FilterJpeg(new CaptureDevices().EnumerateDescriptors());

        string? preference = Environment.GetEnvironmentVariable(BackendEnvironmentVariable)?.Trim();
        if (string.Equals(preference, "directshow", StringComparison.OrdinalIgnoreCase))
            return FilterJpeg(new global::FlashCap.Devices.DirectShowDevices().EnumerateDescriptors());
        if (string.Equals(preference, "vfw", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(preference, "videoforwindows", StringComparison.OrdinalIgnoreCase))
            return FilterJpeg(new global::FlashCap.Devices.VideoForWindowsDevices().EnumerateDescriptors());
        if (string.Equals(preference, "mediafoundation", StringComparison.OrdinalIgnoreCase))
            return FilterJpeg(new global::FlashCap.Devices.MediaFoundationDevices().EnumerateDescriptors());

        // MF is preferred because it shuts down its media source cleanly on reopen.
        // Some older/UVC drivers expose MJPEG through DirectShow only, so keep a
        // compatibility fallback instead of hiding a working camera completely.
        var mediaFoundation = FilterJpeg(new global::FlashCap.Devices.MediaFoundationDevices().EnumerateDescriptors()).ToArray();
        if (mediaFoundation.Length > 0) return mediaFoundation;
        // Match the legacy WPF provider's pre-MF behavior: the default FlashCap
        // set includes DirectShow, Video for Windows, and any other compiled-in
        // Windows backends that can expose the camera.
        return FilterJpeg(new CaptureDevices().EnumerateDescriptors());
    }

    private static IEnumerable<CaptureDeviceDescriptor> FilterJpeg(IEnumerable<CaptureDeviceDescriptor> devices) =>
        devices.Where(static device => device.Characteristics.Any(static c => c.PixelFormat == PixelFormats.JPEG));
    public ValueTask<ImmutableArray<CaptureMode>> GetModesAsync(CameraId device, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult(Get(device).Characteristics.Select((c, i) => (c, i)).Where(p => p.c.PixelFormat == PixelFormats.JPEG)
                .Select(p => new CaptureMode(p.i.ToString(System.Globalization.CultureInfo.InvariantCulture), p.c.Width, p.c.Height,
                    (int)p.c.FramesPerSecond.Numerator, (int)p.c.FramesPerSecond.Denominator, FrameEncoding.Jpeg, Contracts.PixelFormat.Unknown)).ToImmutableArray());
    }
    private CaptureDeviceDescriptor Get(CameraId id) => id.ProviderId == Id && _devices.TryGetValue(id.DeviceKey, out var d) ? d : throw new InvalidOperationException("相机已失效，请刷新设备。");
    public ValueTask<ICameraSession> OpenAsync(CameraOpenOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var descriptor = Get(options.Device);
            var i = int.Parse(options.ModeId, System.Globalization.CultureInfo.InvariantCulture);
            if (i < 0 || i >= descriptor.Characteristics.Length || descriptor.Characteristics[i].PixelFormat != PixelFormats.JPEG) throw new ArgumentException("不支持的采集模式。", nameof(options));
            var c = descriptor.Characteristics[i];
            ICameraSession session = new CameraSession(descriptor, c, new(options.Device, descriptor.Name, DeviceIdentityKind.BackendId, null, null),
                new(options.ModeId, c.Width, c.Height, (int)c.FramesPerSecond.Numerator, (int)c.FramesPerSecond.Denominator, FrameEncoding.Jpeg, Contracts.PixelFormat.Unknown));
            return ValueTask.FromResult(session);
        }
    }
    private sealed class CameraSession(CaptureDeviceDescriptor descriptor, VideoCharacteristics characteristics, CameraDescriptor device, CaptureMode mode) : ICameraSession
    {
        private CaptureDevice? _capture;
        private long _sequence;
        public string SourceId => device.Id.DeviceKey;
        public SourceState State { get; private set; } = SourceState.Open;
        public CameraDescriptor Device => device;
        public CaptureMode NegotiatedMode => mode;
        public ICameraControls? Controls => null;
        public async ValueTask StartAsync(Guid streamEpoch, IFrameReceiver receiver, CancellationToken cancellationToken)
        {
            State = SourceState.Starting;
            _sequence = 0;
            var layout = new ImageLayout(FrameEncoding.Jpeg, Contracts.PixelFormat.Unknown, mode.Width, mode.Height, [], ColorRange.Unspecified, ColorMatrix.Unspecified);
            _capture = await descriptor.OpenAsync(characteristics, TranscodeFormats.DoNotTranscode, scope =>
            {
                try
                {
                    var bytes = scope.Buffer.ReferImage();
                    var stamp = new FrameStamp(new(streamEpoch, Interlocked.Increment(ref _sequence)), SourceId, mode.Width, mode.Height, Stopwatch.GetTimestamp(), scope.Buffer.Timestamp);
                    receiver.OnFrame(new CapturedFrame(stamp, layout, bytes.AsSpan()));
                }
                catch (Exception ex) { receiver.OnFault(new("CaptureCallback", ex.Message, true)); }
                finally { scope.ReleaseNow(); }
            }, cancellationToken).ConfigureAwait(false);
            await _capture.StartAsync(cancellationToken).ConfigureAwait(false);
            State = SourceState.Running;
        }
        public async ValueTask StopAsync(CancellationToken cancellationToken)
        {
            State = SourceState.Stopping;
            if (_capture is not null) await _capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
            State = SourceState.Stopped;
        }
        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
            finally
            {
                if (_capture is not null) await _capture.DisposeAsync().ConfigureAwait(false);
                _capture = null;
                State = SourceState.Disposed;
            }
        }
    }
}
