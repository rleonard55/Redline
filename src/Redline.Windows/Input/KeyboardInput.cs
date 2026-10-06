using System.Runtime.InteropServices;

namespace Redline.Windows.Input;

/// <summary>
/// Synthesized keyboard input via SendInput. Text is sent as KEYEVENTF_UNICODE events, which
/// bypass keyboard layouts and dead keys. Key chords go out in one SendInput batch so the user's
/// keystrokes can't interleave with them; text is paced (see <see cref="TypeTextAsync"/>).
/// </summary>
/// <remarks>
/// Input goes to whatever has keyboard focus — callers must verify focus immediately before.
/// SendInput can't reach elevated windows from a non-elevated process (UIPI); that shows up as
/// "nothing changed" in the caller's verification.
/// </remarks>
public static class KeyboardInput
{
    public const ushort VK_BACK = 0x08;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;
    public const ushort VK_DELETE = 0x2E;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;
    public const ushort VK_V = 0x56;
    public const ushort VK_Z = 0x5A;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    /// <summary>Marks Redline's own synthesized input (dwExtraInfo) so future hooks can recognize it.</summary>
    private static readonly IntPtr RedlineMarker = new(0x52444C4E); // "RDLN"

    /// <summary>Gap between typed characters; see <see cref="TypeTextAsync"/>.</summary>
    public static readonly TimeSpan DefaultCharacterGap = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Types <paramref name="text"/> one character per SendInput call, <paramref name="gap"/> apart.
    /// Returns false if Windows rejected any event.
    /// </summary>
    /// <remarks>
    /// Not one batch: Windows 11 Notepad defers translating VK_PACKET keystrokes at word boundaries,
    /// and every character after a space in a batch then reads as the batch's last character
    /// ("ab cd" arrives as "ab dd"). Measured: no gap fails 2 of 3 times, 2 ms is already reliable.
    /// </remarks>
    public static async Task<bool> TypeTextAsync(string text, TimeSpan? gap = null, CancellationToken ct = default)
    {
        var delay = gap ?? DefaultCharacterGap;
        for (int i = 0; i < text.Length; i++)
        {
            // Keep a surrogate pair in one call so the halves can't be split.
            int count = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            var inputs = new INPUT[count * 2];
            for (int k = 0; k < count; k++)
            {
                inputs[2 * k] = Key(0, text[i + k], KEYEVENTF_UNICODE);
                inputs[2 * k + 1] = Key(0, text[i + k], KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
            }
            if (!Send(inputs)) return false;
            i += count - 1;
            if (i < text.Length - 1 && delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Gap between the events of a chord; see <see cref="PressAsync"/>.</summary>
    public static readonly TimeSpan ChordGap = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Presses and releases <paramref name="key"/> while holding <paramref name="modifiers"/>: one SendInput call per
    /// event, <see cref="ChordGap"/> apart, with real scan codes. Returns false if Windows rejected any event (the
    /// modifiers are released anyway).
    /// </summary>
    /// <remarks>
    /// Not one batch: Windows 11 Notepad handles queued keys late, and a batched Ctrl+Z sometimes arrived as a bare
    /// "z" (Ctrl already released by the time the Z was handled), which replaced the selection instead of undoing.
    /// </remarks>
    public static async Task<bool> PressAsync(ushort key, ushort[]? modifiers = null, CancellationToken ct = default)
    {
        modifiers ??= [];
        uint extended = key is VK_DELETE ? KEYEVENTF_EXTENDEDKEY : 0;
        int down = 0;
        bool ok = true;
        try
        {
            foreach (var m in modifiers)
            {
                if (!(ok = Send([Key(m, Scan(m), 0)]))) return false;
                down++;
                await Task.Delay(ChordGap, ct).ConfigureAwait(false);
            }
            if (!(ok = Send([Key(key, Scan(key), extended)]))) return false;
            await Task.Delay(ChordGap, ct).ConfigureAwait(false);
            ok = Send([Key(key, Scan(key), extended | KEYEVENTF_KEYUP)]);
            if (down > 0) await Task.Delay(ChordGap, CancellationToken.None).ConfigureAwait(false);
            return ok;
        }
        finally
        {
            // Never leave a modifier down, even when cancelled or rejected midway.
            for (int i = down - 1; i >= 0; i--)
                ok &= Send([Key(modifiers[i], Scan(modifiers[i]), KEYEVENTF_KEYUP)]);
        }
    }

    /// <inheritdoc cref="PressAsync(ushort, ushort[], CancellationToken)"/>
    public static Task<bool> PressAsync(ushort key, ushort modifier, CancellationToken ct = default) =>
        PressAsync(key, [modifier], ct);

    private static ushort Scan(ushort vk) => (ushort)MapVirtualKey(vk, 0 /* MAPVK_VK_TO_VSC */);

    /// <summary>
    /// Waits until Shift, Ctrl, Alt and Win are all physically released, so synthesized text isn't
    /// combined with a modifier the user is still holding (e.g. from the hotkey that opened the popup).
    /// </summary>
    public static async Task<bool> WaitForModifiersReleasedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (!IsDown(VK_SHIFT) && !IsDown(VK_CONTROL) && !IsDown(VK_MENU) && !IsDown(VK_LWIN) && !IsDown(VK_RWIN))
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    private static bool IsDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = RedlineMarker } },
    };

    private static bool Send(INPUT[] inputs) =>
        inputs.Length == 0 || SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // The union must include MOUSEINPUT (its largest member) or sizeof(INPUT) is wrong and SendInput fails.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
