using System.Diagnostics;
using System.Windows.Automation;

namespace Redline.CompatibilityHarness.Probes;

public static class ChangeDetectionProbe
{
    public static async Task<ChangeDetectionResult> RunAsync(
        AutomationElement element,
        PatternSupportResult patterns,
        TimeSpan timeout,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(element);

        var sw = Stopwatch.StartNew();
        bool receivedTextChange = false;
        int textChangeCount = 0;
        double? firstLatency = null;
        bool receivedSelectionChange = false;
        bool receivedValueChange = false;
        string? error = null;

        var tcs = new TaskCompletionSource<bool>();

        AutomationEventHandler? textChangedHandler = null;
        AutomationEventHandler? selectionChangedHandler = null;
        AutomationPropertyChangedEventHandler? propChangedHandler = null;

        try
        {
            if (patterns.SupportsTextPattern)
            {
                textChangedHandler = (sender, args) =>
                {
                    textChangeCount++;
                    firstLatency ??= sw.Elapsed.TotalMilliseconds;
                    receivedTextChange = true;
                    progress?.Report($"[TextChangedEvent] fired ({textChangeCount}x)");
                    tcs.TrySetResult(true);
                };

                Automation.AddAutomationEventHandler(
                    TextPattern.TextChangedEvent,
                    element,
                    TreeScope.Element,
                    textChangedHandler);

                selectionChangedHandler = (sender, args) =>
                {
                    receivedSelectionChange = true;
                    progress?.Report("[TextSelectionChangedEvent] fired");
                };

                Automation.AddAutomationEventHandler(
                    TextPattern.TextSelectionChangedEvent,
                    element,
                    TreeScope.Element,
                    selectionChangedHandler);
            }

            if (patterns.SupportsValuePattern)
            {
                propChangedHandler = (sender, args) =>
                {
                    receivedValueChange = true;
                    firstLatency ??= sw.Elapsed.TotalMilliseconds;
                    progress?.Report("[ValueChangedEvent] fired");
                    tcs.TrySetResult(true);
                };

                Automation.AddAutomationPropertyChangedEventHandler(
                    element,
                    TreeScope.Element,
                    propChangedHandler,
                    ValuePattern.ValueProperty);
            }

            progress?.Report($"Listening for events... Please type in the target app (timeout: {timeout.TotalSeconds:F0}s)");

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var registration = linkedCts.Token.Register(() => tcs.TrySetCanceled());

            try
            {
                await tcs.Task;
                // Once first event is captured, give a brief moment to catch any subsequent events
                await Task.Delay(500, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                if (timeoutCts.IsCancellationRequested && !receivedTextChange && !receivedValueChange)
                {
                    error = "Timeout reached: No text change events were detected.";
                }
            }
            finally
            {
                registration.Dispose();
            }
        }
        catch (Exception ex)
        {
            error = $"Event subscription error: {ex.Message}";
        }
        finally
        {
            // Clean up UIA event listeners to prevent COM object leaks
            try
            {
                if (textChangedHandler != null)
                {
                    Automation.RemoveAutomationEventHandler(
                        TextPattern.TextChangedEvent,
                        element,
                        textChangedHandler);
                }

                if (selectionChangedHandler != null)
                {
                    Automation.RemoveAutomationEventHandler(
                        TextPattern.TextSelectionChangedEvent,
                        element,
                        selectionChangedHandler);
                }

                if (propChangedHandler != null)
                {
                    Automation.RemoveAutomationPropertyChangedEventHandler(
                        element,
                        propChangedHandler);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error removing UIA handlers: {ex.Message}");
            }
        }

        return new ChangeDetectionResult
        {
            ReceivedTextChangedEvent = receivedTextChange,
            TextChangedEventCount = textChangeCount,
            TextChangedLatencyMs = firstLatency,
            ReceivedSelectionChangedEvent = receivedSelectionChange,
            ReceivedValueChangedEvent = receivedValueChange,
            Error = error
        };
    }
}
