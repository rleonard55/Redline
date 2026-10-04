using System.Runtime.InteropServices;

namespace Redline.Windows.Automation;

public enum WindowChange
{
    /// <summary>The user started dragging or resizing the window.</summary>
    MoveSizeStart,

    /// <summary>The window moved or resized (fires repeatedly during a drag).</summary>
    Moved,

    /// <summary>A drag or resize finished.</summary>
    MoveSizeEnd,

    Minimized,
    Restored,
}

/// <summary>
/// Reports geometry changes of one top-level window via out-of-context WinEvent hooks scoped to its
/// process (a system-wide EVENT_OBJECT_LOCATIONCHANGE hook would fire for every cursor and caret
/// move on the desktop). Owns a message-pumping thread; <see cref="Track"/> re-targets it.
/// </summary>
public sealed class WindowEventMonitor : IDisposable
{
    private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_APP_RETARGET = 0x8001;
    private const uint PM_NOREMOVE = 0x0000;

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    // Kept alive in a field for as long as the hooks exist.
    private readonly WinEventDelegate _callback;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private readonly List<IntPtr> _hooks = new();
    private uint _threadId;
    private int _disposed;

    // Written by Track on any thread, read on the hook thread.
    private volatile TargetInfo? _pending;
    private TargetInfo? _target;

    private sealed record TargetInfo(uint ProcessId, IntPtr Window);

    public WindowEventMonitor()
    {
        _callback = OnWinEvent;
        _thread = new Thread(Run) { Name = "Redline.WindowEventMonitor", IsBackground = true };
        _thread.Start();
        _started.Wait();
    }

    /// <summary>Raised on the monitor thread. Handlers must not block.</summary>
    public event Action<IntPtr, WindowChange>? Changed;

    /// <summary>Watch <paramref name="window"/> (top-level). Pass IntPtr.Zero to stop watching.</summary>
    public void Track(IntPtr window)
    {
        uint pid = 0;
        if (window != IntPtr.Zero) GetWindowThreadProcessId(window, out pid);
        _pending = new TargetInfo(pid, window);
        PostThreadMessage(_threadId, WM_APP_RETARGET, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE); // create the queue before anyone posts to it
        _threadId = GetCurrentThreadId();
        _started.Set();

        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_APP_RETARGET && msg.hwnd == IntPtr.Zero)
                {
                    Retarget(_pending);
                    continue;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            Unhook(); // hooks must be removed on the thread that installed them
        }
    }

    private void Retarget(TargetInfo? target)
    {
        if (target == _target) return;
        Unhook();
        _target = target;
        if (target is null || target.Window == IntPtr.Zero || target.ProcessId == 0) return;

        foreach (var (min, max) in new[]
                 {
                     (EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND),
                     (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
                     (EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE),
                 })
        {
            // A failed hook only costs responsiveness (the overlay's periodic refresh still runs);
            // throwing here would kill this thread and the process with it.
            var hook = SetWinEventHook(min, max, IntPtr.Zero, _callback, target.ProcessId, 0, WINEVENT_OUTOFCONTEXT);
            if (hook != IntPtr.Zero) _hooks.Add(hook);
        }
    }

    private void Unhook()
    {
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var target = _target;
        if (target is null || hwnd != target.Window || idObject != OBJID_WINDOW || idChild != 0) return;

        WindowChange? change = eventType switch
        {
            EVENT_SYSTEM_MOVESIZESTART => WindowChange.MoveSizeStart,
            EVENT_SYSTEM_MOVESIZEEND => WindowChange.MoveSizeEnd,
            EVENT_SYSTEM_MINIMIZESTART => WindowChange.Minimized,
            EVENT_SYSTEM_MINIMIZEEND => WindowChange.Restored,
            EVENT_OBJECT_LOCATIONCHANGE => WindowChange.Moved,
            _ => null,
        };
        if (change is null) return;

        try { Changed?.Invoke(hwnd, change.Value); }
        catch { /* never unwind into user32 */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread) _thread.Join(1000);
        _started.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventDelegate proc, uint idProcess, uint idThread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
