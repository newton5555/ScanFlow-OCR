using Avalonia.Media;
using System.Globalization;
using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.App.Models;

public sealed class ScanResultItem
{
    public int Index { get; init; }
    public string Time { get; init; } = "";
    public string Text { get; init; } = "";
    public string Confidence { get; init; } = "-";
    public Guid EventId { get; init; }
    public Quad Bounds { get; init; }
    public double ReadingAngleDegrees { get; init; }
    public string SourceId { get; init; } = "";

    /// <summary>Accent color shared with this line's box on the preview overlay.</summary>
    public IBrush AccentBrush { get; init; } = Brushes.Transparent;

    /// <summary>Translucent variant of <see cref="AccentBrush"/> for chips and row bars.</summary>
    public IBrush AccentTintBrush { get; init; } = Brushes.Transparent;

    /// <summary>
    /// OCR engine inference time for the frame this line came from, in milliseconds.
    /// Every line accepted from one frame shares the same value: it is the cost of
    /// the whole frame's inference, not of this single line.
    /// </summary>
    public double? EngineMs { get; init; }

    public string EngineDisplay => EngineMs is double ms ? $"{ms:0} ms" : "-";

    public string ConfidenceDisplay =>
        Confidence is "-" or "" ? "" : $"得分: {Confidence}";

    public string BoundsSummary =>
        string.Create(CultureInfo.InvariantCulture,
            $"[({Bounds.P0.X:F0},{Bounds.P0.Y:F0})→({Bounds.P2.X:F0},{Bounds.P2.Y:F0})] {ReadingAngleDegrees:F0}°");

    public string SearchBlob => $"{Text} {Confidence} {Time} {EventId:N} {SourceId}";
}