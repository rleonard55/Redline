using System.Runtime.InteropServices;
using System.Windows.Automation;
using Redline.Core.Models;
using Redline.Windows.Adapters;
using Redline.Windows.Automation;
using Xunit;

namespace Redline.Windows.Tests;

/// <summary>
/// A classic multiline Win32 Edit with a large font, driven through the real adapter. UIA's Win32 proxy
/// reports the em height there (37 px for a 37 px font) instead of the line height, which put underlines
/// through the letters; the adapter grows the rectangles to the line height.
/// </summary>
public sealed class Win32EditGeometryTests : IDisposable
{
    private const string Text = "This is an tset\r\nsecond line";
    private const int FontPixels = 37; // 28 pt at 96 dpi
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly Thread _uiThread;
    private readonly UiaDispatcher _uia = new();
    private uint _threadId;
    private IntPtr _edit;
    private IntPtr _font;

    public Win32EditGeometryTests()
    {
        using var ready = new ManualResetEventSlim();
        _uiThread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _edit = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, "Edit", Text,
                WS_POPUP | WS_BORDER | ES_MULTILINE, 60, 60, 600, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            _font = CreateFontW(-FontPixels, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            SendMessageW(_edit, WM_SETFONT, _font, (IntPtr)1);
            ShowWindow(_edit, SW_SHOWNOACTIVATE);
            ready.Set();
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            DestroyWindow(_edit);
            DeleteObject(_font);
        });
        _uiThread.IsBackground = true;
        _uiThread.Start();
        Assert.True(ready.Wait(Timeout));
        Assert.NotEqual(IntPtr.Zero, _edit);
    }

    [Fact]
    public async Task MultilineEdit_RectanglesSpanTheWholeLine()
    {
        var element = await _uia.InvokeAsync(() => UiaTestHelpers.FromHandle(_edit));
        var info = await _uia.InvokeAsync(() => ElementInfo.Capture(element));
        Assert.Equal("Win32", info.FrameworkId);
        using var adapter = await _uia.InvokeAsync(() => new GenericUiaAdapter(_uia, element, info));

        // Ground truth from the control itself: where line 1 and line 2 start.
        int line1Top = PosFromChar(0).Y, line2Top = PosFromChar(Text.IndexOf("second", StringComparison.Ordinal)).Y;
        int lineHeight = line2Top - line1Top;
        Assert.True(lineHeight > FontPixels, $"line height {lineHeight}");

        var bounds = await adapter.GetBoundsAsync(new TextRange(Text.IndexOf("tset", StringComparison.Ordinal), 4));
        var word = Assert.Single(bounds);
        Assert.Equal(lineHeight, word.Height);

        var origin = new POINT();
        ClientToScreen(_edit, ref origin);
        Assert.Equal(origin.Y + line1Top, word.Top);
    }

    private POINT PosFromChar(int index)
    {
        long r = SendMessageW(_edit, EM_POSFROMCHAR, index, IntPtr.Zero).ToInt64();
        return new POINT { X = (short)(r & 0xFFFF), Y = (short)((r >> 16) & 0xFFFF) };
    }

    public void Dispose()
    {
        PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _uiThread.Join(Timeout);
        _uia.Dispose();
    }

    private const uint WS_POPUP = 0x80000000, WS_BORDER = 0x00800000, ES_MULTILINE = 0x0004;
    private const uint WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_SETFONT = 0x0030, WM_QUIT = 0x0012, EM_POSFROMCHAR = 0x00D6;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, int wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
