using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.Outputs;

internal static class KeyboardText
{
    public static List<string> CollectItems(ScanRecord record) =>
        record.TextLines
            .Select(l => l.Text)
            .Where(s => !string.IsNullOrEmpty(s))
            .Cast<string>()
            .ToList();
}
