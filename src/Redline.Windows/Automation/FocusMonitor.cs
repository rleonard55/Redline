using System.Windows.Automation;

namespace Redline.Windows.Automation;

/// <summary>
/// Global UIA focus-changed subscription. Callbacks arrive on the UIA client's callback
/// thread and are forwarded as-is; consumers must hop to the <see cref="UiaDispatcher"/>
/// before touching the element.
/// </summary>
public sealed class FocusMonitor : IDisposable
{
    private readonly UiaDispatcher _uia;
    private AutomationFocusChangedEventHandler? _handler;

    public FocusMonitor(UiaDispatcher uia)
    {
        _uia = uia;
    }

    public event Action<AutomationElement>? FocusChanged;

    public Task StartAsync() => _uia.InvokeAsync(() =>
    {
        if (_handler is not null) return;
        _handler = (sender, _) =>
        {
            if (sender is AutomationElement element)
                FocusChanged?.Invoke(element);
        };
        System.Windows.Automation.Automation.AddAutomationFocusChangedEventHandler(_handler);
    });

    public void Dispose()
    {
        var handler = Interlocked.Exchange(ref _handler, null);
        if (handler is null) return;

        try
        {
            // Wait briefly so the subscription is gone before the dispatcher shuts down.
            _uia.InvokeAsync(() => System.Windows.Automation.Automation.RemoveAutomationFocusChangedEventHandler(handler)).Wait(1000);
        }
        catch
        {
            // Shutting down; process exit releases the subscription.
        }
    }
}
