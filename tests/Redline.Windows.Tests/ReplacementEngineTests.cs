using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Redline.Core.Corrections;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Redline.Windows.Adapters;
using Redline.Windows.Automation;
using Redline.Windows.Corrections;
using Xunit;

namespace Redline.Windows.Tests;

/// <summary>
/// Runs only when REDLINE_INTERACTIVE_TESTS=1: these tests take keyboard focus and send real
/// keystrokes (to their own window only — the engine verifies focus first) and use the clipboard.
/// </summary>
public sealed class InteractiveFactAttribute : FactAttribute
{
    public InteractiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REDLINE_INTERACTIVE_TESTS") != "1")
            Skip = "Interactive: set REDLINE_INTERACTIVE_TESTS=1 (takes focus, types, uses the clipboard).";
    }
}

/// <summary>Drives the real ReplacementEngine against a WPF TextBox through real UIA and SendInput.</summary>
[Collection("Interactive")]
public sealed class ReplacementEngineTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly Thread _uiThread;
    private readonly UiaDispatcher _uia = new();
    private readonly DocumentState _document = new();
    private Dispatcher _wpf = null!;
    private TextBox _textBox = null!;
    private Window _window = null!;
    private string? _userClipboardText;

    public ReplacementEngineTests()
    {
        using var ready = new ManualResetEventSlim();
        _uiThread = new Thread(() =>
        {
            _textBox = new TextBox { FontSize = 16, AcceptsReturn = true };
            _window = new Window { Width = 500, Height = 160, Left = 60, Top = 60, Title = "Redline engine test", Content = _textBox };
            _window.Show();
            _wpf = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        });
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.IsBackground = true;
        _uiThread.Start();
        Assert.True(ready.Wait(Timeout));

        if (Environment.GetEnvironmentVariable("REDLINE_INTERACTIVE_TESTS") == "1")
            _userClipboardText = _wpf.Invoke(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
    }

    /// <summary>Puts <paramref name="text"/> in the box, focuses it, and returns an adapter plus a current snapshot.</summary>
    private async Task<(ITextSurfaceAdapter Adapter, TextSnapshot Snapshot)> PrepareAsync(string text, int maxLength = 0)
    {
        await _wpf.InvokeAsync(() =>
        {
            _textBox.MaxLength = maxLength;
            _textBox.Text = text;
            _textBox.CaretIndex = 0;
        });
        BringToFront(await _wpf.InvokeAsync(() => new WindowInteropHelper(_window).Handle));
        await _wpf.InvokeAsync(() => _textBox.Focus());

        var hwnd = await _wpf.InvokeAsync(() => new WindowInteropHelper(_window).Handle);
        var element = await _uia.InvokeAsync(() => AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
        var info = await _uia.InvokeAsync(() => ElementInfo.Capture(element));
        var adapter = await _uia.InvokeAsync(() => (ITextSurfaceAdapter)new GenericUiaAdapter(_uia, element, info));

        _document.Reset(info.SurfaceId);
        var snapshot = _document.Update(info.SurfaceId, (await adapter.ReadTextAsync())!)!.Value.Snapshot;
        return (adapter, snapshot);
    }

    private static TextIssue IssueAt(TextSnapshot snapshot, string word, int occurrence = 0)
    {
        int start = -1;
        for (int i = 0; i <= occurrence; i++)
            start = snapshot.Text.IndexOf(word, start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0);
        return new TextIssue
        {
            StartOffset = start, Length = word.Length, OriginalText = word,
            Category = IssueCategory.Spelling, Message = "test", SnapshotVersion = snapshot.Version,
        };
    }

    private Task<string> BoxText() => _wpf.InvokeAsync(() => _textBox.Text).Task;

    private ReplacementEngine Engine(params ReplacementStrategy[] strategies) =>
        new(_uia, _document, new ReplacementOptions { Strategies = strategies.Length > 0 ? strategies : null });

    [InteractiveFact]
    public async Task SelectAndType_ReplacesExactlyTheFlaggedWord()
    {
        var (adapter, snapshot) = await PrepareAsync("Hello wrold, the wrold is big.");

        var result = await Engine().ApplyAsync(adapter, IssueAt(snapshot, "wrold", occurrence: 1), "world");

        Assert.True(result.Outcome == CorrectionOutcome.Applied, result.ToString());
        Assert.Equal("SelectAndType", result.Method);
        Assert.Equal("Hello wrold, the world is big.", await BoxText());
    }

    [InteractiveFact]
    public async Task MultiWordReplacement_TypesEveryCharacter()
    {
        // Regression: batched Unicode keystrokes after a space arrived as the batch's last character in Notepad.
        var (adapter, snapshot) = await PrepareAsync("I have alot of work.");

        var result = await Engine().ApplyAsync(adapter, IssueAt(snapshot, "alot"), "a lot");

        Assert.True(result.Outcome == CorrectionOutcome.Applied, result.ToString());
        Assert.Equal("I have a lot of work.", await BoxText());
    }

    [InteractiveFact]
    public async Task Deletion_RemovesWordAndOneSpace()
    {
        var (adapter, snapshot) = await PrepareAsync("I saw the the cat.");

        var result = await Engine().ApplyAsync(adapter, IssueAt(snapshot, "the", occurrence: 1), string.Empty);

        Assert.True(result.Outcome == CorrectionOutcome.Applied, result.ToString());
        Assert.Equal("I saw the cat.", await BoxText());
    }

    [InteractiveFact]
    public async Task MultiLineText_SelectsCorrectRangeAfterLineBreaks()
    {
        var (adapter, snapshot) = await PrepareAsync("First line\r\nSecond lnie here\r\nThird");

        var result = await Engine().ApplyAsync(adapter, IssueAt(snapshot, "lnie"), "line");

        Assert.True(result.Outcome == CorrectionOutcome.Applied, result.ToString());
        Assert.Equal("First line\r\nSecond line here\r\nThird", await BoxText());
    }

    [InteractiveFact]
    public async Task StaleIssue_IsRejected_AndNothingChanges()
    {
        var (adapter, snapshot) = await PrepareAsync("Hello wrold");
        var issue = IssueAt(snapshot, "wrold") with { SnapshotVersion = snapshot.Version - 1 };

        var result = await Engine().ApplyAsync(adapter, issue, "world");

        Assert.True(result.Outcome == CorrectionOutcome.Rejected, result.ToString());
        Assert.Equal("Hello wrold", await BoxText());
    }

    [InteractiveFact]
    public async Task TextEditedSinceAnalysis_IsRejected_AndUserEditIsKept()
    {
        var (adapter, snapshot) = await PrepareAsync("Hello wrold");
        await _wpf.InvokeAsync(() => _textBox.AppendText("!")); // the user typed after analysis

        var result = await Engine().ApplyAsync(adapter, IssueAt(snapshot, "wrold"), "world");

        Assert.True(result.Outcome == CorrectionOutcome.Rejected, result.ToString());
        Assert.Equal("Hello wrold!", await BoxText());
    }

    [InteractiveFact]
    public async Task SelectAndPaste_AppliesAndRestoresTheClipboard()
    {
        await _wpf.InvokeAsync(() => Clipboard.SetText("user's clipboard"));
        var (adapter, snapshot) = await PrepareAsync("Hello wrold");

        var result = await Engine(ReplacementStrategy.SelectAndPaste).ApplyAsync(adapter, IssueAt(snapshot, "wrold"), "world");

        Assert.True(result.Outcome == CorrectionOutcome.Applied, result.ToString());
        Assert.Equal("Hello world", await BoxText());
        await Task.Delay(300);
        Assert.Equal("user's clipboard", await _wpf.InvokeAsync(() => Clipboard.GetText()));
    }

    [InteractiveFact]
    public async Task SelectAndPaste_RefusesClipboardItCannotRestore()
    {
        await _wpf.InvokeAsync(() => Clipboard.SetData("Redline.Test.RichFormat", "precious"));
        var (adapter, snapshot) = await PrepareAsync("Hello wrold");

        var result = await Engine(ReplacementStrategy.SelectAndPaste).ApplyAsync(adapter, IssueAt(snapshot, "wrold"), "world");

        Assert.True(result.Outcome == CorrectionOutcome.Rejected, result.ToString());
        Assert.Equal("Hello wrold", await BoxText());
        Assert.True(await _wpf.InvokeAsync(() => Clipboard.ContainsData("Redline.Test.RichFormat")));
    }

    [InteractiveFact]
    public async Task UnexpectedResult_IsUndone()
    {
        // MaxLength truncates the typed replacement, so the result can't match what was expected.
        var (adapter, snapshot) = await PrepareAsync("Hello wrold", maxLength: 11);

        var result = await Engine().ApplyAsync(adapter, IssueAt(snapshot, "wrold"), "worlds");

        Assert.True(result.Outcome == CorrectionOutcome.Reverted, result.ToString());
        Assert.Equal("Hello wrold", await BoxText());
    }

    [Fact]
    public async Task SelectAsync_ReadsBackExactlyTheRequestedText_WithoutFocus()
    {
        await _wpf.InvokeAsync(() => _textBox.Text = "One\r\ntwo three");
        var hwnd = await _wpf.InvokeAsync(() => new WindowInteropHelper(_window).Handle);
        var element = await _uia.InvokeAsync(() => AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
        var info = await _uia.InvokeAsync(() => ElementInfo.Capture(element));
        using var adapter = await _uia.InvokeAsync(() => new GenericUiaAdapter(_uia, element, info));
        var text = (await adapter.ReadTextAsync())!;

        Assert.True(await adapter.SelectAsync(new TextRange(text.IndexOf("three"), 5), text));
        Assert.Equal("three", await _wpf.InvokeAsync(() => _textBox.SelectedText));
        Assert.False(await adapter.SelectAsync(new TextRange(text.IndexOf("three"), 5), text.Replace("three", "THREE")));
    }

    private static void BringToFront(IntPtr hwnd)
    {
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
        AttachThreadInput(me, fg, true);
        try { BringWindowToTop(hwnd); SetForegroundWindow(hwnd); }
        finally { AttachThreadInput(me, fg, false); }
    }

    public void Dispose()
    {
        if (_userClipboardText is not null)
        {
            // Another process (e.g. clipboard history) may hold the clipboard for a moment.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try { _wpf.Invoke(() => Clipboard.SetText(_userClipboardText)); break; }
                catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(50); }
            }
        }
        _wpf.InvokeShutdown();
        _uiThread.Join(2000);
        _uia.Dispose();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
}
