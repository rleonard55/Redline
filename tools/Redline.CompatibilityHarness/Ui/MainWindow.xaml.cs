using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using Microsoft.Win32;
using Redline.CompatibilityHarness.Probes;
using Redline.CompatibilityHarness.Report;
using Redline.Core.Models;
using Redline.Windows.Automation;

namespace Redline.CompatibilityHarness.Ui;

public partial class MainWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private readonly UiaDispatcher _uia = new();
    private readonly CompatibilityReport _report = new();
    private readonly ObservableCollection<AppCompatibilityResult> _history = new();

    private AutomationElement? _currentElement;
    private PatternSupportResult? _currentPatterns;
    private TextReadResult? _currentRead;
    private CaretSelectionResult? _currentCaret;
    private GeometryResult? _currentGeometry;
    private ChangeDetectionResult? _currentChange;
    private ReplaceResult? _currentReplace;

    private Brush GreenBrush => TryFindResource("AccentGreen") as Brush ?? Brushes.LimeGreen;
    private Brush RedBrush => TryFindResource("AccentRed") as Brush ?? Brushes.Crimson;
    private Brush OrangeBrush => TryFindResource("AccentOrange") as Brush ?? Brushes.Orange;

    public MainWindow()
    {
        InitializeComponent();
        GridHistory.ItemsSource = _history;
        Log("Compatibility Harness initialized. Ready to test target applications.");

        Loaded += (s, e) =>
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                _report.Machine.PrimaryDpiScale = source.CompositionTarget.TransformToDevice.M11;
            }
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _uia.Dispose();
    }

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            TxtLog.ScrollToEnd();
        });
    }

    private async void BtnPickIn3Seconds_Click(object sender, RoutedEventArgs e)
    {
        BtnPickIn3Seconds.IsEnabled = false;
        try
        {
            for (int i = 3; i > 0; i--)
            {
                Log($"Please click into your target text control now... ({i}s)");
                await Task.Delay(1000);
            }

            Log("Capturing focused element...");
            var element = await _uia.InvokeAsync(() =>
            {
                var el = AutomationElement.FocusedElement;
                return ResolveDeepestEditableElement(el);
            });

            if (element == null)
            {
                Log("ERROR: AutomationElement.FocusedElement was null.");
                return;
            }

            await SetTargetElementAsync(element);
        }
        catch (Exception ex)
        {
            Log($"Failed capturing focused element: {ex.Message}");
        }
        finally
        {
            BtnPickIn3Seconds.IsEnabled = true;
        }
    }

    private async void BtnPickUnderCursor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            GetCursorPos(out var pt);
            Log($"Probing element under cursor at ({pt.X}, {pt.Y})...");

            var element = await _uia.InvokeAsync(() =>
            {
                var point = new System.Windows.Point(pt.X, pt.Y);
                var el = AutomationElement.FromPoint(point);
                return ResolveDeepestEditableElement(el);
            });

            if (element == null)
            {
                Log("No AutomationElement found under cursor.");
                return;
            }

            await SetTargetElementAsync(element);
        }
        catch (Exception ex)
        {
            Log($"Failed picking element under cursor: {ex.Message}");
        }
    }

    private static AutomationElement ResolveDeepestEditableElement(AutomationElement? element)
    {
        if (element == null) return null!;

        try
        {
            bool hasTextOrValue = element.TryGetCurrentPattern(TextPattern.Pattern, out _) ||
                                  element.TryGetCurrentPattern(ValuePattern.Pattern, out _);

            if (!hasTextOrValue)
            {
                // 1. Try finding descendant with keyboard focus
                var focusedDescendant = element.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true));

                if (focusedDescendant != null && focusedDescendant != element)
                {
                    return focusedDescendant;
                }

                // 2. Try finding an Edit or Document control type descendant
                var editDescendant = element.FindFirst(
                    TreeScope.Descendants,
                    new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)));

                if (editDescendant != null)
                {
                    return editDescendant;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ResolveDeepestEditableElement error: {ex.Message}");
        }

        return element;
    }

    private async Task SetTargetElementAsync(AutomationElement element)
    {
        _currentElement = element;
        _currentPatterns = null;
        _currentRead = null;
        _currentCaret = null;
        _currentGeometry = null;
        _currentChange = null;
        _currentReplace = null;

        // Reset UI statuses
        StatusPatterns.Text = "⏳ Running...";
        StatusTextRead.Text = "⏸️ Not Run";
        StatusCaret.Text = "⏸️ Not Run";
        StatusGeometry.Text = "⏸️ Not Run";
        StatusChange.Text = "⏸️ Not Run";
        StatusReplace.Text = "⏸️ Not Run";
        BtnShowOverlay.IsEnabled = false;

        // Probe 1: Pattern Support
        _currentPatterns = await _uia.InvokeAsync(() => PatternSupportProbe.Run(element));

        var info = _currentPatterns.TargetInfo;
        TxtProcess.Text = $"{info.ProcessName} (PID: {info.ProcessId})";
        TxtWindowTitle.Text = string.IsNullOrEmpty(info.WindowTitle) ? "(no title)" : info.WindowTitle;
        TxtControlClass.Text = $"{info.ControlType} / {info.ClassName}";
        TxtFramework.Text = string.IsNullOrEmpty(info.FrameworkId) ? "N/A" : info.FrameworkId;

        StatusPatterns.Text = "✅ Complete";
        StatusPatterns.Foreground = GreenBrush;

        TxtPatternList.Text = $"Patterns: {string.Join(", ", _currentPatterns.AllSupportedPatterns)} | " +
                              $"Text: {(_currentPatterns.SupportsTextPattern ? "✅" : "❌")} | " +
                              $"Text2: {(_currentPatterns.SupportsTextPattern2 ? "✅" : "❌")} | " +
                              $"Value: {(_currentPatterns.SupportsValuePattern ? "✅" : "❌")} | " +
                              $"Password: {_currentPatterns.IsPassword} | ReadOnly: {_currentPatterns.IsReadOnly}";

        Log($"Selected target: {info.ProcessName} - Control: {info.ControlType} (Framework: {info.FrameworkId})");
    }

    private async void BtnRunAllProbes_Click(object sender, RoutedEventArgs e)
    {
        if (_currentElement == null)
        {
            MessageBox.Show("Please select a target element first.", "Redline Harness", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BtnRunAllProbes.IsEnabled = false;
        try
        {
            await RunReadProbeAsync();
            await RunCaretProbeAsync();
            await RunGeometryProbeAsync();
            await RunChangeProbeAsync();

            // Ask before replace
            var ask = MessageBox.Show(
                "Would you like to run the Text Replace probe now?\nThis will test replacing text in the target control.",
                "Redline Harness — Replace Probe",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (ask == MessageBoxResult.Yes)
            {
                await RunReplaceProbeAsync();
            }

            RecordCurrentResult();
        }
        finally
        {
            BtnRunAllProbes.IsEnabled = true;
        }
    }

    private async Task RunReadProbeAsync()
    {
        if (_currentElement == null || _currentPatterns == null) return;

        StatusTextRead.Text = "⏳ Reading...";
        _currentRead = await _uia.InvokeAsync(() => TextReadProbe.Run(_currentElement, _currentPatterns));

        if (_currentRead.Success)
        {
            StatusTextRead.Text = "✅ Success";
            StatusTextRead.Foreground = GreenBrush;
            TxtReadPreview.Text = $"Text ({_currentRead.TextLength} chars): {_currentRead.TextPreview}";
            TxtReadStats.Text = $"Method: {_currentRead.Method} | Duration: {_currentRead.DurationMs:F1}ms";
            Log($"[ReadProbe] Success via {_currentRead.Method} in {_currentRead.DurationMs:F1}ms ({_currentRead.TextLength} chars)");
        }
        else
        {
            StatusTextRead.Text = "❌ Failed";
            StatusTextRead.Foreground = RedBrush;
            TxtReadPreview.Text = $"Error: {_currentRead.Error}";
            TxtReadStats.Text = $"Duration: {_currentRead.DurationMs:F1}ms";
            Log($"[ReadProbe] FAILED: {_currentRead.Error}");
        }
    }

    private async Task RunCaretProbeAsync()
    {
        if (_currentElement == null || _currentPatterns == null) return;

        StatusCaret.Text = "⏳ Probing...";
        _currentCaret = await _uia.InvokeAsync(() => CaretSelectionProbe.Run(_currentElement, _currentPatterns));

        if (_currentCaret.CanGetSelection || _currentCaret.CanGetCaretRange)
        {
            StatusCaret.Text = "✅ Success";
            StatusCaret.Foreground = GreenBrush;
            TxtCaretInfo.Text = $"Caret Offset: {(_currentCaret.CaretOffset.HasValue ? _currentCaret.CaretOffset.Value.ToString() : "N/A")} | " +
                               $"Selection Text: '{_currentCaret.SelectionText ?? "(none)"}' | " +
                               $"Range Count: {_currentCaret.SelectionRangeCount}";
            Log($"[CaretProbe] Caret detected at offset {_currentCaret.CaretOffset}, selection='{_currentCaret.SelectionText}'");
        }
        else
        {
            StatusCaret.Text = "⚠️ Limited";
            StatusCaret.Foreground = OrangeBrush;
            TxtCaretInfo.Text = $"Status: {_currentCaret.Error ?? "Not supported"}";
            Log($"[CaretProbe] Limited: {_currentCaret.Error}");
        }
    }

    private async Task RunGeometryProbeAsync()
    {
        if (_currentElement == null || _currentPatterns == null) return;
        if (_currentRead == null) await RunReadProbeAsync();

        StatusGeometry.Text = "⏳ Mapping...";
        _currentGeometry = await _uia.InvokeAsync(() => GeometryProbe.Run(_currentElement, _currentPatterns, _currentRead!));

        if (_currentGeometry.CanGetBoundingRectangles)
        {
            StatusGeometry.Text = _currentGeometry.AreRectsReasonable ? "✅ Valid" : "⚠️ Needs Review";
            StatusGeometry.Foreground = _currentGeometry.AreRectsReasonable ? GreenBrush : OrangeBrush;

            BtnShowOverlay.IsEnabled = true;
            TxtGeometryInfo.Text = $"Tested Range: '{_currentGeometry.TestedRange}' | " +
                                   $"Rectangles: {_currentGeometry.BoundingRects.Count} | " +
                                   $"Reasonable Bounds: {_currentGeometry.AreRectsReasonable} | " +
                                   $"Duration: {_currentGeometry.RectComputeDurationMs:F1}ms";

            Log($"[GeometryProbe] {_currentGeometry.BoundingRects.Count} bounds computed in {_currentGeometry.RectComputeDurationMs:F1}ms");
        }
        else
        {
            StatusGeometry.Text = "❌ Failed";
            StatusGeometry.Foreground = RedBrush;
            BtnShowOverlay.IsEnabled = false;
            TxtGeometryInfo.Text = $"Error: {_currentGeometry.Error}";
            Log($"[GeometryProbe] FAILED: {_currentGeometry.Error}");
        }
    }

    private async Task RunChangeProbeAsync()
    {
        if (_currentElement == null || _currentPatterns == null) return;

        StatusChange.Text = "⏳ Listening...";
        var progress = new Progress<string>(msg => Log(msg));

        _currentChange = await ChangeDetectionProbe.RunAsync(
            _currentElement, _currentPatterns, TimeSpan.FromSeconds(7), progress);

        if (_currentChange.ReceivedTextChangedEvent || _currentChange.ReceivedValueChangedEvent)
        {
            StatusChange.Text = "✅ Detected";
            StatusChange.Foreground = GreenBrush;
            TxtChangeInfo.Text = $"TextChangedEvent: {_currentChange.ReceivedTextChangedEvent} ({_currentChange.TextChangedEventCount}x) | " +
                                $"ValueChangedEvent: {_currentChange.ReceivedValueChangedEvent} | " +
                                $"Latency: {_currentChange.TextChangedLatencyMs:F0}ms";
            Log($"[ChangeProbe] Text changes captured! Latency: {_currentChange.TextChangedLatencyMs:F0}ms");
        }
        else
        {
            StatusChange.Text = "❌ No Events";
            StatusChange.Foreground = RedBrush;
            TxtChangeInfo.Text = $"Result: {_currentChange.Error ?? "No events captured"}";
            Log($"[ChangeProbe] No events: {_currentChange.Error}");
        }
    }

    private async Task RunReplaceProbeAsync()
    {
        if (_currentElement == null || _currentPatterns == null) return;
        if (_currentRead == null) await RunReadProbeAsync();

        StatusReplace.Text = "⏳ Replacing...";
        string original = _currentRead?.FullText ?? string.Empty;
        string targetWord = "test";
        if (!string.IsNullOrEmpty(original))
        {
            var words = original.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0) targetWord = words[0];
        }

        string replacement = $"{targetWord}_REDLINE";

        _currentReplace = await ReplaceProbe.RunAsync(
            _currentElement, _currentPatterns, _currentRead!, targetWord, replacement);

        if (_currentReplace.Success)
        {
            StatusReplace.Text = "✅ Success";
            StatusReplace.Foreground = GreenBrush;
            TxtReplaceInfo.Text = $"Method: {_currentReplace.Method} | Verified: {_currentReplace.VerifiedCorrect} | Duration: {_currentReplace.DurationMs:F1}ms";
            Log($"[ReplaceProbe] Success via {_currentReplace.Method} in {_currentReplace.DurationMs:F1}ms");
        }
        else
        {
            StatusReplace.Text = "❌ Failed";
            StatusReplace.Foreground = RedBrush;
            TxtReplaceInfo.Text = $"Error: {_currentReplace.Error}";
            Log($"[ReplaceProbe] FAILED: {_currentReplace.Error}");
        }
    }

    private void RecordCurrentResult()
    {
        if (_currentPatterns == null || _currentRead == null) return;

        var result = new AppCompatibilityResult
        {
            AppName = _currentPatterns.TargetInfo.ProcessName,
            TargetInfo = _currentPatterns.TargetInfo,
            PatternSupport = _currentPatterns,
            TextRead = _currentRead,
            CaretSelection = _currentCaret,
            Geometry = _currentGeometry,
            ChangeDetection = _currentChange,
            Replace = _currentReplace
        };

        _history.Add(result);
        _report.Results.Add(result);
        TxtTargetCount.Text = $"Recorded Targets: {_report.Results.Count}";
    }

    private void BtnShowOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGeometry == null || !_currentGeometry.CanGetBoundingRectangles) return;

        var overlay = new ProbeOverlayWindow(
            _currentGeometry.BoundingRects,
            _currentGeometry.TestedRange,
            _currentGeometry.AreRectsReasonable);

        overlay.Show();
        Log("Probe visual overlay shown for 5 seconds (press any key to dismiss).");
    }

    private async void BtnTestTyping_Click(object sender, RoutedEventArgs e)
    {
        if (_currentElement == null) return;
        BtnTestTyping.IsEnabled = false;
        try
        {
            await RunChangeProbeAsync();
        }
        finally
        {
            BtnTestTyping.IsEnabled = true;
        }
    }

    private async void BtnTestReplace_Click(object sender, RoutedEventArgs e)
    {
        if (_currentElement == null) return;
        var ask = MessageBox.Show(
            "This will modify text in the active control for testing.\nProceed?",
            "Confirm Replace Test",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (ask != MessageBoxResult.Yes) return;

        BtnTestReplace.IsEnabled = false;
        try
        {
            await RunReplaceProbeAsync();
        }
        finally
        {
            BtnTestReplace.IsEnabled = true;
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        TxtLog.Clear();
    }

    private void BtnExportJson_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sfd = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                FileName = $"redline_compatibility_{DateTime.Now:yyyyMMdd_HHmmss}.json"
            };

            if (sfd.ShowDialog() == true)
            {
                var json = ReportRenderer.ToJson(_report);
                File.WriteAllText(sfd.FileName, json);
                Log($"Report exported to {sfd.FileName}");
                MessageBox.Show($"Report successfully exported to:\n{sfd.FileName}", "Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Log($"Export JSON failed: {ex.Message}");
            MessageBox.Show($"Failed to export JSON report:\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnExportMarkdown_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sfd = new SaveFileDialog
            {
                Filter = "Markdown files (*.md)|*.md",
                FileName = $"compatibility_report_{DateTime.Now:yyyyMMdd_HHmmss}.md"
            };

            if (sfd.ShowDialog() == true)
            {
                var md = ReportRenderer.ToMarkdown(_report);
                File.WriteAllText(sfd.FileName, md);
                Log($"Markdown report exported to {sfd.FileName}");
                MessageBox.Show($"Markdown report successfully exported to:\n{sfd.FileName}", "Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Log($"Export Markdown failed: {ex.Message}");
            MessageBox.Show($"Failed to export Markdown report:\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
