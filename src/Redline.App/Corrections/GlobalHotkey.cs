using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Redline.App.Corrections;

/// <summary>
/// A system-wide hotkey via RegisterHotKey on a hidden message window owned by the UI thread.
/// Receiving the hotkey also grants this process the right to activate its own windows, which the
/// suggestion popup relies on.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;
    private const int Id = 0x5244; // arbitrary, unique within this window

    private readonly HwndSource _window;
    private readonly Action _pressed;

    /// <summary>Must be created on the UI thread. Throws if another app already owns the combination.</summary>
    public GlobalHotkey(uint modifiers, uint virtualKey, Action pressed)
    {
        _pressed = pressed;
        _window = new HwndSource(new HwndSourceParameters("Redline hotkey") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);

        if (!RegisterHotKey(_window.Handle, Id, modifiers | MOD_NOREPEAT, virtualKey))
        {
            int error = Marshal.GetLastWin32Error();
            _window.Dispose();
            throw new InvalidOperationException($"Hotkey is already registered by another application (Win32 error {error}).");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            _pressed();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_window.Handle, Id);
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
