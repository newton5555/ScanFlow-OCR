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
