using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using ScanFlowOcr.Capture.FlashCap;
using ScanFlowOcr.Contracts;
using ScanFlowOcr.Imaging;
using ScanFlowOcr.Outputs;
using ScanFlowOcr.Ocr.SimdPaddle;

static void Check(bool ok, string name)
{
    if (!ok) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("OK  " + name);
}

if (args.Contains("--keyboard-live", StringComparer.Ordinal))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows SendInput test only.");
    string marker = "ScanFlow-OCR 键盘输出验收 " + Guid.NewGuid().ToString("N")[..8];
    var frame = new FrameStamp(new FrameId(Guid.NewGuid(), 1), "keyboard-live", 1, 1, Stopwatch.GetTimestamp(), null);
    var analysis = new AnalysisId(frame.Id, 1);
    var line = new OcrLine(marker, default, 1, null, []);
    var record = new ScanRecord(Guid.NewGuid(), analysis, frame, DateTimeOffset.UtcNow,
        [line], ImmutableDictionary<string, string>.Empty);
    await using var keyboard = KeyboardOutputSink.Create(new KeyboardRoute("notepad.exe"));
    var receipt = await keyboard.SendAsync(new OutputMessage(record, "text/plain", [], 1), CancellationToken.None);
    Console.WriteLine($"Keyboard live receipt: {receipt.Disposition} / {receipt.Code} / {marker}");
    Check(receipt.Disposition == DeliveryDisposition.LocallyAccepted && receipt.Code == "InputInserted",
        "keyboard SendInput accepted by foreground Notepad");
    return;
}

Console.WriteLine("=== ScanFlow-OCR smoke ===");

// DET quad reading axis (geometry + optional CLS 180 flip)
{
    var horiz = new Quad(new(0, 0), new(100, 0), new(100, 20), new(0, 20));
    Check(Math.Abs(QuadReadingAxis.Degrees(horiz) - 0) < 0.01, "quad reading axis horizontal");
    Check(Math.Abs(QuadReadingAxis.Degrees(horiz, 180) - 180) < 0.01 || Math.Abs(QuadReadingAxis.Degrees(horiz, 180) + 180) < 0.01,
        "quad reading axis horizontal + CLS 180");
    var vert = new Quad(new(0, 0), new(20, 0), new(20, 100), new(0, 100));
    double vertDeg = QuadReadingAxis.Degrees(vert);
    Check(Math.Abs(vertDeg - 90) < 0.01, "quad reading axis vertical ~90");
    var c = QuadReadingAxis.Center(horiz);
    Check(Math.Abs(c.X - 50) < 0.01 && Math.Abs(c.Y - 10) < 0.01, "quad center");
}


