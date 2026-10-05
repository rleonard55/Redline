using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Redline.Annotations;
using Redline.Core.Corrections;
using Redline.Core.Models;
using TextRange = Redline.Core.Models.TextRange;

namespace Redline.App.Corrections;

/// <summary>
/// Previews every fix in a paragraph, sentence or selection at once: the text with removals struck out and
/// insertions highlighted, a checkbox per change, and (with AI grammar on) the model's version of each sentence
/// that differs from the individual fixes. Completes with the edits to apply, or null when dismissed.
/// Keyboard: Enter applies, 1-9 toggle a change, S / P switch between sentence and paragraph, Esc cancels.
/// </summary>
public partial class FixPopup : Window
{
    private readonly TaskCompletionSource<IReadOnlyList<FixEdit>?> _result = new();
    private readonly IReadOnlyList<CompositeFix> _scopes;
    private readonly HashSet<FixEdit> _unticked = new();
    private readonly HashSet<SentenceAlternative> _tickedAlternatives = new();
    private readonly List<(FixEdit Edit, CheckBox Box)> _editBoxes = new();
    private readonly Brush _removedBrush, _insertedBrush, _mutedBrush;
    private IReadOnlyList<SentenceAlternative> _alternatives = [];
    private IReadOnlyList<FixEdit> _chosen = [];
    private int _scope;
    private bool _aiPending;
    private bool _wasActivated;
    private bool _closing;

    /// <param name="scopes">The fixes the user can switch between (sentence then paragraph, or just the selection).</param>
    /// <param name="initialScope">Index into <paramref name="scopes"/> to show first.</param>
    /// <param name="aiPending">True while the grammar model's sentence versions are still coming (<see cref="SetAlternatives"/>).</param>
    public FixPopup(IReadOnlyList<CompositeFix> scopes, int initialScope, bool aiPending)
    {
        InitializeComponent();
        var palette = SystemTheme.Current;
        FlyoutWindow.ApplyPalette(Resources, palette);
        _removedBrush = Frozen(palette.IsDark ? Color.FromRgb(0xFF, 0x99, 0xA4) : Color.FromRgb(0xC4, 0x2B, 0x1C));
        _insertedBrush = Frozen(palette.IsDark ? Color.FromRgb(0x6C, 0xCB, 0x5F) : Color.FromRgb(0x0F, 0x7B, 0x0F));
        _mutedBrush = palette.MutedText;

        _scopes = scopes;
        _scope = Math.Clamp(initialScope, 0, scopes.Count - 1);
        _aiPending = aiPending;
        foreach (var edit in scopes.SelectMany(s => s.Edits).Where(e => !e.DefaultOn))
            _unticked.Add(edit);
        ScopeButtons.Visibility = scopes.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        BuildLists();

        PreviewKeyDown += OnPreviewKeyDown;
        Activated += (_, _) => _wasActivated = true;
        Deactivated += (_, _) =>
        {
            if (_wasActivated) Complete(null);
        };
    }

    private CompositeFix Current => _scopes[_scope];

    /// <summary>The grammar model's answers have arrived (empty when it had nothing, or isn't available).</summary>
    public void SetAlternatives(IReadOnlyList<SentenceAlternative> alternatives)
    {
        if (_result.Task.IsCompleted) return;
        _alternatives = alternatives;
        _aiPending = false;
        var focused = Keyboard.FocusedElement;
        BuildLists();
        if (focused is UIElement { IsVisible: true } element) element.Focus();
        else ApplyButton.Focus();
    }

    /// <summary>Shows the popup below <paramref name="anchor"/> (physical pixels) and completes with the edits to apply.</summary>
    public Task<IReadOnlyList<FixEdit>?> ShowNear(TextBounds? anchor)
    {
        Show(); // off-screen first (Left/Top = -10000) so it can be measured before placing
        UpdateLayout();
        FlyoutWindow.Place(this, anchor);
        Activate();
        ApplyButton.Focus();

        // Activation can be refused if the user touched another app at the wrong moment; retry once.
        var retry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            if (!IsActive && !_result.Task.IsCompleted)
            {
                Activate();
                ApplyButton.Focus();
            }
        };
        retry.Start();

