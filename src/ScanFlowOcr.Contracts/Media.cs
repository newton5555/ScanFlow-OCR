using System.Collections.Immutable;

namespace ScanFlowOcr.Contracts;

public enum FrameEncoding { Raw, Jpeg }
public enum PixelFormat { Unknown, Gray8, Rgb24, Bgr24, Bgra32, Rgba32, Yuy2, Uyvy, Nv12 }
public enum ColorRange { Unspecified, Full, Limited }
public enum ColorMatrix { Unspecified, Bt601, Bt709, Bt2020 }

// Sequence is monotonic within one epoch. Restart/reconnect creates a new epoch.
public readonly record struct FrameId(Guid StreamEpoch, long Sequence);
public readonly record struct AnalysisId(FrameId Frame, long ProfileRevision);

// ReceivedTimestamp uses Stopwatch.GetTimestamp(), NOT wall-clock ticks.
// DeviceTimestamp is relative to the source clock and is not comparable across sources.
public readonly record struct FrameStamp(
    FrameId Id, string SourceId, int SourceWidth, int SourceHeight,
    long ReceivedTimestamp, TimeSpan? DeviceTimestamp);

// Offset addresses the first visual row. Signed stride allows bottom-up input.
// For every row, Offset + row * StrideBytes .. + RowBytes must be within Buffer.
public readonly record struct PlaneLayout(
    int Offset, int StrideBytes, int RowBytes, int Height);

// JPEG has PixelFormat.Unknown and no planes. Raw images require valid planes.
// Layout can be reused for frames with the same negotiated format.
public sealed record ImageLayout(
    FrameEncoding Encoding, PixelFormat PixelFormat, int Width, int Height,
    ImmutableArray<PlaneLayout> Planes, ColorRange Range, ColorMatrix Matrix);

public readonly record struct Point2(double X, double Y);
public readonly record struct Rect2(double X, double Y, double Width, double Height);
public readonly record struct Quad(Point2 P0, Point2 P1, Point2 P2, Point2 P3);

// Mirrors Sdcb.SimdPaddleOCR 1.4.1 PPOCRCrop / Warp (commit 6fda1085):
// perspective crop, clockwise vertical rotation, then the actual CLS flip.
// Recheck this geometry when upgrading that dependency.
public static class QuadReadingAxis
{
    public static Point2 Center(in Quad q) =>
        new((q.P0.X + q.P1.X + q.P2.X + q.P3.X) * 0.25,
            (q.P0.Y + q.P1.Y + q.P2.Y + q.P3.Y) * 0.25);

    /// <summary>Reading direction at the box center, +X = 0, +Y = 90 (screen-clockwise).</summary>
    public static double Degrees(in Quad q, int appliedRotationDegrees = 0)
    {
        double width = Math.Max(1, Math.Floor(Math.Max(Distance(q.P0, q.P1), Distance(q.P2, q.P3))));
        double height = Math.Max(1, Math.Floor(Math.Max(Distance(q.P0, q.P3), Distance(q.P1, q.P2))));
        bool rotateVertical = height >= width * 1.5;

        double dx1 = q.P1.X - q.P2.X, dx2 = q.P3.X - q.P2.X;
        double dy1 = q.P1.Y - q.P2.Y, dy2 = q.P3.Y - q.P2.Y;
        double dx3 = q.P0.X - q.P1.X + q.P2.X - q.P3.X;
        double dy3 = q.P0.Y - q.P1.Y + q.P2.Y - q.P3.Y;
        double g = 0, h = 0;
        if (Math.Abs(dx3) > 1e-12 || Math.Abs(dy3) > 1e-12)
        {
            double denominator = dx1 * dy2 - dx2 * dy1;
            g = (dx3 * dy2 - dx2 * dy3) / denominator;
            h = (dx1 * dy3 - dx3 * dy1) / denominator;
        }
        double a = q.P1.X - q.P0.X + g * q.P1.X;
        double b = q.P3.X - q.P0.X + h * q.P3.X;
        double d = q.P1.Y - q.P0.Y + g * q.P1.Y;
        double e = q.P3.Y - q.P0.Y + h * q.P3.Y;
        var center = Center(q);
        // Homography derivative at the displayed center; its common positive
        // denominator does not affect the angle. Clockwise crop rotation maps
        // recognition +X back to crop -V, not +V.
        double dx = rotateVertical ? h * center.X - b : a - g * center.X;
        double dy = rotateVertical ? h * center.Y - e : d - g * center.Y;
        double deg = Math.Atan2(dy, dx) * (180.0 / Math.PI);
        if (appliedRotationDegrees is 180 or -180)
            deg += 180.0;
        // Normalize to (-180, 180]
        deg = ((deg + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        if (deg <= -180.0) deg += 360.0;
        return deg;
    }

    private static double Distance(Point2 a, Point2 b) =>
        Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
}

// Column-vector convention: source = H * [inputX, inputY, 1], then divide by w.
// A data contract only; mapping and validation belong to ScanFlowOcr.Runtime.
public readonly record struct ImageTransform(
    double M11, double M12, double M13,
    double M21, double M22, double M23,
    double M31, double M32, double M33)
{
    public static ImageTransform Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);
}

// Borrowed only until IFrameReceiver.OnFrame returns. Never enqueue this view.
public readonly ref struct CapturedFrame
{
    public FrameStamp Stamp { get; }
    public ImageLayout Layout { get; }
    public ReadOnlySpan<byte> Buffer { get; }

    public CapturedFrame(FrameStamp stamp, ImageLayout layout, ReadOnlySpan<byte> buffer)
    {
        Stamp = stamp;
        Layout = layout;
        Buffer = buffer;
    }
}

// This memory is borrowed. The caller owns its lease until the REAL operation ends.
// Engines accept Raw input only; JPEG decompression belongs to the fixed imaging module.
public readonly record struct ImageInput(
    FrameStamp Stamp, ImageLayout Layout, ReadOnlyMemory<byte> Buffer,
    ImageTransform ImageToSource);

// A distinct reference-counted handle per consumer; Dispose is idempotent per handle.
// Retain does not copy pixels. Last Dispose returns the buffer to its bounded owner.
// Input and previously fetched Memory views must not be used after this lease is disposed.
public interface IImageLease : IDisposable
{
    ImageInput Input { get; }
    IImageLease Retain();
}
