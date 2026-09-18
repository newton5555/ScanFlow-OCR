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
    private const int UiSetEvbit = unchecked((int)0x40045564);
    private const int UiSetKeybit = unchecked((int)0x40045565);
    private const int UiDevCreate = 0x5501;
    private const int UiDevDestroy = 0x5502;
    private const ushort EvSyn = 0x00;
    private const ushort EvKey = 0x01;
    private const ushort SynReport = 0;
    private const int KeyLeftShift = 42;
    private const int KeyEnter = 28;
    private const int KeyTab = 15;
    private const int ORdwr = 0x0002;

    private readonly KeyboardRoute _route;
    private readonly object _gate = new();
    private int _fd = -1;
    private bool _created;

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
            return ValueTask.FromResult(new DeliveryReceipt(message.Record.EventId, "keyboard", DeliveryDisposition.NotDelivered, "UinputError", ex.Message));
        }
    }

    private DeliveryReceipt TypeAscii(OutputMessage message, string text)
    {
        foreach (char c in text)
        {
            if (c is '\n' or '\r' or '\t') continue;
            if (c > 0x7F || char.IsControl(c))
                return new(message.Record.EventId, "keyboard", DeliveryDisposition.NotDelivered, "UnicodeUnsupported",
                    "Phase 1 Linux uinput types ASCII only.");
        }
        EmitAscii(text);
        return Ok(message, "ok");
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
            EmitKey(k, 1); EmitKey(k, 0); EmitSyn();
        }
    }

    private void EmitAscii(string text)
    {
        foreach (char c in text)
        {
            if (c == '\t') { EmitKey(KeyTab, 1); EmitKey(KeyTab, 0); EmitSyn(); continue; }
            if (c is '\n' or '\r') { EmitKey(KeyEnter, 1); EmitKey(KeyEnter, 0); EmitSyn(); continue; }
            if (!TryMapAscii(c, out int code, out bool shift))
                throw new InvalidOperationException($"No KEY_* mapping for U+{(int)c:X4}.");
            if (shift) { EmitKey(KeyLeftShift, 1); EmitSyn(); }
            EmitKey(code, 1); EmitKey(code, 0);
            if (shift) EmitKey(KeyLeftShift, 0);
            EmitSyn();
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

    private void EnsureDevice()
    {
        lock (_gate)
        {
            if (_created) return;
            _fd = Open("/dev/uinput", ORdwr);
            if (_fd < 0) throw new InvalidOperationException("无法打开 /dev/uinput（需要权限）。");
            if (Ioctl(_fd, UiSetEvbit, EvKey) < 0) throw new InvalidOperationException("UI_SET_EVBIT failed.");
            for (int k = 1; k < 256; k++)
                _ = Ioctl(_fd, UiSetKeybit, k);

            var name = new byte[80];
            Encoding.ASCII.GetBytes("ScanFlowOCR Virtual Keyboard", name);
            var setup = new UinputSetup
            {
                Id = new InputId { Bustype = 0x03, Vendor = 0x1234, Product = 0x5678, Version = 1 },
                Name = name
            };
            WriteSetup(_fd, ref setup);
            if (Ioctl(_fd, UiDevCreate, 0) < 0) throw new InvalidOperationException("UI_DEV_CREATE failed.");
            _created = true;
            Thread.Sleep(50);
        }
    }

    private void EmitKey(int code, int value)
    {
        var ev = new InputEvent { Type = EvKey, Code = (ushort)code, Value = value };
        WriteEvent(_fd, ref ev);
    }

    private void EmitSyn()
    {
        var ev = new InputEvent { Type = EvSyn, Code = SynReport, Value = 0 };
        WriteEvent(_fd, ref ev);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_fd >= 0)
            {
                if (_created) _ = Ioctl(_fd, UiDevDestroy, 0);
                _ = Close(_fd);
                _fd = -1;
                _created = false;
            }
        }
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

    private static void WriteSetup(int fd, ref UinputSetup setup)
    {
        int size = Marshal.SizeOf<UinputSetup>();
        nint ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(setup, ptr, false);
            if (Write(fd, ptr, (ulong)size) != size) throw new IOException("write(uinput_setup) incomplete.");
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
    private struct UinputSetup
    {
        public InputId Id;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 80)]
        public byte[] Name;
        public uint FfEffectsMax;
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)] private static extern int Open(string pathname, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int Close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int Ioctl(int fd, int request, int arg);
    [DllImport("libc", SetLastError = true)] private static extern long Write(int fd, nint buf, ulong count);
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
