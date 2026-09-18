using System.Buffers.Binary;
using System.Diagnostics;
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

Console.WriteLine("=== ScanFlow-OCR smoke ===");

var stamp = new FrameStamp(new FrameId(Guid.NewGuid(), 1), "test", 64, 64, Stopwatch.GetTimestamp(), null);
var allocator = new ImageAllocator(10_000);
using (var lease = allocator.Allocate(stamp, JpegDecoder.GrayLayout(64, 64), 4096, ImageTransform.Identity))
{
    Check(lease.Input.Layout.Width == 64, "lease width");
    using var retained = lease.Retain();
    Check(allocator.LiveBytes == 4096, "live bytes with retain");
}
Check(allocator.LiveBytes == 0, "live bytes after dispose");

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

