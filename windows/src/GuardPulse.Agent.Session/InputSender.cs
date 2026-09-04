namespace GuardPulse.Agent.Session;

using System.Runtime.InteropServices;

/// <summary>
/// Injects the blocked-site keyboard actions into the FOREGROUND browser: navigate the
/// active tab to the local block page (Ctrl+L, type URL, Enter) or close the active tab
/// (Ctrl+W). The CALLER must verify the foreground app is the expected browser first
/// (ForegroundHook.LastReportedAppKey) so keystrokes never land in another app.
/// </summary>
internal static class InputSender
{
    private const int InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;
    private const ushort VkControl = 0x11;
    private const ushort VkL = 0x4C;
    private const ushort VkW = 0x57;
    private const ushort VkReturn = 0x0D;

    /// <summary>Navigates the foreground browser tab to <paramref name="url"/>.</summary>
    public static void NavigateForegroundTabTo(string url)
    {
        // Ctrl+L focuses the omnibox with the current URL selected.
        SendCtrl(VkL, down: true);
        Thread.Sleep(60);
        // Type the target URL as unicode events, then commit.
        foreach (var ch in url)
        {
            SendUnicode(ch);
            Thread.Sleep(4);
        }

        Thread.Sleep(80);
        Tap(VkReturn);
        SendCtrl(VkL, down: false);
    }

    /// <summary>Closes the foreground browser tab (Ctrl+W).</summary>
    public static void CloseForegroundTab()
    {
        SendCtrl(VkW, down: true);
        Thread.Sleep(40);
        SendCtrl(VkW, down: false);
    }

    private static void SendCtrl(ushort key, bool down)
    {
        Span<INPUT> inputs = [Key(VkControl, down), Key(key, down)];
        _ = SendInput((uint)inputs.Length, ref MemoryMarshal.GetReference(inputs), Marshal.SizeOf<INPUT>());
    }

    private static void Tap(ushort key)
    {
        Span<INPUT> inputs = [Key(key, down: true), Key(key, down: false)];
        _ = SendInput((uint)inputs.Length, ref MemoryMarshal.GetReference(inputs), Marshal.SizeOf<INPUT>());
    }

    private static void SendUnicode(char ch)
    {
        Span<INPUT> inputs =
        [
            Unicode(ch, down: true),
            Unicode(ch, down: false),
        ];
        _ = SendInput((uint)inputs.Length, ref MemoryMarshal.GetReference(inputs), Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool down) => new()
    {
        type = InputKeyboard,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = down ? 0 : KeyeventfKeyup,
            },
        },
    };

    private static INPUT Unicode(char ch, bool down) => new()
    {
        type = InputKeyboard,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wScan = ch,
                dwFlags = KeyeventfUnicode | (down ? 0 : KeyeventfKeyup),
            },
        },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        // The native union is MOUSEINPUT-sized: the struct must reserve its full size
        // or SendInput rejects every call with a cbSize mismatch (silent no-op).
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }
}
