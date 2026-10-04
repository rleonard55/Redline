using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Redline.App.Corrections;
using Redline.Core.Corrections;
using Redline.Core.Interfaces;
using Redline.Core.Settings;

namespace Redline.App.Settings;

/// <summary>
/// Settings UI. Every change is saved immediately through <see cref="SettingsStore"/>; the app's
/// Changed handler applies it. Personal dictionary and ignored rules are edited in their own stores.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly HotkeyManager _hotkeys;
    private readonly IPersonalDictionary _dictionary;
    private readonly IgnoreList _ignores;
    private readonly IReadOnlyList<string> _languages;
    private readonly string? _activeLanguage;
    private readonly IReadOnlyCollection<string> _builtInExclusions;
    private readonly string? _logDirectory;
    private bool _loading;

    public SettingsWindow(
        SettingsStore store, HotkeyManager hotkeys, IPersonalDictionary dictionary, IgnoreList ignores,
        IReadOnlyList<string> languages, string? activeLanguage, IReadOnlyCollection<string> builtInExclusions,
        string? logDirectory)
    {
        _logDirectory = logDirectory;
        _store = store;
        _hotkeys = hotkeys;
        _dictionary = dictionary;
        _ignores = ignores;
        _languages = languages;
        _activeLanguage = activeLanguage;
        _builtInExclusions = builtInExclusions;
        InitializeComponent();

        LanguageBox.ItemsSource = languages;
        BuiltInList.ItemsSource = builtInExclusions.Order(StringComparer.OrdinalIgnoreCase).ToList();
        StartupHint.Text = RunningFromBuildFolder()
            ? "Redline is running from a build folder; signing in will start this copy until the setting is turned off."
            : string.Empty;
        LogsHint.Text = logDirectory ?? "Logging to files is unavailable.";
        LoadSettings(store.Current);
        LoadLists();

        // Settings can also change elsewhere (tray Pause); dictionary/ignores from the suggestion popup.
        Action<RedlineSettings, RedlineSettings> settingsChanged = (_, updated) => Dispatcher.BeginInvoke(() => LoadSettings(updated));
        Action listsChanged = () => Dispatcher.BeginInvoke(LoadLists);
        Action<Hotkey?> hotkeyChanged = _ => Dispatcher.BeginInvoke(() => UpdateHotkeyHint(_store.Current));
        _store.Changed += settingsChanged;
        _dictionary.Changed += listsChanged;
        _ignores.Changed += listsChanged;
        _hotkeys.ActiveChanged += hotkeyChanged;
        Closed += (_, _) =>
        {
            _store.Changed -= settingsChanged;
            _dictionary.Changed -= listsChanged;
            _ignores.Changed -= listsChanged;
            _hotkeys.ActiveChanged -= hotkeyChanged;
            _hotkeys.Suspended = false;
        };
    }

    private void LoadSettings(RedlineSettings s)
    {
        _loading = true;
        try
        {
            EnabledBox.IsChecked = s.General.Enabled;
            if (!LanguageBox.IsKeyboardFocusWithin)
                LanguageBox.Text = s.General.Language;
            UpdateLanguageHint(s.General.Language);
            DelaySlider.Value = s.General.AnalysisDelayMs;
            DelayText.Text = $"{s.General.AnalysisDelayMs} ms";
            if (!HotkeyBox.IsKeyboardFocused)
                HotkeyBox.Text = s.General.Hotkey;
            UpdateHotkeyHint(s);
            StartupBox.IsChecked = s.General.StartWithWindows;
            HoverBox.IsChecked = s.General.HoverSuggestions;
            DiagnosticsBox.IsChecked = s.General.DiagnosticsMode;

            SpellingBox.IsChecked = s.Writing.Spelling;
            GrammarBox.IsChecked = s.Writing.Grammar;
            StyleBox.IsChecked = s.Writing.StyleSuggestions;

            ExcludedList.ItemsSource = s.Applications.Excluded;
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadLists()
    {
        WordList.ItemsSource = _dictionary.Words.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        RuleList.ItemsSource = _ignores.IgnoredRules.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- General ----

    private void General_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool enabled = EnabledBox.IsChecked == true, startup = StartupBox.IsChecked == true, hover = HoverBox.IsChecked == true;
        _store.Update(s => s with { General = s.General with { Enabled = enabled, StartWithWindows = startup, HoverSuggestions = hover } });
    }

    // ---- Diagnostics ----

    private void Diagnostics_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = DiagnosticsBox.IsChecked == true;
        _store.Update(s => s with { General = s.General with { DiagnosticsMode = on } });
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_logDirectory is null || !Directory.Exists(_logDirectory)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_logDirectory}\"") { UseShellExecute = true });
    }

    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && LanguageBox.SelectedItem is string tag)
            SaveLanguage(tag);
    }

    private void Language_Commit(object sender, RoutedEventArgs e)
    {
        if (!_loading && !LanguageBox.IsKeyboardFocusWithin)
            SaveLanguage(LanguageBox.Text);
    }

    private void SaveLanguage(string tag)
    {
        var updated = _store.Update(s => s with { General = s.General with { Language = tag } });
        UpdateLanguageHint(updated.General.Language);
    }

    private void UpdateLanguageHint(string configured)
    {
        string hint = _languages.Count > 0 && !_languages.Contains(configured, StringComparer.OrdinalIgnoreCase)
            ? $"{configured} has no Windows spelling dictionary installed; English (en-US) will be used. "
            : string.Empty;
        if (!string.Equals(configured, _activeLanguage, StringComparison.OrdinalIgnoreCase))
            hint += $"Applies after Redline restarts (in use now: {_activeLanguage ?? "none"}).";
        LanguageHint.Text = hint;
    }

    private void Delay_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return; // Minimum/Maximum raise this while the XAML is still loading
        int ms = (int)DelaySlider.Value;
        DelayText.Text = $"{ms} ms";
        if (_loading) return;
        _store.Update(s => s with { General = s.General with { AnalysisDelayMs = ms } });
    }

    // ---- Hotkey capture ----

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Pressing the current hotkey while choosing a new one shouldn't open the suggestion popup.
        _hotkeys.Suspended = true;
        HotkeyHint.Text = "Press the new combination (Ctrl, Alt or Win plus a key). Esc cancels.";
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _hotkeys.Suspended = false;
        HotkeyBox.Text = _store.Current.General.Hotkey;
        UpdateHotkeyHint(_store.Current);
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (modifiers == ModifierKeys.None && key == Key.Tab) return; // keep keyboard navigation
        e.Handled = true;

        if (modifiers == ModifierKeys.None && key == Key.Escape)
        {
            HotkeyBox.Text = _store.Current.General.Hotkey;
            Keyboard.ClearFocus();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return; // wait for the non-modifier key
        }

        var mods = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        var candidate = new Hotkey(mods, KeyInterop.VirtualKeyFromKey(key));

        // Only accept what the settings file can store (and Ctrl/Alt/Win is required).
        if (!Hotkey.TryParse(candidate.ToString(), out var parsed) || parsed != candidate)
        {
            HotkeyHint.Text = $"{candidate} can't be used. Hold Ctrl, Alt or Win and press a letter, digit, F-key or punctuation.";
            return;
        }

        ApplyHotkey(candidate);
    }

    private void HotkeyReset_Click(object sender, RoutedEventArgs e)
    {
        if (Hotkey.TryParse(GeneralSettings.DefaultHotkey, out var hk))
            ApplyHotkey(hk);
    }

    private void ApplyHotkey(Hotkey hotkey)
    {
        // Register first: if another app owns it, the old hotkey stays and nothing is saved.
        if (!_hotkeys.TryChangeHotkey(hotkey))
        {
            HotkeyBox.Text = _store.Current.General.Hotkey;
            HotkeyHint.Text = $"{hotkey} is used by another app. Try a different combination.";
            return;
        }

        var updated = _store.Update(s => s with { General = s.General with { Hotkey = hotkey.ToString() } });
        HotkeyBox.Text = updated.General.Hotkey;
        UpdateHotkeyHint(updated);
    }

    private void UpdateHotkeyHint(RedlineSettings s)
    {
        if (HotkeyBox.IsKeyboardFocused) return;
        var active = _hotkeys.Active;
        HotkeyHint.Text = active is null
            ? $"{s.General.Hotkey} is used by another app, so no suggestion hotkey is active."
            : active.Value.ToString() != s.General.Hotkey
                ? $"{s.General.Hotkey} is used by another app; {active} is active instead."
                : "Press it with the cursor in a flagged word to see suggestions.";
    }

    // ---- Writing ----

    private void Writing_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var writing = new WritingSettings
        {
            Spelling = SpellingBox.IsChecked == true,
            Grammar = GrammarBox.IsChecked == true,
            StyleSuggestions = StyleBox.IsChecked == true,
        };
        _store.Update(s => s with { Writing = writing });
    }

    // ---- Apps ----

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (ExcludeInput.IsKeyboardFocusWithin) return;
        string text = ExcludeInput.Text;
        ExcludeInput.ItemsSource = RunningAppNames();
        ExcludeInput.Text = text;
    }

    private List<string> RunningAppNames()
    {
        var excluded = new HashSet<string>(_store.Current.Applications.Excluded, StringComparer.OrdinalIgnoreCase);
        excluded.UnionWith(_builtInExclusions);
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero || p.Id == Environment.ProcessId) continue;
                    var name = RedlineSettings.NormalizeProcessName(p.ProcessName);
                    if (!excluded.Contains(name)) names.Add(name);
                }
                catch (InvalidOperationException)
                {
                    // exited while enumerating
                }
            }
        }
        return names.ToList();
    }

    private void ExcludeAdd_Click(object sender, RoutedEventArgs e)
    {
        var name = RedlineSettings.NormalizeProcessName(ExcludeInput.Text);
        if (name.Length == 0) return;
        _store.Update(s => s with { Applications = s.Applications with { Excluded = [.. s.Applications.Excluded, name] } });
        ExcludeInput.Text = string.Empty;
        ExcludeInput.ItemsSource = RunningAppNames();
    }

    private void ExcludeRemove_Click(object sender, RoutedEventArgs e)
    {
        var remove = ExcludedList.SelectedItems.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (remove.Count == 0) return;
        _store.Update(s => s with
        {
            Applications = s.Applications with { Excluded = s.Applications.Excluded.Where(p => !remove.Contains(p)).ToList() },
        });
    }

    // ---- Dictionary ----

    private void WordRemove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var word in WordList.SelectedItems.Cast<string>().ToList())
            _dictionary.Remove(word);
    }

    private void RuleRestore_Click(object sender, RoutedEventArgs e)
    {
        foreach (var rule in RuleList.SelectedItems.Cast<string>().ToList())
            _ignores.RestoreRule(rule);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static bool RunningFromBuildFolder()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty;
        var sep = Path.DirectorySeparatorChar;
        return dir.Contains($"{sep}bin{sep}Debug{sep}", StringComparison.OrdinalIgnoreCase)
            || dir.Contains($"{sep}bin{sep}Release{sep}", StringComparison.OrdinalIgnoreCase);
    }
}
