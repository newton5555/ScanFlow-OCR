using System.Diagnostics;
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

var sink = KeyboardOutputSink.Create(new KeyboardRoute("scanflow-smoke-target"));
Check(sink.Descriptor.Id == "keyboard", "keyboard sink id");
await sink.DisposeAsync();

Console.WriteLine("All smoke checks passed.");
