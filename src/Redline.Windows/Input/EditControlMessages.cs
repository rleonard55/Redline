using System.Runtime.InteropServices;
using System.Text;

namespace Redline.Windows.Input;

/// <summary>
/// Edits a classic Win32 Edit or RichEdit control (Notepad, WinForms TextBox/RichTextBox, many older apps) with
/// window messages instead of synthesized keystrokes: <c>EM_REPLACESEL</c> replaces the selection the caller has
/// already made and verified, <c>EM_UNDO</c> takes it back. Windows marshals the string across processes for these
/// system messages. No keyboard input is involved, so security software that blocks injected keystrokes doesn't
/// see any, and the edit lands in the control's undo history like typing does.
/// </summary>
public static class EditControlMessages
{
    private const uint EM_REPLACESEL = 0x00C2;
    private const uint EM_UNDO = 0x00C7;
    private const int GWL_STYLE = -16;
    private const long ES_READONLY = 0x0800;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SMTO_ERRORONEXIT = 0x0020;
    private const uint TimeoutMs = 2000;

    /// <summary>True for the window classes of Edit and RichEdit controls, including WinForms' wrapped names.</summary>
    public static bool IsEditClass(string? className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        // WinForms: "WindowsForms10.EDIT.app.0.2bf8098_r3_ad1", "WindowsForms10.RichEdit20W.app.0...".
        const string winForms = "WindowsForms10.";
        if (className.StartsWith(winForms, StringComparison.Ordinal))
        {
            var rest = className.Substring(winForms.Length);
            int dot = rest.IndexOf('.');
            className = dot >= 0 ? rest.Substring(0, dot) : rest;
        }
        return className.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || className.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase); // RichEdit20W, RICHEDIT50W, RichEditD2DPT
    }

    /// <summary>
    /// True if <paramref name="hwnd"/> is a live, editable Edit/RichEdit window that holds keyboard focus in its
    /// thread (so the selection we just made is the one the message replaces).
    /// </summary>
    public static bool IsFocusedEditWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (!IsEditClass(ClassOf(hwnd))) return false;
        if ((GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & ES_READONLY) != 0) return false;

        uint thread = GetWindowThreadProcessId(hwnd, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) && info.hwndFocus == hwnd;
    }

    /// <summary>True for an Edit/RichEdit window, whatever its focus (to decide whether to offer the strategy).</summary>
    public static bool IsEditWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd) && IsEditClass(ClassOf(hwnd));

    /// <summary>Replaces the control's current selection, undoably. False if the message didn't get through.</summary>
    public static bool ReplaceSelection(IntPtr hwnd, string text) =>
        SendMessageTimeout(hwnd, EM_REPLACESEL, new IntPtr(1), text, SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT, TimeoutMs, out _) != IntPtr.Zero;

    /// <summary>Undoes the control's last edit. False if the message didn't get through or the control refused.</summary>
    public static bool Undo(IntPtr hwnd) =>
        SendMessageTimeout(hwnd, EM_UNDO, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT, TimeoutMs, out var result) != IntPtr.Zero
        && result != IntPtr.Zero;

    private static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