        // The content grows when the model's answers arrive; keep the popup on screen.
        SizeChanged += (_, _) => { if (IsVisible) FlyoutWindow.Place(this, anchor); };
        return _result.Task;
    }

    /// <summary>(Re)creates the change and alternative rows for the current scope.</summary>
    private void BuildLists()
    {
        var fix = Current;
        TitleText.Text = fix.Scope switch
        {
            FixScope.Sentence => "Fix this sentence",
            FixScope.Paragraph => "Fix this paragraph",
            _ => "Fix the selection",
        };
        var selected = (Brush)Resources["Flyout.Hover"];
        SentenceButton.Background = fix.Scope == FixScope.Sentence ? selected : Brushes.Transparent;
        ParagraphButton.Background = fix.Scope == FixScope.Paragraph ? selected : Brushes.Transparent;
        SentenceButton.FontWeight = fix.Scope == FixScope.Sentence ? FontWeights.SemiBold : FontWeights.Normal;
        ParagraphButton.FontWeight = fix.Scope == FixScope.Paragraph ? FontWeights.SemiBold : FontWeights.Normal;

        ChangeList.Children.Clear();
        _editBoxes.Clear();
        int number = 1;
        foreach (var edit in fix.Edits)
        {
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
            if (number <= 9) label.Inlines.Add(new Run($"{number}  ") { Foreground = _mutedBrush });
            label.Inlines.AddRange(DescribeEdit(edit));
            label.Inlines.Add(new Run($"  {edit.Category}") { Foreground = _mutedBrush, FontSize = 11 });

            var box = NewCheckBox(label, !_unticked.Contains(edit));
            var captured = edit;
            box.Checked += (_, _) => { _unticked.Remove(captured); Refresh(); };
            box.Unchecked += (_, _) => { _unticked.Add(captured); Refresh(); };
            _editBoxes.Add((edit, box));
            ChangeList.Children.Add(box);
            number++;
        }
        NoChanges.Visibility = fix.Edits.Count == 0 && !_aiPending ? Visibility.Visible : Visibility.Collapsed;

        AlternativeList.Children.Clear();
        var alternatives = AlternativesInScope();
        foreach (var alternative in alternatives)
        {
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
            label.Inlines.AddRange(PreviewRuns(fix.Text, alternative.Sentence, alternative.Edits));
            var box = NewCheckBox(label, _tickedAlternatives.Contains(alternative));
            var captured = alternative;
            box.Checked += (_, _) => { _tickedAlternatives.Add(captured); Refresh(); };
            box.Unchecked += (_, _) => { _tickedAlternatives.Remove(captured); Refresh(); };
            AlternativeList.Children.Add(box);
        }
        AiHeader.Visibility = alternatives.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AiStatus.Text = "The AI grammar model is checking these sentences...";
        AiStatus.Visibility = _aiPending ? Visibility.Visible : Visibility.Collapsed;

        Refresh();
    }

    /// <summary>Preview, enabled states and the Apply button after any toggle.</summary>
    private void Refresh()
    {
        var fix = Current;
        var alternatives = AlternativesInScope().Where(_tickedAlternatives.Contains).ToList();
        _chosen = CompositeFix.Combine(fix.Edits.Where(e => !_unticked.Contains(e)), alternatives);

        foreach (var (edit, box) in _editBoxes)
            box.IsEnabled = !alternatives.Any(a => a.Sentence.IntersectsWith(edit.Range));

        PreviewText.Inlines.Clear();
        PreviewText.Inlines.AddRange(PreviewRuns(fix.Text, fix.Range, _chosen));

        ApplyButton.IsEnabled = _chosen.Count > 0;
        ApplyButton.Content = _chosen.Count switch
        {
            0 => "Nothing to apply",
            1 => "Apply 1 change",
            var n => $"Apply {n} changes",
        };
    }

    /// <summary>Model versions inside the current scope that differ from what the individual fixes make of the sentence.</summary>
    private List<SentenceAlternative> AlternativesInScope()
    {
        var fix = Current;
        return _alternatives
            .Where(a => a.Sentence.Start >= fix.Range.Start && a.Sentence.End <= fix.Range.End)
            .Where(a => CompositeFix.AddsSomething(fix.Text, a, fix.Edits))
            .ToList();
    }

    private IEnumerable<Inline> DescribeEdit(FixEdit edit)
    {
        if (edit.Replacement.Length == 0)
        {
            yield return new Run("remove ");
            yield return new Run(edit.Original.Trim()) { Foreground = _removedBrush, TextDecorations = TextDecorations.Strikethrough };
            yield break;
        }
        yield return new Run(edit.Original) { Foreground = _removedBrush, TextDecorations = TextDecorations.Strikethrough };
        yield return new Run("  " + (char)0x2192 + "  ") { Foreground = _mutedBrush };
        yield return new Run(edit.Replacement) { Foreground = _insertedBrush, FontWeight = FontWeights.SemiBold };
    }

    private IEnumerable<Inline> PreviewRuns(string text, TextRange range, IEnumerable<FixEdit> edits)
    {
        var previous = PreviewKind.Same;
        foreach (var segment in CompositeFix.Preview(text, range, edits))
        {
            // A thin space keeps "an" + "a" from reading as "ana"; it isn't part of the text.
            if (previous == PreviewKind.Removed && segment.Kind == PreviewKind.Inserted)
                yield return new Run(((char)0x2009).ToString());
            previous = segment.Kind;
            yield return segment.Kind switch
            {
                PreviewKind.Removed => new Run(segment.Text) { Foreground = _removedBrush, TextDecorations = TextDecorations.Strikethrough },
                PreviewKind.Inserted => new Run(segment.Text) { Foreground = _insertedBrush, FontWeight = FontWeights.SemiBold },
                _ => new Run(segment.Text),
            };
        }
    }

    private CheckBox NewCheckBox(TextBlock label, bool isChecked) => new()
    {
        Content = label,
        IsChecked = isChecked,
        Margin = new Thickness(0, 3, 0, 3),
        Foreground = (Brush)Resources["Flyout.Text"],
        VerticalContentAlignment = VerticalAlignment.Top,
    };

    private void SwitchScope(FixScope scope)
    {
        int index = _scopes.ToList().FindIndex(s => s.Scope == scope);
        if (index < 0 || index == _scope) return;
        _scope = index;
        BuildLists();
        ApplyButton.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        int digit = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1
                  : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;

        if (digit >= 0 && digit < _editBoxes.Count)
        {
            var box = _editBoxes[digit].Box;
            if (box.IsEnabled) box.IsChecked = box.IsChecked != true;
        }
        else if (e.Key == Key.Enter && ApplyButton.IsEnabled && Keyboard.FocusedElement is not Button { Name: not nameof(ApplyButton) })
            Complete(_chosen);
        else if (e.Key == Key.Escape)
            Complete(null);
        else if (e.Key == Key.S && ScopeButtons.IsVisible)
            SwitchScope(FixScope.Sentence);
        else if (e.Key == Key.P && ScopeButtons.IsVisible)
            SwitchScope(FixScope.Paragraph);
        else
            return; // Tab/arrows/Space drive the focused control
        e.Handled = true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Complete(_chosen);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Complete(null);
    private void Sentence_Click(object sender, RoutedEventArgs e) => SwitchScope(FixScope.Sentence);
    private void Paragraph_Click(object sender, RoutedEventArgs e) => SwitchScope(FixScope.Paragraph);

    private void Complete(IReadOnlyList<FixEdit>? edits)
    {
        if (!_result.TrySetResult(edits) || _closing) return;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true; // closing deactivates the window, which would call Close again
        _result.TrySetResult(null);
        base.OnClosing(e);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
