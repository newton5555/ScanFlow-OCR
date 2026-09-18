using System.Buffers.Binary;
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
            var jpegLayout = new ImageLayout(FrameEncoding.Jpeg, Contracts.PixelFormat.Unknown, mode.Width, mode.Height, [], ColorRange.Unspecified, ColorMatrix.Unspecified);
            _capture = await descriptor.OpenAsync(characteristics, TranscodeFormats.Auto, scope =>
            {
                try
                {
                    var bytes = scope.Buffer.ReferImage();
                    var stamp = new FrameStamp(new(streamEpoch, Interlocked.Increment(ref _sequence)), SourceId, mode.Width, mode.Height, Stopwatch.GetTimestamp(), scope.Buffer.Timestamp);
                    var image = bytes.AsSpan();
                    if (IsJpeg(image))
                    {
                        receiver.OnFrame(new CapturedFrame(stamp, jpegLayout, image));
                    }
                    else if (TryConvertBitmapToBgra(image, mode.Width, mode.Height, out byte[] bgra, out string error))
                    {
                        var rawLayout = CreateBgraLayout(mode.Width, mode.Height);
                        receiver.OnFrame(new CapturedFrame(stamp, rawLayout, bgra));
                    }
                    else
                    {
                        receiver.OnFault(new("CaptureFormat", error, true));
                    }
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

    private static ImageLayout CreateBgraLayout(int width, int height) =>
        new(FrameEncoding.Raw, Contracts.PixelFormat.Bgra32, width, height,
            [new PlaneLayout(0, checked(width * 4), checked(width * 4), height)],
            ColorRange.Full, ColorMatrix.Unspecified);

    private static bool IsJpeg(ReadOnlySpan<byte> image) =>
        image.Length >= 2 && image[0] == 0xff && image[1] == 0xd8;

    internal static bool TryConvertBitmapToBgra(
        ReadOnlySpan<byte> image,
        int expectedWidth,
        int expectedHeight,
        out byte[] bgra,
        out string error)
    {
        bgra = [];
        error = "";

        const int bitmapFileHeaderSize = 14;
        const int bitmapInfoHeaderMinimumSize = 40;
        if (image.Length < bitmapFileHeaderSize + bitmapInfoHeaderMinimumSize ||
            image[0] != (byte)'B' || image[1] != (byte)'M')
        {
            error = "相机返回的帧既不是 JPEG，也不是 FlashCap BMP。";
            return false;
        }

        uint pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(10, 4));
        uint infoHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(14, 4));
        int width = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(18, 4));
        int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(22, 4));
        ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(26, 2));
        ushort bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(28, 2));
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(30, 4));

        long height = Math.Abs((long)signedHeight);
        if (infoHeaderSize < bitmapInfoHeaderMinimumSize ||
            width < 1 || height < 1 || height > int.MaxValue ||
            width != expectedWidth || height != expectedHeight)
        {
            error = $"相机 BMP 尺寸无效：收到 {width}×{height}，协商为 {expectedWidth}×{expectedHeight}。";
            return false;
        }
        if (planes != 1 || (bitsPerPixel != 24 && bitsPerPixel != 32) || compression != 0)
        {
            error = $"相机 BMP 像素格式不支持：{bitsPerPixel}bpp/compression={compression}。";
            return false;
        }

        long sourceRowBytes = bitsPerPixel == 24
            ? ((long)width * 3 + 3) & ~3L
            : (long)width * 4;
        long sourceBytes = checked(sourceRowBytes * height);
        long sourceEnd = checked((long)pixelOffset + sourceBytes);
        if (pixelOffset < bitmapFileHeaderSize + infoHeaderSize || sourceEnd > image.Length)
        {
            error = "相机 BMP 的像素数据超出帧缓冲区。";
            return false;
        }

        int destinationStride = checked(width * 4);
        bgra = new byte[checked(destinationStride * (int)height)];
        bool bottomUp = signedHeight > 0;
        int sourcePixelBytes = bitsPerPixel / 8;
        for (int y = 0; y < (int)height; y++)
        {
            int sourceY = bottomUp ? (int)height - y - 1 : y;
            var sourceRow = image.Slice(checked((int)(pixelOffset + sourceY * sourceRowBytes)), checked(width * sourcePixelBytes));
            var destinationRow = bgra.AsSpan(y * destinationStride, destinationStride);
            for (int x = 0; x < width; x++)
            {
                int sourceOffset = x * sourcePixelBytes;
                int destinationOffset = x * 4;
                destinationRow[destinationOffset] = sourceRow[sourceOffset];
                destinationRow[destinationOffset + 1] = sourceRow[sourceOffset + 1];
                destinationRow[destinationOffset + 2] = sourceRow[sourceOffset + 2];
                destinationRow[destinationOffset + 3] = 255;
            }
        }

        return true;
    }
}