var stamp = new FrameStamp(new FrameId(Guid.NewGuid(), 1), "test", 64, 64, Stopwatch.GetTimestamp(), null);
var allocator = new ImageAllocator(10_000);
using (var lease = allocator.Allocate(stamp, JpegDecoder.GrayLayout(64, 64), 4096, ImageTransform.Identity))
{
    Check(lease.Input.Layout.Width == 64, "lease width");
    using var retained = lease.Retain();
    Check(allocator.LiveBytes == 4096, "live bytes with retain");
}
Check(allocator.LiveBytes == 0, "live bytes after dispose");
Check(allocator.CommittedBytes == 4096 && allocator.IdleBytes == 4096,
    "allocator tracks idle committed bytes");
{
    using var reused = allocator.Allocate(stamp, JpegDecoder.GrayLayout(64, 64), 4096, ImageTransform.Identity);
    Check(allocator.LiveBytes == 4096 && allocator.CommittedBytes == 4096,
        "allocator reuses an exact-size native block");
}
allocator.TrimExcess();
Check(allocator.CommittedBytes == 0 && allocator.IdleBytes == 0,
    "allocator trims idle native blocks");
{
    // Nearby JPEG-like sizes must share one power-of-two capacity bucket.
    using (var first = allocator.Allocate(stamp, JpegDecoder.GrayLayout(64, 64), 5000, ImageTransform.Identity))
    {
        Check(first.WritableBuffer.Length == 5000, "lease exposes requested length only");
        Check(allocator.CommittedBytes == 8192, "5000 rounds to 8KiB size class");
    }
    Check(allocator.IdleBytes == 8192, "idle tracks size-class capacity");
    using (var second = allocator.Allocate(stamp, JpegDecoder.GrayLayout(64, 64), 6000, ImageTransform.Identity))
    {
        Check(second.WritableBuffer.Length == 6000, "reuse lease still exposes caller length");
        Check(allocator.LiveBytes == 6000 && allocator.CommittedBytes == 8192,
            "allocator reuses size-class block across 5000/6000");
    }
    allocator.TrimExcess();
    Check(allocator.CommittedBytes == 0 && allocator.IdleBytes == 0,
        "TrimExcess clears size-class idle blocks");
}
{
    using var bounded = new ImageAllocator(4096);
    using (var held = bounded.Allocate(stamp, JpegDecoder.GrayLayout(64, 64), 4096, ImageTransform.Identity)) { }
    bool rejectedWhileIdle = false;
    try
    {
        using var oversized = bounded.Allocate(stamp, JpegDecoder.GrayLayout(64, 64), 1, ImageTransform.Identity);
    }
    catch (InvalidOperationException) { rejectedWhileIdle = true; }
    Check(rejectedWhileIdle, "allocator limit includes idle blocks");
    bounded.Dispose();
}
{
    var original = allocator.Allocate(stamp, JpegDecoder.GrayLayout(64, 64), 4096, ImageTransform.Identity);
    original.WritableBuffer.Span.Fill(123);
    using var survivor = original.Retain();
    original.Dispose();
    original.Dispose();
    Check(survivor.Input.Buffer.Span[4095] == 123 && allocator.LiveBytes == 4096,
        "native pixels survive owner disposal while retained");
}
Check(allocator.LiveBytes == 0, "native pixels released after final reference");
{
    using var source = allocator.Allocate(stamp, JpegDecoder.BgrLayout(8, 8), 192, ImageTransform.Identity);
    source.WritableBuffer.Span.Fill(42);
    var cropMethod = typeof(ScanFlowOcr.Runtime.ScanSession).GetMethod("CropBgrView",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var view = (ImageInput)cropMethod.Invoke(null, [source.Input, new Rect2(2, 3, 4, 2)])!;
    Check(view.Layout.Planes[0].Offset == 78 && view.Layout.Planes[0].StrideBytes == 24,
        "ROI uses original stride and offset");
    Check(allocator.LiveBytes == 192 && view.ImageToSource.M13 == 2 && view.ImageToSource.M23 == 3,
        "ROI view allocates no pixel copy and preserves source position");
    source.WritableBuffer.Span[78] = 99;
    Check(view.Buffer.Span[view.Layout.Planes[0].Offset] == 99, "ROI shares retained source pixels");
}

{
    const int width = 2;
    const int height = 2;
    const int pixelOffset = 54;
    const int sourceStride = 8;
    byte[] bmp = new byte[pixelOffset + sourceStride * height];
    bmp[0] = (byte)'B';
    bmp[1] = (byte)'M';
    BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2, 4), (uint)bmp.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10, 4), pixelOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14, 4), 40);
    BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), width);
    BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), height);
    BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26, 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28, 2), 24);

    // Positive BMP height is bottom-up. The first source row is blue/white;
    // the second source row is red/green and must become the visual top row.
    new byte[] { 255, 0, 0, 255, 255, 255, 0, 0 }.AsSpan().CopyTo(bmp.AsSpan(pixelOffset, sourceStride));
    new byte[] { 0, 0, 255, 0, 255, 0, 0, 0 }.AsSpan().CopyTo(bmp.AsSpan(pixelOffset + sourceStride, sourceStride));
    Check(FlashCapProvider.TryConvertBitmapToBgra(bmp, width, height, out byte[] bgra, out _), "camera BMP conversion");
    Check(bgra.AsSpan().SequenceEqual(new byte[] {
        0, 0, 255, 255, 0, 255, 0, 255,
        255, 0, 0, 255, 255, 255, 255, 255
    }.AsSpan()), "camera BMP orientation and BGRA");
}

Check(new KeyboardRoute("notepad.exe").Validate() is null, "keyboard route ok");
Check(new KeyboardRoute("bad/name").Validate() is not null, "keyboard route rejects path chars");
Check(KeyboardRoute.UnescapeSeparator("\\t") == "\t", "unescape tab");

{
    byte[] bgr = new byte[8 * 8 * 3];
    for (int i = 0; i < bgr.Length; i += 3) { bgr[i] = 0; bgr[i + 1] = 0; bgr[i + 2] = 255; }
    byte[] jpeg = StillImages.EncodeJpegFromBgr(bgr, 8, 8, 90);
    using var ms = new MemoryStream(jpeg);
    var decoded = StillImages.DecodeBgra(ms);
    Check(decoded.Width == 8 && decoded.Height == 8, "still jpeg decode size");
}

