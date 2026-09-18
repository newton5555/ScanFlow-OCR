namespace ScanFlowOcr.Outputs;

public enum KeyboardSuffix { None, Enter, Tab }
public enum KeyboardSendMode { Combined, Individual }
public enum KeyboardTargetAction { KeepInQueue, Discard }

public sealed record KeyboardRoute(
    string TargetProcess,
    KeyboardSuffix Suffix = KeyboardSuffix.Enter,
    KeyboardSendMode SendMode = KeyboardSendMode.Combined,
    string Separator = " | ",
    KeyboardTargetAction TargetAction = KeyboardTargetAction.KeepInQueue)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(TargetProcess) ||
            TargetProcess.IndexOfAny(['\\', '/', ':', '*', '?']) >= 0)
            return "键盘模拟需要有效的目标进程名（例如 notepad.exe 或目标窗口类名）。";
        if (!Enum.IsDefined(Suffix))
            return "键盘结束符选项无效。";
        if (!Enum.IsDefined(SendMode))
            return "键盘多码发送方式选项无效。";
        if (!Enum.IsDefined(TargetAction))
            return "目标未就绪行为选项无效。";
        if (SendMode == KeyboardSendMode.Combined && Separator is null)
            return "合并发送分隔符不能为 null。";
        return null;
    }

    public static string UnescapeSeparator(string? separator)
    {
        if (string.IsNullOrEmpty(separator)) return " ";
        return separator
            .Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\\r\\n", "\r\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal);
    }
}
