using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Redline.App;

/// <summary>
/// Shown once, on the first start of a fresh install: Redline has no main window, so without this a new
/// user sees only a tray icon and doesn't know about the hover fix, the hotkey or the optional AI model.
/// </summary>
internal sealed class WelcomeWindow : Window
{
    private const string EditGlyph = "";
    private const string KeyboardGlyph = "";
    private const string SettingsGlyph = "";
    private const string LightbulbGlyph = "";

    /// <param name="hotkey">The active suggestion hotkey, or null if none could be registered.</param>
    /// <param name="hoverSuggestions">Whether resting the pointer on an underline shows a quick fix.</param>
    /// <param name="aiGrammarOn">AI grammar is already on (then its offer is left out).</param>
    /// <param name="turnOnAiGrammar">Turns AI grammar on (which starts the model download).</param>
    /// <param name="openSettings">Opens Settings.</param>
    public WelcomeWindow(string? hotkey, bool hoverSuggestions, bool aiGrammarOn, Action turnOnAiGrammar, Action openSettings)
    {
#pragma warning disable WPF0001 // Fluent theme is experimental in .NET 9; System = follow Windows light/dark.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        Title = "Welcome to Redline";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = "Welcome to Redline", FontSize = 22, FontWeight = FontWeights.SemiBold });
        root.Children.Add(Hint(
            "Redline checks spelling and grammar as you type in most apps: Word, Outlook, Teams, Notepad, web pages and more. " +
            "Everything is checked on this PC; your text never leaves it.", top: 6));

        if (hoverSuggestions)
            root.Children.Add(Tip(EditGlyph, "Fix a word", "Rest the pointer on an underlined word to see the best suggestion; click it to apply."));
        // The shortcut is bold so its last key ("." by default) doesn't read as punctuation.
        root.Children.Add(Tip(KeyboardGlyph, "All suggestions",
            hotkey is null
                ? "Put the cursor in an underlined word and use the suggestion shortcut (set it in Settings > General)."
                : "Put the cursor in an underlined word and press {key} to see every suggestion, or to ignore it or add it to the dictionary.",
            key: hotkey));
        root.Children.Add(Tip(SettingsGlyph, "Find Redline in the tray",
            "Its icon sits by the clock (click ^ if it's hidden). Right-click it to pause, open Settings, or stop checking in one app."));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        if (!aiGrammarOn)
        {
            var ai = Tip(LightbulbGlyph, "Optional: AI grammar check",
                "A small AI model on this PC catches errors the built-in rules miss, like \"She go to school\". " +
                "It needs a one-time 806 MB download and uses the graphics card when there is one.");
            var turnOn = new Button { Content = "Turn on AI grammar", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            var done = Hint("Downloading in the background. Settings > Writing shows the progress.", top: 8);
            done.Visibility = Visibility.Collapsed;
            turnOn.Click += (_, _) =>
            {
                turnOnAiGrammar();
                turnOn.Visibility = Visibility.Collapsed;
                done.Visibility = Visibility.Visible;
            };
            var text = (StackPanel)((DockPanel)ai).Children[1];
            text.Children.Add(turnOn);
            text.Children.Add(done);
            root.Children.Add(ai);
        }

        var settings = new Button { Content = "Open Settings", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        settings.Click += (_, _) =>
        {
            Close();
            openSettings();
        };
        var start = new Button { Content = "Get started", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, IsCancel = true };
        start.SetResourceReference(StyleProperty, "AccentButtonStyle");
        start.Click += (_, _) => Close();
        buttons.Children.Add(settings);
        buttons.Children.Add(start);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) =>
        {
            Activate();
            start.Focus();
        };
    }

    private static TextBlock Hint(string text, double top)
    {
        var hint = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return hint;
    }

    /// <param name="key">Shown in bold in place of "{key}" in <paramref name="body"/>.</param>
    private static FrameworkElement Tip(string glyph, string title, string body, string? key = null)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 20,
            Width = 36,
            Margin = new Thickness(0, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        var hint = Hint(string.Empty, top: 2);
        var parts = body.Split("{key}");
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) hint.Inlines.Add(new System.Windows.Documents.Run(key) { FontWeight = FontWeights.SemiBold });
            hint.Inlines.Add(new System.Windows.Documents.Run(parts[i]));
        }
        text.Children.Add(hint);

        var row = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
    }
}