Check(SimdPaddleOcrMetadata.Parameters.Length > 0, "simd paddle parameters");
Check(SimdPaddleOcrMetadata.ProviderId == "SimdPaddle", "provider id");


Check(MqttRoute.Default.Validate() is null, "mqtt default route ok");
Check(new MqttRoute("bad host", 1883, false, "id", "t", 1, null, null).Validate() is not null, "mqtt rejects whitespace broker");
Check(new MqttRoute("localhost", 1883, false, "id", "a/+/b", 1, null, null).Validate() is not null, "mqtt rejects wildcard topic");
Check(new TcpRoute("127.0.0.1", 9100, false).Validate() is null, "tcp route ok");
Check(new TcpRoute("host", 0, false).Validate() is not null, "tcp rejects bad port");

{
    string dir = Path.Combine(Path.GetTempPath(), "scanflow-ocr-smoke-" + Guid.NewGuid().ToString("N"));
    await using var coordinator = new OutputCoordinator(dir);
    coordinator.Configure(true, MqttRoute.Default, 100);
    var status = coordinator.GetStatus();
    Check(status.Enabled, "coordinator mqtt enabled");
    Check(status.Routes.Length == 1 && status.Routes[0].SinkId == "mqtt", "coordinator mqtt route");
    coordinator.Configure([]);
    Check(!coordinator.GetStatus().Enabled, "coordinator disabled");

    var tcp = new TcpRoute("127.0.0.1", 9, false);
    coordinator.Configure([new OutputRouteProfile("tcp", true, 10, 1024 * 1024,
        System.Text.Json.JsonSerializer.SerializeToElement(tcp))]);
    Check(coordinator.GetStatus().Routes[0].SinkId == "tcp", "coordinator tcp route");
    coordinator.Configure([]);
}

var sink = KeyboardOutputSink.Create(new KeyboardRoute("scanflow-smoke-target"));
Check(sink.Descriptor.Id == "keyboard", "keyboard sink id");
await sink.DisposeAsync();


Check(Enum.IsDefined(ScanFlowOcr.Contracts.DedupeMode.Session), "dedupe Session");
Check(Enum.IsDefined(ScanFlowOcr.Contracts.DedupeMode.Cooldown), "dedupe Cooldown");
Check(typeof(ScanFlowOcr.Runtime.ScanSession).IsClass, "ScanSession type present");

