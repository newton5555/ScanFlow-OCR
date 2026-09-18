using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.Outputs;

/// <summary>Windows keystroke sink via user32 <c>SendInput</c> (Unicode).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsKeyboardOutputSink(KeyboardRoute route) : IOutputSink
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;
    private static readonly int InputSize = Marshal.SizeOf<Input>();

    public OutputSinkDescriptor Descriptor => new("keyboard", false, false);

    public ValueTask<DeliveryReceipt> SendAsync(OutputMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return ValueTask.FromResult(Fail(message, DeliveryDisposition.NotDelivered, "WindowsOnly"));

        var foreground = GetForegroundWindow();
        bool isTargetForeground = false;
        if (foreground != nint.Zero && GetWindowThreadProcessId(foreground, out uint processId) != 0)
        {
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                string expected = Path.GetFileNameWithoutExtension(route.TargetProcess);
                isTargetForeground = string.Equals(process.ProcessName, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch { isTargetForeground = false; }
        }

        if (!isTargetForeground)
        {
            if (route.TargetAction == KeyboardTargetAction.Discard)
                return ValueTask.FromResult(Ok(message, "TargetMissingDiscarded"));
            return ValueTask.FromResult(Fail(message, DeliveryDisposition.NotDelivered, "TargetNotForeground"));
        }

        var items = KeyboardText.CollectItems(message.Record);
        if (items.Count == 0)
            return ValueTask.FromResult(Ok(message, "NoText"));

        ushort suffixKey = route.Suffix switch
        {
            KeyboardSuffix.Enter => 0x0D,
            KeyboardSuffix.Tab => 0x09,
            _ => (ushort)0
        };
        var inputs = new List<Input>();

        void AppendText(string text)
        {
            foreach (char c in text)
            {
                inputs.Add(MakeKey(0, c, Unicode));
                inputs.Add(MakeKey(0, c, Unicode | KeyUp));
            }
        }

        void AppendSuffix()
        {
            if (suffixKey == 0) return;
            inputs.Add(MakeKey(suffixKey, 0, 0));
            inputs.Add(MakeKey(suffixKey, 0, KeyUp));
        }

        if (route.SendMode == KeyboardSendMode.Individual)
        {
            foreach (var item in items) { AppendText(item); AppendSuffix(); }
        }
        else
        {
            AppendText(string.Join(KeyboardRoute.UnescapeSeparator(route.Separator), items));
            AppendSuffix();
        }

        if (inputs.Count > 16384)
            return ValueTask.FromResult(Fail(message, DeliveryDisposition.NotDelivered, "TextTooLong"));

        uint inserted = SendInput(checked((uint)inputs.Count), [.. inputs], InputSize);
        if (inserted == inputs.Count) return ValueTask.FromResult(Ok(message, "InputInserted"));
        if (inserted == 0) return ValueTask.FromResult(Fail(message, DeliveryDisposition.NotDelivered, "InputBlocked"));
        return ValueTask.FromResult(new DeliveryReceipt(message.Record.EventId, "keyboard", DeliveryDisposition.Unknown, "PartialInput", null));
    }

    private static DeliveryReceipt Ok(OutputMessage m, string code) =>
        new(m.Record.EventId, "keyboard", DeliveryDisposition.LocallyAccepted, code, null);
    private static DeliveryReceipt Fail(OutputMessage m, DeliveryDisposition d, string code) =>
        new(m.Record.EventId, "keyboard", d, code, null);

    private static Input MakeKey(ushort vk, ushort scan, uint flags) =>
        new() { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, ScanCode = scan, Flags = flags } } };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
