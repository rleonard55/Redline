using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Redline.Core.Models;

namespace Redline.App.Corrections;

public enum PopupChoiceKind { Cancel, Suggestion, AddToDictionary, Ignore, IgnoreRule }

public readonly record struct PopupChoice(PopupChoiceKind Kind, string? Replacement = null);

/// <summary>
/// Shows one issue with its suggestions next to the flagged text. Keyboard: 1-9 / Enter apply a
/// suggestion, D adds to dictionary, I ignores, R ignores the rule, Esc or clicking away cancels.
/// The popup takes focus while open; the replacement engine moves focus back to the target.
/// </summary>
public partial class SuggestionPopup : Window
{
    private readonly TaskCompletionSource<PopupChoice> _choice = new();
    private readonly TextIssue _issue;
    private readonly List<Button> _suggestionButtons = new();

    public SuggestionPopup(TextIssue issue)
    {
        InitializeComponent();
        _issue = issue;

        CategoryText.Text = issue.Category.ToString();
        CategoryBadge.Background = new SolidColorBrush(CategoryColor(issue.Category));
        OriginalText.Text = issue.OriginalText;
        MessageText.Text = issue.Message;

        int number = 1;
        foreach (var suggestion in issue.Suggestions.Take(9))
        {
            var label = new TextBlock();
            label.Inlines.Add(new Run($"{number}  ") { Foreground = Brushes.Gray });
            label.Inlines.Add(suggestion.Length == 0
                ? new Run("(remove)") { FontStyle = FontStyles.Italic }
                : new Run(suggestion) { FontWeight = FontWeights.SemiBold });

            var replacement = suggestion;
            var button = new Button { Content = label, Style = (Style)FindResource("ItemButton") };
            button.Click += (_, _) => Complete(new PopupChoice(PopupChoiceKind.Suggestion, replacement));
            _suggestionButtons.Add(button);
            SuggestionList.Items.Add(button);
            number++;
        }

        NoSuggestions.Visibility = _suggestionButtons.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddToDictionaryButton.Visibility = issue.Category == IssueCategory.Spelling && issue.RuleId != "Spelling:RepeatedWord"
            ? Visibility.Visible : Visibility.Collapsed;
        IgnoreRuleButton.Visibility = issue.Category == IssueCategory.Spelling ? Visibility.Collapsed : Visibility.Visible;

        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => Complete(new PopupChoice(PopupChoiceKind.Cancel));
    }

    /// <summary>
    /// Shows the popup just below <paramref name="anchor"/> (physical screen pixels), or at the mouse
    /// pointer when there's no anchor, and completes when the user chooses or dismisses.
    /// </summary>
    public Task<PopupChoice> ShowNear(TextBounds? anchor)
    {
        Show(); // starts off-screen (Left/Top = -10000) so we can measure before placing
        UpdateLayout();
        Place(anchor);
        Activate();
        (_suggestionButtons.FirstOrDefault() ?? (UIElement)AddToDictionaryButton).Focus();
        return _choice.Task;
    }

    private void Place(TextBounds? anchor)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        int margin = (int)(8 * dpi.DpiScaleX); // the Border's shadow margin

        int x, y, anchorTop;
        if (anchor is { } a)
        {
            x = (int)a.Left - margin;
            y = (int)a.Bottom + 2 - margin;
            anchorTop = (int)a.Top;
        }
        else
        {
            GetCursorPos(out var p);
            (x, y, anchorTop) = (p.X, p.Y + 16, p.Y);
        }

        // Keep it on the monitor that holds the anchor; flip above the text if there's no room below.
        var monitor = MonitorFromPoint(new POINT { X = x + margin, Y = anchorTop }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            var work = info.rcWork;
            if (y + height > work.Bottom) y = anchorTop - height + margin - 2;
            x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
            y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        }

        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        int digit = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1
                  : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;

        if (digit >= 0 && digit < _suggestionButtons.Count)
            Complete(new PopupChoice(PopupChoiceKind.Suggestion, _issue.Suggestions[digit]));
        else if (e.Key == Key.Escape)
            Complete(new PopupChoice(PopupChoiceKind.Cancel));
        else if (e.Key == Key.D && AddToDictionaryButton.IsVisible)
            Complete(new PopupChoice(PopupChoiceKind.AddToDictionary));
        else if (e.Key == Key.I)
            Complete(new PopupChoice(PopupChoiceKind.Ignore));
        else if (e.Key == Key.R && IgnoreRuleButton.IsVisible)
            Complete(new PopupChoice(PopupChoiceKind.IgnoreRule));
        else
            return; // let Tab/arrows/Enter/Space drive the focused button
        e.Handled = true;
    }

    private void AddToDictionary_Click(object sender, RoutedEventArgs e) => Complete(new PopupChoice(PopupChoiceKind.AddToDictionary));
    private void Ignore_Click(object sender, RoutedEventArgs e) => Complete(new PopupChoice(PopupChoiceKind.Ignore));
    private void IgnoreRule_Click(object sender, RoutedEventArgs e) => Complete(new PopupChoice(PopupChoiceKind.IgnoreRule));

    private void Complete(PopupChoice choice)
    {
        if (!_choice.TrySetResult(choice)) return;
        Close();
    }

    private static Color CategoryColor(IssueCategory category) => category switch
    {
        IssueCategory.Spelling => Color.FromRgb(0xC4, 0x2B, 0x1C),
        IssueCategory.Grammar => Color.FromRgb(0x1F, 0x5F, 0xBF),
        IssueCategory.Style => Color.FromRgb(0x9A, 0x67, 0x00),
        IssueCategory.Punctuation => Color.FromRgb(0x6B, 0x3F, 0xA0),
        _ => Color.FromRgb(0x55, 0x55, 0x55),
    };

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
