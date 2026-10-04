using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Logs foreground/focus/activation-related WinEvents system-wide for N seconds.
var seconds = int.Parse(args[0]);
var sw = Stopwatch.StartNew();
WinEventDelegate cb = (h, ev, hwnd, obj, child, thread, time) =>
{
    if (ev == 0x8005 && obj != -4) return; // OBJECT_FOCUS: only OBJID_CLIENT-ish noise filter (keep all except caret)
    GetWindowThreadProcessId(hwnd, out uint pid);
    string proc = "?"; try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { }
    var cls = new StringBuilder(128); GetClassName(hwnd, cls, 128);
    var name = ev switch { 0x0003 => "FOREGROUND", 0x8005 => "focus", 0x0016 => "minstart", 0x0017 => "minend", 0x8002 => "show", 0x8003 => "hide", _ => ev.ToString("X") };
    if (ev is 0x8002 or 0x8003 && proc != "Redline") return;
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {name,-10} hwnd={hwnd} {proc}/{cls}");
};
var hooks = new[] { SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, cb, 0, 0, 0), SetWinEventHook(0x8005, 0x8005, IntPtr.Zero, cb, 0, 0, 0), SetWinEventHook(0x8002, 0x8003, IntPtr.Zero, cb, 0, 0, 0) };
while (sw.Elapsed.TotalSeconds < seconds)
{
    while (PeekMessage(out var m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); }
    Thread.Sleep(5);
}
foreach (var h in hooks) UnhookWinEvent(h);

delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
[StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }
partial class Program
{
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h, uint a, uint b, uint r);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
}
