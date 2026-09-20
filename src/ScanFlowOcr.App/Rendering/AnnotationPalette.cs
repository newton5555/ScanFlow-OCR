using Avalonia.Media;

namespace ScanFlowOcr.App.Rendering;

/// <summary>
/// Per-OCR-box accent colors. A line's index inside the current frame (or accepted
/// record) selects <c>Colors[index % Length]</c>, so the overlay box, its floating
/// toast card and the right-hand result row all show the same color for the same line.
/// Red is reserved for the reading-axis arrow and therefore never appears here.
/// </summary>
internal static class AnnotationPalette
{
    private static readonly Color[] Colors =
    [
        Color.FromRgb(0x3B, 0x82, 0xF6), // blue
        Color.FromRgb(0x22, 0xC5, 0x5E), // green
        Color.FromRgb(0xEA, 0xB3, 0x08), // yellow
        Color.FromRgb(0xA8, 0x55, 0xF7), // purple
        Color.FromRgb(0x06, 0xB6, 0xD4), // cyan
        Color.FromRgb(0x14, 0xB8, 0xA6), // teal
        Color.FromRgb(0x84, 0xCC, 0x16), // lime
        Color.FromRgb(0x63, 0x66, 0xF1)  // indigo
    ];

    private static readonly IBrush[] StrokeBrushes = new IBrush[Colors.Length];
    private static readonly IBrush[] FillBrushes = new IBrush[Colors.Length];
    private static readonly IBrush[] TintBrushes = new IBrush[Colors.Length];
    private static readonly IBrush[] BorderBrushes = new IBrush[Colors.Length];

    static AnnotationPalette()
    {
        for (int i = 0; i < Colors.Length; i++)
        {
            Color c = Colors[i];
            StrokeBrushes[i] = new SolidColorBrush(c);
            FillBrushes[i] = new SolidColorBrush(Color.FromArgb(0x38, c.R, c.G, c.B));
            TintBrushes[i] = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B));
            BorderBrushes[i] = new SolidColorBrush(Color.FromArgb(0x99, c.R, c.G, c.B));
        }
    }

    public static int Length => Colors.Length;

    /// <summary>Opaque outline: box polygon, toast heading, row accent bar and chip text.</summary>
    public static IBrush Stroke(int index) => StrokeBrushes[Normalize(index)];

    /// <summary>Translucent wash used while a box or card is highlighted.</summary>
    public static IBrush Fill(int index) => FillBrushes[Normalize(index)];

    /// <summary>Subtle background for cards and chips.</summary>
    public static IBrush Tint(int index) => TintBrushes[Normalize(index)];

    /// <summary>Softer outline for card and chip borders.</summary>
    public static IBrush Border(int index) => BorderBrushes[Normalize(index)];

    private static int Normalize(int index)
    {
        int length = Colors.Length;
        return ((index % length) + length) % length;
    }
}
