using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.Outputs;

/// <summary>
/// Linux keystroke sink via self-wrapped <c>/dev/uinput</c>.
/// ASCII (+ Tab/Enter) via EV_KEY. Non-ASCII returns <c>UnicodeUnsupported</c>
/// (document Unicode strategy later: e.g. clipboard paste or IBus).
/// Requires write access to <c>/dev/uinput</c>. Focus/target matching is deferred;
/// phase 1 types once the virtual device is open.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxUinputKeyboardSink : IOutputSink
{
    private const ulong UiSetEvbit = 0x40045564;
    private const ulong UiSetKeybit = 0x40045565;
    private const ulong UiDevSetup = 0x405C5503;
    private const ulong UiDevCreate = 0x5501;
    private const ulong UiDevDestroy = 0x5502;
    private const ushort EvSyn = 0x00;
    private const ushort EvKey = 0x01;
    private const ushort SynReport = 0;
    private const int KeyLeftShift = 42;
    private const int KeyEnter = 28;
    private const int KeyTab = 15;
    private const int ORdwr = 0x0002;

    private static readonly object GlobalGate = new();
    private static int _globalFd = -1;
    private static bool _globalCreated;

    private readonly KeyboardRoute _route;

    public LinuxUinputKeyboardSink(KeyboardRoute route) => _route = route;

    public OutputSinkDescriptor Descriptor => new("keyboard", false, false);

    public ValueTask<DeliveryReceipt> SendAsync(OutputMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
            return ValueTask.FromResult(Fail(message, "LinuxOnly"));

        var items = KeyboardText.CollectItems(message.Record);
        if (items.Count == 0)
            return ValueTask.FromResult(Ok(message, "NoText"));

        try
        {
            EnsureDevice();
            if (_route.SendMode == KeyboardSendMode.Individual)
            {
                foreach (var item in items)
                {
                    var r = TypeAscii(message, item);
                    if (r.Disposition != DeliveryDisposition.LocallyAccepted)
                        return ValueTask.FromResult(r);
                    EmitSuffix();
                }
            }
            else
            {
                var r = TypeAscii(message, string.Join(KeyboardRoute.UnescapeSeparator(_route.Separator), items));
                if (r.Disposition != DeliveryDisposition.LocallyAccepted)
                    return ValueTask.FromResult(r);
                EmitSuffix();
            }
            return ValueTask.FromResult(Ok(message, "InputInserted"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LinuxUinputKeyboardSink] Error: {ex}");
            return ValueTask.FromResult(new DeliveryReceipt(message.Record.EventId, "keyboard", DeliveryDisposition.NotDelivered, "UinputError", ex.Message));
        }
    }

    private DeliveryReceipt TypeAscii(OutputMessage message, string text)
    {
        string normalized = NormalizeToAscii(text);
        foreach (char c in normalized)
        {
            if (c is '\n' or '\r' or '\t') continue;
            if (c > 0x7F || char.IsControl(c))
            {
                string msg = $"字符 U+{(int)c:X4} ('{c}') 暂不支持模拟键盘直接击键输出。";
                Console.Error.WriteLine($"[LinuxUinputKeyboardSink] {msg}");
                return new(message.Record.EventId, "keyboard", DeliveryDisposition.NotDelivered, "UnicodeUnsupported", msg);
            }
        }
        EmitAscii(normalized);
        EmitSuffix();
        return Ok(message, "ok");
    }

    private static string NormalizeToAscii(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\u3000') { sb.Append(' '); continue; }
            if (c == '。') { sb.Append('.'); continue; }
            if (c is >= '\uFF01' and <= '\uFF5E')
            {
                sb.Append((char)(c - 0xFEE0));
                continue;
            }
            if (c is '：') { sb.Append(':'); continue; }
            if (c is '，') { sb.Append(','); continue; }
            if (c is '“' or '”') { sb.Append('"'); continue; }
            if (c is '‘' or '’') { sb.Append('\''); continue; }
            if (c is '（') { sb.Append('('); continue; }
            if (c is '）') { sb.Append(')'); continue; }
            if (c is '【' or '〔') { sb.Append('['); continue; }
            if (c is '】' or '〕') { sb.Append(']'); continue; }
            if (c is '—' or '–') { sb.Append('-'); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private void EmitSuffix()
    {
        int? key = _route.Suffix switch
        {
            KeyboardSuffix.Enter => KeyEnter,
            KeyboardSuffix.Tab => KeyTab,
            _ => null
        };
        if (key is int k)
        {
            EmitKeyPress(k, false);
        }
    }

    private static void EmitAscii(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                // 如果后面紧跟 \n（Windows 换行 CRLF），跳过 \n，避免敲击两次回车
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                EmitKeyPress(KeyEnter, false);
                continue;
            }
            if (c == '\n')
            {
                EmitKeyPress(KeyEnter, false);
                continue;
            }
            if (c == '\t')
            {
                EmitKeyPress(KeyTab, false);
                continue;
            }
            if (!TryMapAscii(c, out int code, out bool shift))
                throw new InvalidOperationException($"No KEY_* mapping for U+{(int)c:X4} ('{c}').");
            EmitKeyPress(code, shift);
        }
    }

    private static void EmitKeyPress(int code, bool shift)
    {
        if (shift)
        {
            EmitKey(KeyLeftShift, 1);
            EmitSyn();
            Thread.Sleep(5);
        }

        EmitKey(code, 1);
        EmitSyn();
        Thread.Sleep(8);

        EmitKey(code, 0);
        EmitSyn();
        Thread.Sleep(5);

        if (shift)
        {
            EmitKey(KeyLeftShift, 0);
            EmitSyn();
            Thread.Sleep(5);
        }
    }

    private static bool TryMapAscii(char c, out int code, out bool shift)
    {
        shift = false;
        if (c is >= 'a' and <= 'z') { code = MapLetter(c); return code != 0; }
        if (c is >= 'A' and <= 'Z') { shift = true; code = MapLetter(char.ToLowerInvariant(c)); return code != 0; }
        if (c is >= '1' and <= '9') { code = 2 + (c - '1'); return true; }
        switch (c)
        {
            case '0': code = 11; return true;
            case ' ': code = 57; return true;
            case '-': code = 12; return true;
            case '=': code = 13; return true;
            case '[': code = 26; return true;
            case ']': code = 27; return true;
            case '\\': code = 43; return true;
            case ';': code = 39; return true;
            case '\'': code = 40; return true;
            case '`': code = 41; return true;
            case ',': code = 51; return true;
            case '.': code = 52; return true;
            case '/': code = 53; return true;
            case '!': shift = true; code = 2; return true;
            case '@': shift = true; code = 3; return true;
            case '#': shift = true; code = 4; return true;
            case '$': shift = true; code = 5; return true;
            case '%': shift = true; code = 6; return true;
            case '^': shift = true; code = 7; return true;
            case '&': shift = true; code = 8; return true;
            case '*': shift = true; code = 9; return true;
            case '(': shift = true; code = 10; return true;
            case ')': shift = true; code = 11; return true;
            case '_': shift = true; code = 12; return true;
            case '+': shift = true; code = 13; return true;
            case '{': shift = true; code = 26; return true;
            case '}': shift = true; code = 27; return true;
            case '|': shift = true; code = 43; return true;
            case ':': shift = true; code = 39; return true;
            case '"': shift = true; code = 40; return true;
            case '~': shift = true; code = 41; return true;
            case '<': shift = true; code = 51; return true;
            case '>': shift = true; code = 52; return true;
            case '?': shift = true; code = 53; return true;
            default: code = 0; return false;
        }
    }

    private static int MapLetter(char lower) => lower switch
    {
        'a' => 30, 'b' => 48, 'c' => 46, 'd' => 32, 'e' => 18, 'f' => 33, 'g' => 34, 'h' => 35,
        'i' => 23, 'j' => 36, 'k' => 37, 'l' => 38, 'm' => 50, 'n' => 49, 'o' => 24, 'p' => 25,
        'q' => 16, 'r' => 19, 's' => 31, 't' => 20, 'u' => 22, 'v' => 47, 'w' => 17, 'x' => 45,
        'y' => 21, 'z' => 44, _ => 0
    };

    private static unsafe void EnsureDevice()
    {
        lock (GlobalGate)
        {
            if (_globalCreated && _globalFd >= 0) return;
            _globalFd = Open("/dev/uinput", ORdwr);
            if (_globalFd < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException($"无法打开 /dev/uinput（errno={err}，需 chmod 666 /dev/uinput 或加入 input 组并配置 udev 规则）。");
            }
            if (Ioctl(_globalFd, UiSetEvbit, EvKey) < 0)
                throw new InvalidOperationException("UI_SET_EVBIT failed.");
            for (int k = 1; k < 256; k++)
                _ = Ioctl(_globalFd, UiSetKeybit, k);

            var setup = new UinputSetup
            {
                Id = new InputId { Bustype = 0x03, Vendor = 0x1234, Product = 0x5678, Version = 1 }
            };
            byte[] nameBytes = Encoding.ASCII.GetBytes("ScanFlowOCR Virtual Keyboard");
            int len = Math.Min(nameBytes.Length, 79);
            for (int i = 0; i < len; i++)
                setup.Name[i] = nameBytes[i];

            if (IoctlSetup(_globalFd, UiDevSetup, ref setup) < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException($"UI_DEV_SETUP failed (errno={err})。");
            }
            if (Ioctl(_globalFd, UiDevCreate, 0) < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException($"UI_DEV_CREATE failed (errno={err})。");
            }
            _globalCreated = true;
            Thread.Sleep(60);
        }
    }

    private static void EmitKey(int code, int value)
    {
        var ev = new InputEvent { Type = EvKey, Code = (ushort)code, Value = value };
        WriteEvent(_globalFd, ref ev);
    }

    private static void EmitSyn()
    {
        var ev = new InputEvent { Type = EvSyn, Code = SynReport, Value = 0 };
        WriteEvent(_globalFd, ref ev);
    }

    public ValueTask DisposeAsync()
    {
        // 保持单例虚拟键盘设备常驻，避免每次消息发送后热拔插注销导致桌面焦点丢失或事件冲刷丢失
        return ValueTask.CompletedTask;
    }

    private static DeliveryReceipt Ok(OutputMessage m, string code) =>
        new(m.Record.EventId, "keyboard", DeliveryDisposition.LocallyAccepted, code, null);
    private static DeliveryReceipt Fail(OutputMessage m, string code) =>
        new(m.Record.EventId, "keyboard", DeliveryDisposition.NotDelivered, code, null);

    private static void WriteEvent(int fd, ref InputEvent ev)
    {
        int size = Marshal.SizeOf<InputEvent>();
        nint ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(ev, ptr, false);
            if (Write(fd, ptr, (ulong)size) != size) throw new IOException("write(input_event) incomplete.");
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeVal { public long TvSec; public long TvUsec; }
    [StructLayout(LayoutKind.Sequential)]
    private struct InputEvent { public TimeVal Time; public ushort Type; public ushort Code; public int Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct InputId { public ushort Bustype; public ushort Vendor; public ushort Product; public ushort Version; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UinputSetup
    {
        public InputId Id;
        public fixed byte Name[80];
        public uint FfEffectsMax;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)] private static extern int Open(string pathname, int flags);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int Close(int fd);
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static extern int Ioctl(int fd, ulong request, int arg);
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static extern int IoctlSetup(int fd, ulong request, ref UinputSetup setup);
    [DllImport("libc", EntryPoint = "write", SetLastError = true)] private static extern long Write(int fd, nint buf, ulong count);
}

/// <summary>Factory for the platform keyboard sink.</summary>
public static class KeyboardOutputSink
{
    public static IOutputSink Create(KeyboardRoute route)
    {
        string? error = route.Validate();
        if (error is not null) throw new ArgumentException(error, nameof(route));
        if (OperatingSystem.IsWindows()) return new WindowsKeyboardOutputSink(route);
        if (OperatingSystem.IsLinux()) return new LinuxUinputKeyboardSink(route);
        throw new PlatformNotSupportedException("Keyboard output supports Windows SendInput and Linux /dev/uinput only.");
    }
}
