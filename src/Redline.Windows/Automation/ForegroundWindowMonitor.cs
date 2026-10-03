using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Redline.Windows.Automation;

/// <summary>
/// Raises <see cref="ForegroundChanged"/> when the foreground window changes, via an
/// out-of-context <c>SetWinEventHook</c>. Out-of-context hooks are delivered through the
/// installing thread's message queue, so the monitor owns a dedicated thread that pumps messages.
/// </summary>
/// <remarks>
/// UIA focus events usually cover foreground switches too, but not for every app (some
/// Chromium windows activate without a UIA focus event), so the tracker listens to both.
/// </remarks>
public sealed class ForegroundWindowMonitor : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint WM_QUIT = 0x0012;
    private const uint PM_NOREMOVE = 0x0000;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    // Held in a field for the hook's lifetime: if the GC collected the delegate, Windows would
    // call into freed memory.
    private readonly WinEventDelegate _callback;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private uint _threadId;
    private Exception? _startError;
    private int _disposed;

    public event Action<IntPtr>? ForegroundChanged;

    public ForegroundWindowMonitor()
    {
        _callback = OnWinEvent;
        _thread = new Thread(Run) { Name = "Redline.ForegroundMonitor", IsBackground = true };
    }

    public void Start()
    {
        _thread.Start();
        _started.Wait();
        if (_startError is not null)
            throw new InvalidOperationException("Failed to install foreground hook.", _startError);
    }

    private void Run()
    {
        // Force creation of this thread's message queue before anyone can PostThreadMessage to it.
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        _threadId = GetCurrentThreadId();

        var hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook == IntPtr.Zero)
        {
            _startError = new Win32Exception(Marshal.GetLastWin32Error());
            _started.Set();
            return;
        }

        _started.Set();
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            // Must unhook on the installing thread.
            UnhookWinEvent(hook);
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (eventType != EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero) return;
        try
        {
            ForegroundChanged?.Invoke(hwnd);
        }
        catch
        {
            // Never let an exception unwind into user32.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (Thread.CurrentThread != _thread)
                _thread.Join(1000);
        }
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
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