{
    string nativeJpeg = Path.GetFullPath(OperatingSystem.IsWindows()
        ? "native/win-x64/turbojpeg.dll" : "native/linux-x64/libturbojpeg.so");
    if (File.Exists(nativeJpeg))
    {
        using (var decoder = new JpegDecoder(nativeJpeg))
        {
            var testAllocator = new ImageAllocator(64 * 64 * 4);
            byte[] scaledJpeg = StillImages.EncodeJpegFromBgr(new byte[64 * 64 * 3], 64, 64, 90);
            var jpegInput = new ImageInput(stamp,
                new ImageLayout(FrameEncoding.Jpeg, PixelFormat.Unknown, 64, 64, [], ColorRange.Full, ColorMatrix.Unspecified),
                scaledJpeg, ImageTransform.Identity);
            using (var preview = decoder.DecodePreview(jpegInput, testAllocator, 16, 16))
            {
                Check(preview.Input.Layout.Width == 16 && preview.Input.Layout.Height == 16,
                    "JPEG preview decoded natively at quarter resolution");
                Check(testAllocator.LiveBytes == 16 * 16 * 4,
                    "scaled JPEG preview never allocates full pixel frame");
                Check(preview.Input.ImageToSource.M11 == 4, "scaled preview maps to source coordinates");
            }
            Check(testAllocator.LiveBytes == 0, "scaled preview memory released");
        }
        byte[] pixels = new byte[8 * 8 * 3];
        byte[] jpeg = StillImages.EncodeJpegFromBgr(pixels, 8, 8, 90);
        var cameraId = new CameraId("test", "jpeg");
        var mode = new CaptureMode("jpeg", 8, 8, 1, 1, FrameEncoding.Jpeg, PixelFormat.Unknown);
        var profile = new SessionProfile(1, new CameraSourceProfile(new CameraOpenOptions(cameraId, mode.ModeId)),
            WorkflowMode.OcrOnly,
            new OcrStageProfile("probe", new OcrSettings([], OcrLayout.TextBlock,
                JsonSerializer.SerializeToElement(new { })), null),
            new SchedulingProfile(1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2), 1024 * 1024, 4 * 1024 * 1024),
            new PreviewProfile(false, 1, 0, 0),
            new DedupeProfile(DedupeMode.Session, TimeSpan.FromSeconds(1), 10), []);
        await using var session = new ScanFlowOcr.Runtime.ScanSession(
            new ProbeCameraProvider(cameraId, mode, jpeg), new ProbeOcrFactory(), nativeJpeg, profile);
        await session.StartAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool recognized = false;
        await foreach (var evt in session.ReadEventsAsync(deadline.Token))
        {
            if (evt is ScanRecordReady { Record.TextLines.Length: > 0 })
            {
                recognized = true;
                break;
            }
            if (evt is StateChanged { State: SessionState.Faulted } fault)
                throw new InvalidOperationException("JPEG session fault: " + fault.Reason);
        }
        Check(recognized, "JPEG camera frame reaches OCR as BGR24 without ROI");
        await session.StopAsync(CancellationToken.None);

        var roiProfile = profile with
        {
            Ocr = profile.Ocr! with { Region = new Rect2(2, 3, 4, 2) }
        };
        var jpegRoiFactory = new ProbeOcrFactory();
        await using var jpegRoiSession = new ScanFlowOcr.Runtime.ScanSession(
            new ProbeCameraProvider(cameraId, mode, jpeg), jpegRoiFactory, nativeJpeg, roiProfile);
        await jpegRoiSession.StartAsync(CancellationToken.None);
        using var jpegRoiDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool jpegRoiRecognized = false;
        await foreach (var evt in jpegRoiSession.ReadEventsAsync(jpegRoiDeadline.Token))
        {
            if (evt is ScanRecordReady { Record.TextLines.Length: > 0 })
            {
                jpegRoiRecognized = true;
                break;
            }
            if (evt is StateChanged { State: SessionState.Faulted } fault)
                throw new InvalidOperationException("JPEG ROI session fault: " + fault.Reason);
        }
        Check(jpegRoiRecognized, "JPEG camera ROI reaches OCR");
        var jpegRoiInput = jpegRoiFactory.LastInput ?? throw new InvalidOperationException("JPEG ROI OCR input missing");
        Check(jpegRoiInput.Layout.Width == 4 && jpegRoiInput.Layout.Height == 2,
            "JPEG OCR receives ROI dimensions");
        Check(jpegRoiInput.Layout.Planes[0].Offset == 78 && jpegRoiInput.Layout.Planes[0].StrideBytes == 24 &&
              jpegRoiInput.ImageToSource.M13 == 2 && jpegRoiInput.ImageToSource.M23 == 3,
            "JPEG OCR receives ROI view offset and source mapping");
        await jpegRoiSession.StopAsync(CancellationToken.None);

        var rawMode = mode with { Encoding = FrameEncoding.Raw, PixelFormat = PixelFormat.Bgra32 };
        var rawRoiFactory = new ProbeOcrFactory();
        await using var rawSession = new ScanFlowOcr.Runtime.ScanSession(
            new ProbeCameraProvider(cameraId, rawMode, new byte[8 * 8 * 4]),
            rawRoiFactory, nativeJpeg, roiProfile);
        await rawSession.StartAsync(CancellationToken.None);
        using var rawDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool rawRecognized = false;
        await foreach (var evt in rawSession.ReadEventsAsync(rawDeadline.Token))
        {
            if (evt is ScanRecordReady { Record.TextLines.Length: > 0 })
            {
                rawRecognized = true;
                break;
            }
            if (evt is StateChanged { State: SessionState.Faulted } fault)
                throw new InvalidOperationException("Raw session fault: " + fault.Reason);
        }
        Check(rawRecognized, "BGRA image frame reaches OCR as BGR24 with ROI");
        var rawRoiInput = rawRoiFactory.LastInput ?? throw new InvalidOperationException("BGRA ROI OCR input missing");
        Check(rawRoiInput.Layout.Width == 4 && rawRoiInput.Layout.Height == 2 &&
              rawRoiInput.Layout.Planes[0].Offset == 0 && rawRoiInput.Layout.Planes[0].StrideBytes == 12,
            "BGRA OCR receives packed ROI dimensions");
        Check(rawRoiInput.ImageToSource.M13 == 2 && rawRoiInput.ImageToSource.M23 == 3,
            "BGRA OCR ROI preserves source mapping");
        await rawSession.StopAsync(CancellationToken.None);
    }
}

