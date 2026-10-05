using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Redline.Analysis.Grmr;
using Redline.App.Corrections;
using Redline.App.Updates;
using Redline.Core.Corrections;
using Redline.Core.Diagnostics;
using Redline.Core.Interfaces;
using Redline.Core.Settings;
using Redline.Core.Updates;

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
    private readonly UpdateService _updates;
    private readonly GrmrModelStore _models;
    private readonly CompatibilityLog _compatibility;
    private readonly Func<Task<bool>> _removeModel;
    private readonly Func<string> _aiDeviceStatus;
    private readonly System.Windows.Threading.DispatcherTimer _aiDeviceTimer;
    private string? _modelNote;
    private bool _loading;

    public SettingsWindow(
        SettingsStore store, HotkeyManager hotkeys, IPersonalDictionary dictionary, IgnoreList ignores,
        IReadOnlyList<string> languages, string? activeLanguage, IReadOnlyCollection<string> builtInExclusions,
        string? logDirectory, UpdateService updates, GrmrModelStore models, Func<Task<bool>> removeModel,
        CompatibilityLog compatibility, Func<string> aiDeviceStatus)
    {
        _aiDeviceStatus = aiDeviceStatus;
        _compatibility = compatibility;
        _models = models;
        _removeModel = removeModel;
        _logDirectory = logDirectory;
        _updates = updates;
        _store = store;
        _hotkeys = hotkeys;
        _dictionary = dictionary;
        _ignores = ignores;
        _languages = languages;
        _activeLanguage = activeLanguage;
        _builtInExclusions = builtInExclusions;
#pragma warning disable WPF0001 // Fluent theme is experimental in .NET 9; System = follow Windows light/dark.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        InitializeComponent();

        LanguageBox.ItemsSource = languages;
        BuiltInList.ItemsSource = builtInExclusions.Order(StringComparer.OrdinalIgnoreCase).ToList();
        StartupHint.Text = RunningFromBuildFolder()
            ? "Redline is running from a build folder; signing in will start this copy until the setting is turned off."
            : string.Empty;
        LogsHint.Text = logDirectory ?? "Logging to files is unavailable.";
        VersionText.Text = $"Redline {App.Version.Split('+')[0]}";
        ShowUpdateState();
        ShowModelState();
        LoadCompatibility();
        LoadSettings(store.Current);

        // The model loads in the background, so where it runs is only known later.
        _aiDeviceTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _aiDeviceTimer.Tick += (_, _) => ShowAiDevice();
        _aiDeviceTimer.Start();
        LoadLists();

        // Settings can also change elsewhere (tray Pause); dictionary/ignores from the suggestion popup.
        Action<RedlineSettings, RedlineSettings> settingsChanged = (_, updated) => Dispatcher.BeginInvoke(() => LoadSettings(updated));
        Action listsChanged = () => Dispatcher.BeginInvoke(LoadLists);
        Action<Hotkey?> hotkeyChanged = _ => Dispatcher.BeginInvoke(() => UpdateHotkeyHint(_store.Current));
        Action updateChanged = () => Dispatcher.BeginInvoke(ShowUpdateState);
        Action modelChanged = () => Dispatcher.BeginInvoke(ShowModelState);
        Action compatibilityChanged = () => Dispatcher.BeginInvoke(LoadCompatibility);
        _updates.Changed += updateChanged;
        _models.Changed += modelChanged;
        _compatibility.Changed += compatibilityChanged;
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
            _updates.Changed -= updateChanged;
            _models.Changed -= modelChanged;
            _compatibility.Changed -= compatibilityChanged;
            _aiDeviceTimer.Stop();
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
            GutterBox.IsChecked = s.General.ParagraphGutter;
            DiagnosticsBox.IsChecked = s.General.DiagnosticsMode;
            UpdatesBox.IsChecked = s.General.CheckForUpdates;

            SpellingBox.IsChecked = s.Writing.Spelling;
            GrammarBox.IsChecked = s.Writing.Grammar;
            StyleBox.IsChecked = s.Writing.StyleSuggestions;
            AiGrammarBox.IsChecked = s.Writing.AiGrammar;
            AiGpuBox.IsChecked = s.Writing.AiGrammarUseGpu;
            ShowAiDevice();

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
        bool enabled = EnabledBox.IsChecked == true, startup = StartupBox.IsChecked == true, hover = HoverBox.IsChecked == true,
            gutter = GutterBox.IsChecked == true;
        _store.Update(s => s with { General = s.General with { Enabled = enabled, StartWithWindows = startup, HoverSuggestions = hover, ParagraphGutter = gutter } });
    }

    // ---- About / updates ----

    private void ShowUpdateState()
    {
        bool busy = _updates.State is UpdateState.Checking or UpdateState.Downloading;
        CheckNowButton.IsEnabled = !busy && _updates.State != UpdateState.Ready;
        InstallUpdateButton.Visibility = _updates.State == UpdateState.Ready ? Visibility.Visible : Visibility.Collapsed;
        if (_updates.Update is { } u)
            InstallUpdateButton.Content = $"Install {u.Version}";

        var status = _updates.Status;
        if (_updates.LastChecked is { } at && !busy)
            status += (status.Length > 0 ? " " : string.Empty) + $"Last checked {at:g}.";
        UpdateStatus.Text = status;
    }

    private void Updates_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = UpdatesBox.IsChecked == true;
        _store.Update(s => s with { General = s.General with { CheckForUpdates = on } });
    }

    private void CheckNow_Click(object sender, RoutedEventArgs e) => _ = _updates.CheckAsync();

    private void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!_updates.Install())
            UpdateStatus.Text = "That update isn't available any more. Check again.";
    }

    private void Notices_Click(object sender, RoutedEventArgs e)
    {
        // Installed copies ship the generated file; build-folder copies fall back to the repository.
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        if (File.Exists(notices))
            Process.Start(new ProcessStartInfo(notices) { UseShellExecute = true });
        else
            App.OpenUrl(new Uri($"https://github.com/{UpdateCatalog.Owner}/{UpdateCatalog.Repository}#license"));
    }

    private void GitHub_Click(object sender, RoutedEventArgs e) =>
        App.OpenUrl(new Uri($"https://github.com/{UpdateCatalog.Owner}/{UpdateCatalog.Repository}"));

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
        bool spelling = SpellingBox.IsChecked == true, grammar = GrammarBox.IsChecked == true,
            style = StyleBox.IsChecked == true, ai = AiGrammarBox.IsChecked == true, gpu = AiGpuBox.IsChecked == true;
        // Turning AI grammar on starts the model download (App.ApplyAiGrammar).
        _store.Update(s => s with
        {
            Writing = s.Writing with
            {
                Spelling = spelling, Grammar = grammar, StyleSuggestions = style, AiGrammar = ai, AiGrammarUseGpu = gpu,
            },
        });
    }

    /// <summary>Brings a tab to the front by its header ("Apps", "Writing", ...).</summary>
    public void ShowTab(string header)
    {
        if (Tabs.Items.OfType<TabItem>().FirstOrDefault(t => Equals(t.Header, header)) is { } tab)
            tab.IsSelected = true;
    }

    private void ShowAiDevice() => AiDeviceHint.Text = _aiDeviceStatus();

    // ---- Compatibility ----

    private sealed record CompatRow(string App, string Status, string LastSeen, string Details, string Key);

    private void LoadCompatibility()
    {
        CompatGrid.ItemsSource = _compatibility.Entries()
            .Where(e => e.Attaches > 0 || e.Blocked.Count > 0)
            .Select(e => new CompatRow(
                e.Process,
                CompatibilityLog.StatusOf(e),
                e.LastSeen,
                $"Version {e.AppVersion ?? "unknown"}{Environment.NewLine}Field: {string.Join(" / ", new[] { e.ControlType, e.ClassName, e.Framework }.Where(s => s.Length > 0))}" +
                $"{Environment.NewLine}Underlines placed {e.IssuesPlaced} of {e.IssuesPlaced + e.IssuesNotPlaced}; fixes applied {e.FixesApplied}, problems {e.FixProblems.Values.Sum()}",
                e.Key))
            .ToList();
    }

    private void CompatReport_Click(object sender, RoutedEventArgs e)
    {
        _compatibility.Save();
        new ReportWindow("Redline " + (char)0x2014 + " Compatibility report",
            "This is the complete record, exactly as stored in " + CompatibilityLog.DefaultPath + ". It is not sent anywhere.",
            _compatibility.ToJson()) { Owner = this }.ShowDialog();
    }

    private void CompatClear_Click(object sender, RoutedEventArgs e) => _compatibility.Clear();

    private void CompatGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        CompatIssueButton.IsEnabled = CompatGrid.SelectedItem is CompatRow;

    /// <summary>Opens GitHub's new-issue form pre-filled with the selected app's entry; nothing is sent until the user submits.</summary>
    private void CompatIssue_Click(object sender, RoutedEventArgs e)
    {
        if (CompatGrid.SelectedItem is not CompatRow row) return;
        var entry = _compatibility.Entries().FirstOrDefault(x => x.Key == row.Key);
        if (entry is null) return;
        var url = CompatibilityIssue.Url(UpdateCatalog.Owner, UpdateCatalog.Repository, _compatibility.Report() with { Apps = [] }, entry);
        if (url is null)
        {
            MessageDialog.ShowInfo("This app's record is too large for a pre-filled issue. Use View report and copy the part you need.", this);
            return;
        }
        App.OpenUrl(url);
    }

    private void ShowModelState()
    {
        var state = _models.State;
        ModelDownloadButton.Visibility = state is ModelState.NotInstalled or ModelState.Failed ? Visibility.Visible : Visibility.Collapsed;
        ModelCancelButton.Visibility = state == ModelState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        ModelRemoveButton.Visibility = state == ModelState.Installed ? Visibility.Visible : Visibility.Collapsed;
        ModelProgress.Visibility = state == ModelState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        ModelProgress.Value = _models.Progress;
        ModelStatus.Text = _modelNote ?? state switch
        {
            ModelState.Downloading => $"Downloading{(char)0x2026} {_models.Progress:P0} of {_models.Size / 1_000_000} MB",
            ModelState.Installed => $"Installed ({_models.Size / 1_000_000} MB).",
            ModelState.Failed => _models.Error ?? "The download failed.",
            _ => "Not downloaded yet.",
        };
        ModelStatus.ToolTip = state == ModelState.Installed ? _models.ModelPath : null;
        _modelNote = null;
    }

    private void ModelDownload_Click(object sender, RoutedEventArgs e) => _models.StartDownload();

    private void ModelCancel_Click(object sender, RoutedEventArgs e) => _models.CancelDownload();

    private async void ModelRemove_Click(object sender, RoutedEventArgs e)
    {
        ModelRemoveButton.IsEnabled = false;
        try
        {
            if (!await _removeModel())
            {
                _modelNote = "The model file is still in use. Try again in a moment.";
                ShowModelState();
            }
        }
        finally
        {
            ModelRemoveButton.IsEnabled = true;
        }
    }

    private void ModelPage_Click(object sender, RoutedEventArgs e) => App.OpenUrl(new Uri(GrmrModel.ModelPage));

    private void GemmaTerms_Click(object sender, RoutedEventArgs e) => App.OpenUrl(new Uri(GrmrModel.BaseModelTerms));

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

    private void WordImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import words",
            Filter = "Dictionaries and word lists (*.dic;*.txt)|*.dic;*.txt|All files (*.*)|*.*",
            InitialDirectory = WordListFile.OfficeDictionaryFolder ?? string.Empty,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var words = WordListFile.Read(dialog.FileName);
            int added = _dictionary.AddRange(words);
            string name = Path.GetFileName(dialog.FileName);
            ImportResult.Text = words.Count == 0
                ? $"No words found in {name}."
                : $"Added {added} of {words.Count} word{(words.Count == 1 ? "" : "s")} from {name}" +
                  (added < words.Count ? $" ({words.Count - added} already there)." : ".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ImportResult.Text = "Couldn't read that file: " + ex.Message;
        }
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
