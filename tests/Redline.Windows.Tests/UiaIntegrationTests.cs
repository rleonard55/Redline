using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Redline.Core.Models;
using Redline.Windows.Adapters;
using Redline.Windows.Automation;
using Xunit;

namespace Redline.Windows.Tests;

/// <summary>
/// Hosts a real WPF window on its own STA thread and drives it through UI Automation exactly
/// as Redline drives other apps: UiaDispatcher → ElementInfo → adapter → watcher.
/// </summary>
public sealed class UiaIntegrationTests : IDisposable
{
    private const string InitialText = "Hello wrold, this is Redline.";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly Thread _uiThread;
    private readonly UiaDispatcher _uia = new();
    private Dispatcher _wpf = null!;
    private TextBox _textBox = null!;
    private IntPtr _hwnd;

    public UiaIntegrationTests()
    {
        using var ready = new ManualResetEventSlim();
        _uiThread = new Thread(() =>
        {
            _textBox = new TextBox { Text = InitialText, AcceptsReturn = true, FontSize = 16 };
            var window = new Window
            {
                Width = 500, Height = 200, Left = 50, Top = 50,
                Content = new StackPanel { Children = { _textBox, new PasswordBox { Password = "hunter2" } } },
                ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            _wpf = Dispatcher.CurrentDispatcher;
            _hwnd = new WindowInteropHelper(window).Handle;
            ready.Set();
            Dispatcher.Run();
        });
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.IsBackground = true;
        _uiThread.Start();
        Assert.True(ready.Wait(Timeout));
    }

    private Task<AutomationElement> FindAsync(ControlType type) => _uia.InvokeAsync(() =>
        UiaTestHelpers.FromHandle(_hwnd).FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, type)));

    [Fact]
    public async Task GenericAdapter_ReadsText_Caret_AndGeometry()
    {
        var element = await FindAsync(ControlType.Edit);
        var info = await _uia.InvokeAsync(() => ElementInfo.Capture(element));

        Assert.Equal("Edit", info.ControlType);
        Assert.Equal("WPF", info.FrameworkId);
        Assert.True(info.SupportsTextPattern);
        Assert.True(new GenericUiaAdapterFactory().CanHandle(info));

        using var adapter = await _uia.InvokeAsync(() => new GenericUiaAdapterFactory().Create(_uia, element, info));
        Assert.True(adapter.Capabilities.CanReadText);
        Assert.Equal(info.SurfaceId, adapter.Context.SurfaceId);

        Assert.Equal(InitialText, await adapter.ReadTextAsync());

        await _wpf.InvokeAsync(() => { _textBox.Focus(); _textBox.CaretIndex = 6; });
        Assert.Equal(6, await adapter.GetCaretOffsetAsync());

        var bounds = await adapter.GetBoundsAsync(new TextRange(6, 5)); // "wrold"
        var single = Assert.Single(bounds);
        Assert.True(single.Width > 10 && single.Height > 10, $"Unexpected bounds {single}");

        // The word's rectangle must lie inside the window's on-screen rectangle.
        var window = await _uia.InvokeAsync(() => UiaTestHelpers.FromHandle(_hwnd).Current.BoundingRectangle);
        Assert.True(window.Contains(new Point(single.X + 1, single.Y + 1)), $"{single} not within {window}");
    }

    [Fact]
    public async Task Watcher_ReportsEditsMadeInTheTargetControl()
    {
        var element = await FindAsync(ControlType.Edit);
        var info = await _uia.InvokeAsync(() => ElementInfo.Capture(element));
        using var adapter = await _uia.InvokeAsync(() => new GenericUiaAdapter(_uia, element, info));
        using var watcher = new TextChangeWatcher(_uia, element, adapter, NullLogger.Instance);

        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.TextRead += text =>
        {
            if (text.Contains("edited")) seen.TrySetResult(text);
        };

        await _uia.InvokeAsync(() => watcher.Start(info.SupportsTextPattern, info.SupportsValuePattern));
        await _wpf.InvokeAsync(() => _textBox.AppendText(" edited"));

        Assert.Equal(InitialText + " edited", await seen.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task PasswordBox_IsBlockedBySecurityFilter()
    {
        var element = await _uia.InvokeAsync(() =>
            UiaTestHelpers.FromHandle(_hwnd).FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsPasswordProperty, true)));
        var info = await _uia.InvokeAsync(() => ElementInfo.Capture(element));

        var decision = new SecurityFilter().Evaluate(info);

        Assert.False(decision.Allowed);
        Assert.Equal("Password field", decision.Reason);
    }

    public void Dispose()
    {
        _wpf.InvokeShutdown();
        _uiThread.Join(2000);
        _uia.Dispose();
    }
}
