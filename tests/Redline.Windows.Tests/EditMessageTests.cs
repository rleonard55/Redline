using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Redline.Core.Corrections;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Redline.Windows.Adapters;
using Redline.Windows.Automation;
using Redline.Windows.Corrections;
using Redline.Windows.Input;
using Xunit;

namespace Redline.Windows.Tests;

public sealed class EditClassTests
{
    [Theory]
    [InlineData("Edit", true)]
    [InlineData("RichEdit20W", true)]
    [InlineData("RICHEDIT50W", true)]
    [InlineData("RichEditD2DPT", true)] // Windows 11 Notepad
    [InlineData("WindowsForms10.EDIT.app.0.2bf8098_r3_ad1", true)]
    [InlineData("WindowsForms10.RichEdit20W.app.0.2bf8098_r3_ad1", true)]
    [InlineData("WindowsForms10.RICHEDIT50W.app.0.2bf8098_r3_ad1", true)]
    [InlineData("_WwG", false)]
    [InlineData("Chrome_RenderWidgetHostHWND", false)]
    [InlineData("WindowsForms10.BUTTON.app.0.2bf8098_r3_ad1", false)]
    [InlineData("EditBox", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEditClass(string? className, bool expected) =>
        Assert.Equal(expected, EditControlMessages.IsEditClass(className));
}

/// <summary>
/// The edit-message strategy against a WinForms TextBox/RichTextBox in <em>another process</em> (Windows PowerShell),
/// so the string really crosses a process boundary as it does with Notepad. Interactive: takes focus.
/// </summary>
[Collection("Interactive")]
public sealed class EditMessageTests : IDisposable
{
    private readonly UiaDispatcher _uia = new();
    private readonly DocumentState _document = new();
    private Process? _host;

    private async Task<(ITextSurfaceAdapter Adapter, TextSnapshot Snapshot)> HostAsync(string text, bool rich, int maxLength = 0)
    {
        var title = "Redline edit-message test " + Guid.NewGuid().ToString("N");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var script = string.Join("\n",
            "Add-Type -AssemblyName System.Windows.Forms",
            "$f = New-Object System.Windows.Forms.Form",
            $"$f.Text = '{title}'; $f.Width = 500; $f.Height = 200; $f.StartPosition = 'Manual'; $f.Left = 80; $f.Top = 80",
            rich ? "$b = New-Object System.Windows.Forms.RichTextBox" : "$b = New-Object System.Windows.Forms.TextBox; $b.Multiline = $true",
            "$b.Dock = 'Fill'",
            maxLength > 0 ? $"$b.MaxLength = {maxLength}" : "",
            $"$b.Text = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{b64}'))",
            "$f.Controls.Add($b)",
            "$f.Add_Shown({ $f.Activate(); $b.Focus(); $b.SelectionStart = 0 })",
            "[Windows.Forms.Application]::Run($f)");
        _host = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        { UseShellExecute = false, CreateNoWindow = true })!;

        AutomationElement? box = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (box is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            box = await _uia.InvokeAsync(() =>
            {
                var form = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                    new PropertyCondition(AutomationElement.NameProperty, title));
                return form?.FindFirst(TreeScope.Descendants, new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)));
            });
        }
        Assert.NotNull(box);

        var hwnd = new IntPtr(await _uia.InvokeAsync(() => box!.Current.NativeWindowHandle));
        BringToFront(GetAncestor(hwnd, 2 /* GA_ROOT */));
        await _uia.InvokeAsync(() => box!.SetFocus());

        var info = await _uia.InvokeAsync(() => ElementInfo.Capture(box!));
        var adapter = await _uia.InvokeAsync(() => (ITextSurfaceAdapter)new GenericUiaAdapter(_uia, box!, info));
        _document.Reset(info.SurfaceId);
        var snapshot = _document.Update(info.SurfaceId, (await adapter.ReadTextAsync())!)!.Value.Snapshot;
        return (adapter, snapshot);
    }

    private static TextIssue IssueAt(TextSnapshot snapshot, string word)
    {
        int start = snapshot.Text.IndexOf(word, StringComparison.Ordinal);
        Assert.True(start >= 0);
        return new TextIssue
        {
            StartOffset = start, Length = word.Length, OriginalText = word,
            Category = IssueCategory.Spelling, Message = "test", SnapshotVersion = snapshot.Version,
        };
    }

    private ReplacementEngine Engine(params ReplacementStrategy[] strategies) =>
        new(_uia, _document, new ReplacementOptions { Strategies = strategies.Length > 0 ? strategies : null });

    [InteractiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultOrder_UsesEditMessage_InAnotherProcess(bool rich)
    {
        var (adapter, snapshot) = await HostAsync("Hello wrold, this is an tset.", rich);

        var result = await Engine().ApplyAsync(adapter, IssueAt(snapshot, "tset"), "test");

        Assert.True(result.Outcome == CorrectionOutcome.Applied, result.ToString());
        Assert.Equal("EditMessage", result.Method);
        // RichEdit's UIA text ends with its end-of-document "\r".
        Assert.Equal("Hello wrold, this is an test.", (await adapter.ReadTextAsync())!.TrimEnd('\r'));
    }

    [InteractiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_AndUnicode_AndDeletion(bool rich)
    {
        var (adapter, snapshot) = await HostAsync("First line\r\nShe go to the the cafe, its nice.", rich);
        var text = snapshot.Text;
        int the2 = text.IndexOf("the the", StringComparison.Ordinal) + 4;
        var deletion = CorrectionMath.ExpandDeletion(text, new TextRange(the2, 3));
        int cafe = text.IndexOf("cafe", StringComparison.Ordinal), go = text.IndexOf("go", StringComparison.Ordinal);
        FixEdit[] edits =
        [
            new(new TextRange(go, 2), "go", "goes", IssueCategory.Grammar, "test"),
            new(deletion, text.Substring(deletion.Start, deletion.Length), "", IssueCategory.Grammar, "test"),
            new(new TextRange(cafe, 4), "cafe", "caf" + (char)0xE9, IssueCategory.Spelling, "test"),
        ];

        var result = await Engine(ReplacementStrategy.EditMessage).ApplyBatchAsync(adapter, snapshot.Version, edits);

        Assert.True(result.Succeeded, result.Last.ToString());
        Assert.Equal(3, result.Applied);
        var expected = text.Replace("the the", "the").Replace("She go", "She goes").Replace("cafe", "caf" + (char)0xE9);
        Assert.True(CorrectionMath.EquivalentForVerification((await adapter.ReadTextAsync())!, expected));
    }

    [InteractiveFact]
    public async Task UnexpectedResult_IsUndoneWithEmUndo()
    {
        // MaxLength truncates the replacement, so the result can't match what was expected.
        var (adapter, snapshot) = await HostAsync("Hello wrold", rich: false, maxLength: 11);

        var result = await Engine(ReplacementStrategy.EditMessage).ApplyAsync(adapter, IssueAt(snapshot, "wrold"), "worlds");

        Assert.True(result.Outcome == CorrectionOutcome.Reverted, result.ToString());
        Assert.Equal("Hello wrold", await adapter.ReadTextAsync());
    }

    [Fact]
    public void NotAnEditWindow_IsRefused()
    {
        Assert.False(EditControlMessages.IsEditWindow(IntPtr.Zero));
        Assert.False(EditControlMessages.IsFocusedEditWindow(GetDesktopWindow()));
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
        try { if (_host is { HasExited: false }) _host.Kill(); } catch (InvalidOperationException) { }
        _host?.Dispose();
        _uia.Dispose();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
}

public sealed class InteractiveTheoryAttribute : TheoryAttribute
{
    public InteractiveTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("REDLINE_INTERACTIVE_TESTS") != "1")
            Skip = "Interactive: set REDLINE_INTERACTIVE_TESTS=1 (takes focus, types, uses the clipboard).";
    }
}
