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
    public string SourceId { get; init; } = "";

    public string ConfidenceDisplay =>
        Confidence is "-" or "" ? "" : $"得分: {Confidence}";

    public string BoundsSummary =>
        string.Create(CultureInfo.InvariantCulture,
            $"[({Bounds.P0.X:F0},{Bounds.P0.Y:F0})…({Bounds.P2.X:F0},{Bounds.P2.Y:F0})]");

    public string SearchBlob => $"{Text} {Confidence} {Time} {EventId:N} {SourceId}";
}