if (args.Any(static arg => string.Equals(arg, "--camera", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("=== camera probe ===");
    try
    {
        if (TurboJpegNative.TryResolveLibraryPath(out string? turboJpegPath, out string turboJpegError))
            Console.WriteLine($"CAMERA TurboJPEG: {turboJpegPath}");
        else
            Console.WriteLine($"CAMERA TurboJPEG unavailable: {turboJpegError}");

        var provider = new FlashCapProvider();
        var cameras = await provider.EnumerateAsync(CancellationToken.None);
        Console.WriteLine($"CAMERA devices: {cameras.Length}");
        for (int i = 0; i < cameras.Length; i++)
        {
            var camera = cameras[i];
            var modes = await provider.GetModesAsync(camera.Id, CancellationToken.None);
            Console.WriteLine($"CAMERA[{i}] {camera.DisplayName} [{camera.Id.DeviceKey}] ({modes.Length} MJPEG mode(s))");
            foreach (var mode in modes)
                Console.WriteLine($"  MODE {mode.ModeId}: {mode.Width}x{mode.Height} @ {mode.FpsNumerator}/{Math.Max(1, mode.FpsDenominator)}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"CAMERA probe failed: {ex.GetType().Name}: {ex.Message}");
        Environment.ExitCode = 2;
    }
}

Console.WriteLine(Environment.ExitCode == 0
    ? "All smoke checks passed."
    : "Smoke checks passed, but the optional camera probe failed.");

file sealed class ProbeCameraProvider(CameraId id, CaptureMode mode, byte[] pixels) : ICameraProvider
{
    public string Id => id.ProviderId;
    public ValueTask<ImmutableArray<CameraDescriptor>> EnumerateAsync(CancellationToken token) =>
        ValueTask.FromResult(ImmutableArray.Create(new CameraDescriptor(id, "probe", DeviceIdentityKind.BackendId, null, null)));
    public ValueTask<ImmutableArray<CaptureMode>> GetModesAsync(CameraId device, CancellationToken token) =>
        ValueTask.FromResult(ImmutableArray.Create(mode));
    public ValueTask<ICameraSession> OpenAsync(CameraOpenOptions options, CancellationToken token) =>
        ValueTask.FromResult<ICameraSession>(new ProbeCameraSession(id, mode, pixels));
}

file sealed class ProbeCameraSession(CameraId id, CaptureMode mode, byte[] pixels) : ICameraSession
{
    public string SourceId => id.DeviceKey;
    public SourceState State { get; private set; } = SourceState.Open;
    public CameraDescriptor Device => new(id, "probe", DeviceIdentityKind.BackendId, null, null);
    public CaptureMode NegotiatedMode => mode;
    public ICameraControls? Controls => null;
    public ValueTask StartAsync(Guid epoch, IFrameReceiver receiver, CancellationToken token)
    {
        State = SourceState.Running;
        var stamp = new FrameStamp(new FrameId(epoch, 1), SourceId, 8, 8, Stopwatch.GetTimestamp(), null);
        var layout = mode.Encoding == FrameEncoding.Jpeg
            ? new ImageLayout(FrameEncoding.Jpeg, PixelFormat.Unknown, 8, 8, [],
                ColorRange.Unspecified, ColorMatrix.Unspecified)
            : new ImageLayout(FrameEncoding.Raw, PixelFormat.Bgra32, 8, 8,
                [new PlaneLayout(0, 32, 32, 8)], ColorRange.Full, ColorMatrix.Unspecified);
        receiver.OnFrame(new CapturedFrame(stamp, layout, pixels));
        return ValueTask.CompletedTask;
    }
    public ValueTask StopAsync(CancellationToken token) { State = SourceState.Stopped; return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { State = SourceState.Disposed; return ValueTask.CompletedTask; }
}

file sealed class ProbeOcrFactory : IOcrReaderFactory
{
    public ImageInput? LastInput { get; set; }
    public string ProviderId => "probe";
    public ValueTask<IOcrReader> CreateAsync(OcrSettings settings, CancellationToken token) =>
        ValueTask.FromResult<IOcrReader>(new ProbeOcrReader(this));
}

file sealed class ProbeOcrReader(ProbeOcrFactory owner) : IOcrReader
{
    public EngineDescriptor Descriptor => new("probe", "1", [PixelFormat.Bgr24], true, false, false, 1);
    public ValueTask<EngineBatch<OcrLine>> ReadAsync(ImageInput input, RecognitionRequest request, CancellationToken token)
    {
        owner.LastInput = input;
        RawImages.ValidateBgr(input);
        return ValueTask.FromResult(new EngineBatch<OcrLine>(request.Analysis, StageStatus.Completed,
            [new OcrLine("probe", new Quad(new(0, 0), new(8, 0), new(8, 8), new(0, 8)), 1, null, [])],
            TimeSpan.Zero, null));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

