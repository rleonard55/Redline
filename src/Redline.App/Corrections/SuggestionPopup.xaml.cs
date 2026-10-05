using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Redline.Annotations;
using Redline.Core.Models;

namespace Redline.App.Corrections;

public enum PopupChoiceKind { Cancel, Suggestion, AddToDictionary, Ignore, IgnoreRule, FixMore }

public readonly record struct PopupChoice(PopupChoiceKind Kind, string? Replacement = null);

/// <summary>
/// Shows one issue with its suggestions next to the flagged text. Keyboard: 1-9 / Enter apply a
/// suggestion, D adds to dictionary, I ignores, R ignores the rule, F opens the paragraph fix (when offered),
/// Esc or clicking away cancels.
/// The popup takes focus while open; the replacement engine moves focus back to the target.
/// </summary>
public partial class SuggestionPopup : Window
{
    private readonly TaskCompletionSource<PopupChoice> _choice = new();
    private readonly TextIssue _issue;
    private readonly List<Button> _suggestionButtons = new();
    private bool _wasActivated;

    /// <param name="fixMoreLabel">Label for the "fix the whole paragraph" item, or null to leave it out.</param>
    public SuggestionPopup(TextIssue issue, string? fixMoreLabel = null)
    {
        InitializeComponent();
        _issue = issue;
        FlyoutWindow.ApplyPalette(Resources, SystemTheme.Current);

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
            SuggestionList.Children.Add(button);
            number++;
        }

        NoSuggestions.Visibility = _suggestionButtons.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddToDictionaryButton.Visibility = issue.Category == IssueCategory.Spelling && issue.RuleId != "Spelling:RepeatedWord"
            ? Visibility.Visible : Visibility.Collapsed;
        IgnoreRuleButton.Visibility = issue.Category == IssueCategory.Spelling ? Visibility.Collapsed : Visibility.Visible;
        if (fixMoreLabel is not null)
        {
            FixMoreText.Text = fixMoreLabel;
            FixMoreButton.Visibility = Visibility.Visible;
        }

        PreviewKeyDown += OnPreviewKeyDown;

        // Losing focus means "clicked away" only if we had it: when Windows refuses the activation
        // (foreground lock), Deactivated still fires and would close the popup the instant it appears.
        Activated += (_, _) => _wasActivated = true;
        Deactivated += (_, _) =>
        {
            if (_wasActivated) Complete(new PopupChoice(PopupChoiceKind.Cancel));
        };
    }

    /// <summary>
    /// Shows the popup just below <paramref name="anchor"/> (physical screen pixels), or at the mouse
    /// pointer when there's no anchor, and completes when the user chooses or dismisses.
    /// </summary>
    public Task<PopupChoice> ShowNear(TextBounds? anchor)
    {
        Show(); // starts off-screen (Left/Top = -10000) so we can measure before placing
        try
        {
            UpdateLayout();
        }
        catch (InvalidOperationException)
        {
            // Defensive: if an external window on the dispatcher has a transient layout conflict, don't abort
        }
        FlyoutWindow.Place(this, anchor);
        Activate();
        (_suggestionButtons.FirstOrDefault() ?? (UIElement)AddToDictionaryButton).Focus();

        // Activation can be refused if the user touched another app at the wrong moment; retry once.
        // If it still fails the popup stays up, and clicking it activates it normally.
        var retry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            if (!IsActive && !_choice.Task.IsCompleted)
            {
                Activate();
                (_suggestionButtons.FirstOrDefault() ?? (UIElement)AddToDictionaryButton).Focus();
            }
        };
        retry.Start();
        return _choice.Task;
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
        else if (e.Key == Key.F && FixMoreButton.IsVisible)
            Complete(new PopupChoice(PopupChoiceKind.FixMore));
        else
            return; // let Tab/arrows/Enter/Space drive the focused button
        e.Handled = true;
    }

    private void AddToDictionary_Click(object sender, RoutedEventArgs e) => Complete(new PopupChoice(PopupChoiceKind.AddToDictionary));
    private void Ignore_Click(object sender, RoutedEventArgs e) => Complete(new PopupChoice(PopupChoiceKind.Ignore));
    private void IgnoreRule_Click(object sender, RoutedEventArgs e) => Complete(new PopupChoice(PopupChoiceKind.IgnoreRule));
    private void FixMore_Click(object sender, RoutedEventArgs e) => Complete(new PopupChoice(PopupChoiceKind.FixMore));

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
}
