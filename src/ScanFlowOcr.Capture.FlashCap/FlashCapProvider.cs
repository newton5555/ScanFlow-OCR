using System.Collections.Immutable;
using System.Diagnostics;
using FlashCap;
using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.Capture.FlashCap;

public sealed class FlashCapProvider : ICameraProvider
{
    public string Id => "FlashCap";
    private readonly Dictionary<string, CaptureDeviceDescriptor> _devices = [];
    private readonly object _gate = new();
    public ValueTask<ImmutableArray<CameraDescriptor>> EnumerateAsync(CancellationToken cancellationToken) => new(Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _devices.Clear();
            // The tested Windows DirectShow device retains native resources on every
            // reopen. Use FlashCap's MF backend, which shuts down its media source.
            CaptureDevices backend = OperatingSystem.IsWindowsVersionAtLeast(6, 1)
                ? new global::FlashCap.Devices.MediaFoundationDevices()
                : new CaptureDevices(); // Linux → V4L2 via CaptureDevices
            foreach (var device in backend.EnumerateDescriptors())
            {
                // First route accepts MJPEG only; unsupported modes are never advertised.
                if (!device.Characteristics.Any(c => c.PixelFormat == PixelFormats.JPEG)) continue;
                _devices[$"{device.DeviceType}:{device.Identity}"] = device;
            }
            return _devices.Select(p => new CameraDescriptor(new(Id, p.Key), p.Value.Name, DeviceIdentityKind.BackendId, null, null)).ToImmutableArray();
        }
    }, cancellationToken));
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
