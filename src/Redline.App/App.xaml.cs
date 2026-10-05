using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Redline.Analysis;
using Redline.Analysis.Grmr;
using Redline.Analysis.Harper;
using Redline.Annotations;
using Redline.App.Corrections;
using Redline.App.Diagnostics;
using Redline.App.Logging;
using Redline.App.Settings;
using Redline.App.TrayIcon;
using Redline.App.Updates;
using Redline.Core.Corrections;
using Redline.Core.Diagnostics;
using Redline.Core.Interfaces;
using Redline.Core.Pipeline;
using Redline.Core.Settings;
using Redline.Core.Updates;
using Redline.Windows;
using Redline.Windows.Automation;
using Redline.Windows.Corrections;

namespace Redline.App;

public partial class App : Application
{
    private const string SingleInstanceName = @"Local\Redline.App.SingleInstance";
    private const string ExitEventName = @"Local\Redline.App.Exit";

    private Mutex? _singleInstance;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitWait;
    private ServiceProvider? _services;
    private TrayIconHost? _tray;
    private DiagnosticsWindow? _diagnosticsWindow;
    private SettingsWindow? _settingsWindow;
    private HotkeyManager? _hotkeys;
    private DiagnosticsLog? _log;
    private DispatcherTimer? _perfTimer;
    private DispatcherTimer? _updateTimer;
    private UpdateService? _updates;
    private Version? _notifiedUpdate;
    private ModelState _modelState;
    private DispatcherTimer? _compatibilityTimer;
    private readonly Dictionary<int, string?> _appVersions = new();
    private IReadOnlyDictionary<string, long> _lastPerfCounts = new Dictionary<string, long>();
    private DateTime _lastPerfAt = DateTime.UtcNow;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    private static readonly TimeSpan PerfSummaryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    public static string Version { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    /// <summary>The suggestion hotkey actually registered, for UI hints.</summary>
    public static string? HotkeyName { get; private set; }
    private ILogger<App>? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(ExitRunningInstance() ? 0 : 1);
            return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out bool createdNew);
        if (!createdNew)
        {
            MessageDialog.ShowInfo("Redline is already running (see the system tray).");
            Shutdown();
            return;
        }

        // The installer runs "Redline.exe --exit" before replacing or removing files.
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _logger?.LogInformation("Exit requested by another process (installer)");
            Shutdown();
        }), null, Timeout.Infinite, executeOnlyOnce: true);

        var log = _log = new DiagnosticsLog(DiagnosticsLog.DefaultDirectory);
        _services = ConfigureServices(log);
        _logger = _services.GetRequiredService<ILogger<App>>();

        int unhandledCount = 0;
        DateTimeOffset lastUnhandled = DateTimeOffset.MinValue;
        DispatcherUnhandledException += (_, args) =>
        {
            var now = DateTimeOffset.Now;
            if ((now - lastUnhandled).TotalSeconds < 1)
            {
                if (++unhandledCount > 5)
                {
                    args.Handled = true;
                    return;
                }
            }
            else
            {
                unhandledCount = 1;
                lastUnhandled = now;
            }

            _logger.LogError(args.Exception, "Unhandled UI exception");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            _logger.LogCritical(args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", args.IsTerminating);
            WriteCrashReport(args.ExceptionObject as Exception, "Unhandled exception (AppDomain)", args.IsTerminating);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogWarning(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        var tracker = _services.GetRequiredService<SurfaceTracker>();
        var pipeline = _services.GetRequiredService<AnalysisPipeline>();

        var cache = _services.GetRequiredService<IssueCacheManager>();
        tracker.SnapshotChanged += (_, s) => pipeline.Submit(s.Surface, s.Snapshot);
        pipeline.AnalysisCompleted += (_, r) => cache.Update(r.Surface.SurfaceId, r.Issues);
        tracker.SurfaceChanged += (_, s) =>
        {
            if (s.Surface is null) pipeline.Clear();
            else _lastApp = new LastApp(s.Surface.ProcessName, AppDisplayNameOf(s.Surface.ProcessId, s.Surface.ProcessName));
        };

        // Per-app compatibility record (local only; Settings > Compatibility). Identity and counts, never text.
        var compatibility = _services.GetRequiredService<CompatibilityLog>();
        tracker.SurfaceChanged += (_, s) =>
        {
            if (s.Surface is { } surface && s.Capabilities is { } caps)
                compatibility.Attached(surface, caps, AppVersionOf(surface.ProcessId));
        };
        tracker.SnapshotChanged += (_, s) => compatibility.TextRead(s.Surface, s.Snapshot.Length == 0);
        tracker.SurfaceBlocked += (_, b) => compatibility.Blocked(b.ProcessName, b.ControlType, b.ClassName, b.FrameworkId, b.Reason);
        _compatibilityTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _compatibilityTimer.Tick += (_, _) => compatibility.Save();
        _compatibilityTimer.Start();

        // The grammar model works in the background; re-run analysis when it has new suggestions.
        var grmr = _services.GetRequiredService<GrmrAnalyzer>();
        grmr.ResultsReady += pipeline.Refresh;
        var models = _services.GetRequiredService<GrmrModelStore>();
        _modelState = models.State;
        models.Changed += () => Dispatcher.BeginInvoke(OnModelChanged);

        // Constructed now so it captures events from the start, even before the window opens.
        _services.GetRequiredService<DiagnosticsViewModel>();

        foreach (var analyzer in pipeline.Analyzers)
            _logger.LogInformation("Analyzer {Name}: {State}", analyzer.Name, analyzer.IsAvailable ? "available" : "unavailable");

        var settings = _services.GetRequiredService<SettingsStore>();
        _tray = new TrayIconHost(
            () => ShowSettings(), ShowDiagnostics,
            () => settings.Update(st => st with { General = st.General with { Enabled = !st.General.Enabled } }),
            Shutdown,
            () => _lastApp is { } app && !AppExclusion.IsExcluded(settings.Current, app.ProcessName) ? app.DisplayName : null,
            ExcludeLastApp);

        var corrections = _services.GetRequiredService<CorrectionController>();
        corrections.Notify += (message, isError) => _tray.Notify(message, isError);

        if (_services.GetRequiredService<GpuGuard>().CrashedLastTime)
        {
            _logger.LogWarning("The previous run ended while the grammar model was starting on the GPU; using the CPU");
            _tray.Notify("Redline closed unexpectedly while starting the AI grammar model on the graphics card. It now runs on the processor (Settings > Writing).", true);
        }

        if (log.LogDirectory is { } logDir && CrashReport.TakePending(logDir) is { } report)
        {
            _logger.LogWarning("The previous run ended unexpectedly; crash report {Report}", System.IO.Path.GetFileName(report));
            _tray.Notify("Redline closed unexpectedly last time. A crash report was saved in the logs folder.", true);
        }

        var perf = _services.GetRequiredService<PerfCounters>();
        pipeline.AnalysisCompleted += (_, r) =>
        {
            perf.Record("analysis", r.Duration);
            foreach (var (name, duration) in r.AnalyzerDurations)
                perf.Record("analysis." + name, duration);
        };
        _perfTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = PerfSummaryInterval };
        _perfTimer.Tick += (_, _) => LogPerfSummary();

        _updates = _services.GetRequiredService<UpdateService>();
        _updates.Changed += () => Dispatcher.BeginInvoke(OnUpdateChanged);
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = FirstUpdateCheck };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = UpdateCheckInterval;
            _ = _updates.CheckAsync();
        };

        // Ctrl+Alt+. (the default) echoes the familiar Ctrl+. "quick fix" without shadowing it in VS Code/Office.
        _hotkeys = new HotkeyManager(() => _ = corrections.ShowForCaretAsync(), _logger);
        _hotkeys.ActiveChanged += active =>
        {
            HotkeyName = active?.ToString();
            _tray.SetHotkeyHint(HotkeyName);
        };
        Hotkey.TryParse(settings.Current.General.Hotkey, out var configuredHotkey); // Validated() guarantees it parses
        if (_hotkeys.RegisterConfigured(configuredHotkey) is { } hotkeyProblem)
            _tray.Notify(hotkeyProblem, _hotkeys.Active is null);

        SyncStartWithWindows(settings);
        ApplySettings(null, settings.Current);
        settings.Changed += (old, updated) => Dispatcher.BeginInvoke(() => ApplySettings(old, updated));

        var overlay = _services.GetRequiredService<OverlayManager>();
        overlay.ApplyRequested += issue => _ = corrections.ApplyFirstSuggestionAsync(issue);
        overlay.MoreRequested += issue => _ = corrections.ShowForIssueAsync(issue);
        overlay.ParagraphFixRequested += request => _ = corrections.ShowParagraphFixAsync(request.Paragraph, request.SnapshotVersion, request.Anchor);
        overlay.Start();

        try
        {
            await tracker.StartAsync();
            _logger.LogInformation("Redline started");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to start focus tracking");
            MessageDialog.ShowError($"Redline could not start focus tracking:\n{ex.Message}");
        }

        if (e.Args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase))
            ShowDiagnostics();
        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            ShowSettings();
        ShowWelcomeIfFirstRun(e.Args.Contains("--welcome", StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The welcome window, once, on the first start of a fresh install (or always with --welcome). Upgrades
    /// from versions without it (a settings file already existed) only record it as seen.
    /// </summary>
    private void ShowWelcomeIfFirstRun(bool force)
    {
        var settings = _services!.GetRequiredService<SettingsStore>();
        if (!force && settings.Current.General.WelcomeShown) return;
        settings.Update(s => s with { General = s.General with { WelcomeShown = true } });
        if (!force && !settings.IsNew) return;

        var s = settings.Current;
        new WelcomeWindow(HotkeyName, s.General.HoverSuggestions, s.Writing.AiGrammar,
            turnOnAiGrammar: () => settings.Update(st => st with { Writing = st.Writing with { AiGrammar = true } }),
            openSettings: () => ShowSettings()).Show();
    }

    /// <summary>The app Redline last attached to (for the tray's "Don't check in ..."). No text, just the process.</summary>
    private sealed record LastApp(string ProcessName, string DisplayName);

    private volatile LastApp? _lastApp;
    private readonly Dictionary<string, string> _appDisplayNames = new(StringComparer.OrdinalIgnoreCase);

    private string AppDisplayNameOf(int processId, string processName)
    {
        lock (_appDisplayNames)
        {
            if (_appDisplayNames.TryGetValue(processName, out var cached)) return cached;
            string? description = null;
            try
            {
                using var p = Process.GetProcessById(processId);
                description = p.MainModule?.FileVersionInfo.FileDescription;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited, or elevated (no access to its modules): fall back to the process name.
            }
            return _appDisplayNames[processName] = AppExclusion.DisplayName(processName, description);
        }
    }

    private void ExcludeLastApp()
    {
        if (_lastApp is not { } app || _services is null) return;
        _services.GetRequiredService<SettingsStore>().Update(s => AppExclusion.Exclude(s, app.ProcessName));
        _logger?.LogInformation("Excluded {Process} from the tray", app.ProcessName);
        _tray?.Notify($"Redline won't check {app.DisplayName} anymore. Click here to undo it in Settings > Apps.",
            onClick: () => ShowSettings("Apps"));
    }

    /// <summary>Applies settings at startup (<paramref name="old"/> null) and after each change. UI thread.</summary>
    private void ApplySettings(RedlineSettings? old, RedlineSettings s)
    {
        if (_services is null || _tray is null || _hotkeys is null) return;

        // Resuming schedules a focus evaluation, which must not happen before the tracker has started.
        if (old is null ? !s.General.Enabled : old.General.Enabled != s.General.Enabled)
        {
            _services.GetRequiredService<SurfaceTracker>().SetPaused(!s.General.Enabled);
            _logger?.LogInformation("Checking {State}", s.General.Enabled ? "resumed" : "paused");
        }
        _tray.SetPaused(!s.General.Enabled);
        _services.GetRequiredService<IssueCacheManager>().SetWriting(s.Writing);
        ApplyAiGrammar(old, s);
        _services.GetRequiredService<AnalysisPipeline>().Debounce = TimeSpan.FromMilliseconds(s.General.AnalysisDelayMs);
        _services.GetRequiredService<SecurityFilter>().SetUserExclusions(s.Applications.Excluded);
        if (old is not null && !old.Applications.Excluded.SequenceEqual(s.Applications.Excluded, StringComparer.OrdinalIgnoreCase))
            _services.GetRequiredService<SurfaceTracker>().Reevaluate(); // the current app may be excluded now
        _services.GetRequiredService<OverlayManager>().HoverEnabled = s.General.HoverSuggestions;
        _services.GetRequiredService<OverlayManager>().GutterEnabled = s.General.ParagraphGutter;

        if (_log is not null) _log.FileLevel = s.General.DiagnosticsMode ? LogLevel.Debug : LogLevel.Information;
        // Timers only start/stop on an actual change; Start() on a running timer would restart its interval.
        if (old is null ? s.General.DiagnosticsMode : old.General.DiagnosticsMode != s.General.DiagnosticsMode)
        {
            if (s.General.DiagnosticsMode) _perfTimer?.Start(); else _perfTimer?.Stop();
            _logger?.LogInformation("Diagnostics mode: {Enabled} (Redline {Version})", s.General.DiagnosticsMode, Version);
        }
        if (old is null || old.General.CheckForUpdates != s.General.CheckForUpdates)
        {
            if (s.General.CheckForUpdates) _updateTimer?.Start(); else _updateTimer?.Stop();
        }

        // The settings window registers a new hotkey before saving it; this covers hand edits of settings.json.
        if (old is not null && old.General.Hotkey != s.General.Hotkey
            && Hotkey.TryParse(s.General.Hotkey, out var hotkey) && !_hotkeys.TryChangeHotkey(hotkey))
        {
            _tray.Notify($"{hotkey} is used by another app; keeping {_hotkeys.Active?.ToString() ?? "no hotkey"}.", true);
        }

        // Only an actual change writes the Run key; at startup the setting was synced from it instead.
        if (old is not null && old.General.StartWithWindows != s.General.StartWithWindows)
        {
            try
            {
                _services.GetRequiredService<StartupRegistration>().Apply(s.General.StartWithWindows, Environment.ProcessPath!);
                _logger?.LogInformation("Start with Windows: {Enabled}", s.General.StartWithWindows);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                _logger?.LogWarning("Couldn't update the Run key: {Reason}", ex.Message);
                _tray.Notify("Couldn't change the start-with-Windows setting.", true);
            }
        }
    }

    /// <summary>
    /// Turning AI grammar on starts the model download if needed (the setting says it downloads), and
    /// re-analyzes so suggestions appear without typing. At startup a missing model is not fetched
    /// silently; Settings offers the download.
    /// </summary>
    private void ApplyAiGrammar(RedlineSettings? old, RedlineSettings s)
    {
        var services = _services!;
        var grmr = services.GetRequiredService<GrmrAnalyzer>();
        grmr.Enabled = s.Writing.AiGrammar;
        if (old is not null && old.Writing.AiGrammarUseGpu != s.Writing.AiGrammarUseGpu)
        {
            _logger?.LogInformation("AI grammar on the GPU: {Enabled}", s.Writing.AiGrammarUseGpu);
            if (s.Writing.AiGrammarUseGpu)
                services.GetRequiredService<GpuGuard>().Reset(); // the user asked to try again
            _ = grmr.ReleaseModelAsync(); // reloads on the next sentence, on the new device
        }
        if (old is null || old.Writing.AiGrammar == s.Writing.AiGrammar) return;

        _logger?.LogInformation("AI grammar: {Enabled}", s.Writing.AiGrammar);
        if (!s.Writing.AiGrammar) return;
        var models = services.GetRequiredService<GrmrModelStore>();
        if (models.State is ModelState.NotInstalled or ModelState.Failed)
            models.StartDownload();
        else
            services.GetRequiredService<AnalysisPipeline>().Refresh();
    }

    private static ServiceProvider ConfigureServices(DiagnosticsLog log)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sp => new SettingsStore(SettingsStore.DefaultPath, sp.GetRequiredService<ILogger<SettingsStore>>()));
        services.AddSingleton(_ => new StartupRegistration());

        services.AddSingleton(log);
        services.AddSingleton<PerfCounters>();
        services.AddSingleton(sp => new CompatibilityLog(CompatibilityLog.DefaultPath, Version.Split('+')[0], sp.GetRequiredService<ILogger<CompatibilityLog>>()));
        services.AddSingleton(sp => new UpdateService(Version, sp.GetRequiredService<ILogger<UpdateService>>()));
        services.AddLogging(b => b.ClearProviders().AddProvider(log).SetMinimumLevel(LogLevel.Debug));

        // Windows integration
        services.AddSingleton<UiaDispatcher>();
        services.AddSingleton<FocusMonitor>();
        services.AddSingleton<ForegroundWindowMonitor>();
        services.AddSingleton<ITextSurfaceAdapterFactory, GenericUiaAdapterFactory>();
        services.AddSingleton<AdapterSelector>();
        services.AddSingleton(_ => new SecurityFilter());
        services.AddSingleton<DocumentState>();
        services.AddSingleton<SurfaceTracker>();

        // Analysis
        services.AddSingleton<IPersonalDictionary>(_ => new PersonalDictionary(PersonalDictionary.DefaultPath));
        services.AddSingleton(_ => new IgnoreList(IgnoreList.DefaultPath));
        services.AddSingleton(sp => new IssueCacheManager(sp.GetRequiredService<IPersonalDictionary>(), sp.GetRequiredService<IgnoreList>()));
        services.AddSingleton<ITextAnalyzer>(sp => new SpellAnalyzer(
            sp.GetRequiredService<IPersonalDictionary>(), sp.GetRequiredService<SettingsStore>().Current.General.Language, sp.GetRequiredService<ILogger<SpellAnalyzer>>()));
        services.AddSingleton<ITextAnalyzer>(sp => new HarperAnalyzer(sp.GetRequiredService<ILogger<HarperAnalyzer>>()));
        services.AddSingleton(sp => new GrmrModelStore(GrmrModelStore.DefaultDirectory, Version, sp.GetRequiredService<ILogger<GrmrModelStore>>()));
        services.AddSingleton(_ => new GpuGuard(GrmrModelStore.DefaultDirectory));
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<GrmrModelStore>();
            var settings = sp.GetRequiredService<SettingsStore>();
            var guard = sp.GetRequiredService<GpuGuard>();
            var llamaLogger = sp.GetRequiredService<ILogger<LlamaSentenceCorrector>>();
            return new GrmrAnalyzer(() => store.InstalledPath,
                path => LlamaSentenceCorrector.Create(path, settings.Current.Writing.AiGrammarUseGpu, guard, llamaLogger),
                sp.GetRequiredService<ILogger<GrmrAnalyzer>>());
        });
        services.AddSingleton<ITextAnalyzer>(sp => sp.GetRequiredService<GrmrAnalyzer>());
        services.AddSingleton(sp => new AnalysisPipelineOptions
        {
            Debounce = TimeSpan.FromMilliseconds(sp.GetRequiredService<SettingsStore>().Current.General.AnalysisDelayMs),
        });
        services.AddSingleton(sp => new AnalysisPipeline(
            sp.GetServices<ITextAnalyzer>(),
            sp.GetRequiredService<AnalysisPipelineOptions>(),
            sp.GetRequiredService<ILogger<AnalysisPipeline>>()));

        // Corrections
        services.AddSingleton(sp => new ReplacementEngine(
            sp.GetRequiredService<UiaDispatcher>(), sp.GetRequiredService<DocumentState>(),
            new ReplacementOptions(), sp.GetRequiredService<ILogger<ReplacementEngine>>(), sp.GetRequiredService<PerfCounters>(),
            sp.GetRequiredService<CompatibilityLog>()));
        services.AddSingleton<CorrectionController>();

        // Annotations
        services.AddSingleton<WindowEventMonitor>();
        services.AddSingleton(sp => new OverlayManager(
            Current.Dispatcher, sp.GetRequiredService<SurfaceTracker>(), sp.GetRequiredService<DocumentState>(),
            sp.GetRequiredService<IssueCacheManager>(), sp.GetRequiredService<WindowEventMonitor>(),
            sp.GetRequiredService<ILogger<OverlayManager>>(), sp.GetRequiredService<PerfCounters>(),
            sp.GetRequiredService<CompatibilityLog>()));

        // UI
        services.AddSingleton(sp => new DiagnosticsViewModel(
            Current.Dispatcher,
            sp.GetRequiredService<SurfaceTracker>(),
            sp.GetRequiredService<AnalysisPipeline>(),
            sp.GetRequiredService<IssueCacheManager>(),
            sp.GetServices<ITextAnalyzer>(),
            sp.GetRequiredService<DiagnosticsLog>(),
            sp.GetRequiredService<PerfCounters>()));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// <c>Redline.exe --exit</c>: asks the running instance (this session) to quit and waits for it,
    /// killing it after 10 s. Returns false if one had to be killed.
    /// </summary>
    private static bool ExitRunningInstance()
    {
        using var self = Process.GetCurrentProcess();
        var running = Process.GetProcessesByName("Redline").Where(p => p.Id != self.Id && p.SessionId == self.SessionId).ToList();
        if (running.Count == 0) return true;

        if (EventWaitHandle.TryOpenExisting(ExitEventName, out var signal))
        {
            using (signal) signal.Set();
        }

        bool clean = true;
        foreach (var process in running)
        {
            using (process)
            {
                if (process.WaitForExit(10_000)) continue;
                clean = false;
                try
                {
                    process.Kill();
                    process.WaitForExit(5_000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone, or not ours to kill.
                }
            }
        }
        return clean;
    }

    /// <summary>
    /// The Run key is the truth for start-with-Windows: the installer writes it and uninstall removes it,
    /// so the setting mirrors it. Never rewritten at startup — a build-folder copy must not take over the
    /// installed copy's entry.
    /// </summary>
    private void SyncStartWithWindows(SettingsStore settings)
    {
        bool registered;
        try
        {
            registered = _services!.GetRequiredService<StartupRegistration>().RegisteredCommand is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            _logger?.LogWarning("Couldn't read the Run key: {Reason}", ex.Message);
            return;
        }
        if (registered != settings.Current.General.StartWithWindows)
            settings.Update(st => st with { General = st.General with { StartWithWindows = registered } });
    }

    /// <summary>Reflects the updater in the tray: a menu item while an update waits, and one notification per version.</summary>
    /// <summary>
    /// The target app's product version (e.g. Teams 25.x), for retesting the same build. Cached per process;
    /// null when the process can't be opened (elevated, already gone).
    /// </summary>
    private string? AppVersionOf(int processId)
    {
        lock (_appVersions)
        {
            if (_appVersions.TryGetValue(processId, out var cached)) return cached;
            string? version = null;
            try
            {
                using var p = Process.GetProcessById(processId);
                version = p.MainModule?.FileVersionInfo.ProductVersion?.Trim();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // leave it unknown
            }
            if (_appVersions.Count > 256) _appVersions.Clear();
            return _appVersions[processId] = string.IsNullOrEmpty(version) ? null : version;
        }
    }

    /// <summary>The grammar model finished downloading (or failed). UI thread.</summary>
    private void OnModelChanged()
    {
        if (_services is null || _tray is null) return;
        var models = _services.GetRequiredService<GrmrModelStore>();
        var previous = _modelState;
        _modelState = models.State;
        if (previous == _modelState) return;

        if (_modelState == ModelState.Installed && previous == ModelState.Downloading
            && _services.GetRequiredService<SettingsStore>().Current.Writing.AiGrammar)
        {
            _tray.Notify("The AI grammar model is ready. Redline will now suggest grammar fixes it finds.", false);
            _services.GetRequiredService<AnalysisPipeline>().Refresh();
        }
        else if (_modelState == ModelState.Failed)
        {
            _tray.Notify("The AI grammar model couldn't be downloaded. Try again from Settings > Writing.", true);
        }
    }

    private void OnUpdateChanged()
    {
        if (_updates is null || _tray is null) return;
        var update = _updates.Update;
        switch (_updates.State)
        {
            case UpdateState.Ready when update is not null:
                _tray.SetUpdateItem($"Install Redline {update.Version}", InstallUpdate);
                NotifyUpdateOnce(update, $"Redline {update.Version} is ready. Click to install it.", InstallUpdate);
                break;
            case UpdateState.Available when update is not null:
                _tray.SetUpdateItem($"Get Redline {update.Version}…", () => OpenUrl(update.PageUrl));
                NotifyUpdateOnce(update, $"Redline {update.Version} is available. Click to open the download page.", () => OpenUrl(update.PageUrl));
                break;
            case UpdateState.UpToDate:
                _tray.SetUpdateItem(null, null);
                break;
        }
    }

    private void NotifyUpdateOnce(AvailableUpdate update, string message, Action onClick)
    {
        if (_notifiedUpdate == update.Version) return;
        _notifiedUpdate = update.Version;
        _tray?.Notify(message, false, onClick);
    }

    private void InstallUpdate()
    {
        if (_updates?.Install() != true)
            _tray?.Notify("That update isn't available any more. Check again from Settings › About.", true);
    }

    public static void OpenUrl(Uri url) => Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });

    /// <summary>Timing summary (counts and milliseconds only).</summary>
    private void LogPerfSummary()
    {
        var perf = _services?.GetService<PerfCounters>();
        if (perf is null) return;
        var summary = perf.Summary();
        if (!string.IsNullOrEmpty(summary))
            _logger?.LogInformation("Perf p50/p95/max: {Summary}", summary);

        var counts = perf.Counts();
        var now = DateTime.UtcNow;
        var rates = PerfCounters.Rates(_lastPerfCounts, counts, now - _lastPerfAt);
        _lastPerfCounts = counts;
        _lastPerfAt = now;
        _logger?.LogInformation("Rates: {Rates}; memory: {Usage}", rates.Length > 0 ? rates : "idle", ResourceUsage.Current());
    }

    /// <summary>Best effort: the process may be going down, so nothing here may throw.</summary>
    private void WriteCrashReport(Exception? exception, string context, bool terminating)
    {
        if (exception is null || _log?.LogDirectory is not { } dir) return;
        try
        {
            CrashReport.Write(dir, exception, context, terminating, Version, _uptime.Elapsed);
        }
        catch
        {
            // Nothing more we can do.
        }
    }

    private void ShowSettings(string? tab = null)
    {
        if (_services is null || _hotkeys is null) return;

        if (_settingsWindow is null)
        {
            var spelling = _services.GetServices<ITextAnalyzer>().OfType<SpellAnalyzer>().FirstOrDefault();
            _settingsWindow = new SettingsWindow(
                _services.GetRequiredService<SettingsStore>(), _hotkeys,
                _services.GetRequiredService<IPersonalDictionary>(), _services.GetRequiredService<IgnoreList>(),
                SpellAnalyzer.SupportedLanguages(), spelling?.LanguageTag,
                _services.GetRequiredService<SecurityFilter>().BuiltInExclusions,
                _log?.LogDirectory,
                _services.GetRequiredService<UpdateService>(),
                _services.GetRequiredService<GrmrModelStore>(),
                RemoveGrammarModelAsync,
                _services.GetRequiredService<CompatibilityLog>(),
                AiDeviceStatus);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Activate();
        }
        if (tab is not null) _settingsWindow.ShowTab(tab);
    }

    /// <summary>Settings > Writing: where the AI grammar model runs, or will run.</summary>
    private string AiDeviceStatus()
    {
        var services = _services!;
        var writing = services.GetRequiredService<SettingsStore>().Current.Writing;
        if (!writing.AiGrammarUseGpu)
            return "The model runs on the processor.";
        if (services.GetRequiredService<GpuGuard>().Blocked)
            return "The graphics card crashed while running the model, so it runs on the processor. Turn this off and on again to retry.";
        if (LlamaSentenceCorrector.GpuNeedsRestart)
            return "Restart Redline to move the model to the graphics card.";
        return services.GetRequiredService<GrmrAnalyzer>().Device switch
        {
            null => "Uses a graphics card with Vulkan support (NVIDIA, AMD, Intel) if there is one, otherwise the processor.",
            "CPU" => "Running on the processor: no usable graphics card was found.",
            var device => "Running on the graphics card (" + device["GPU: ".Length..] + ").",
        };
    }

    /// <summary>Turns AI grammar off, unloads the model and deletes it. False if the file couldn't be deleted.</summary>
    private async Task<bool> RemoveGrammarModelAsync()
    {
        var services = _services!;
        var settings = services.GetRequiredService<SettingsStore>();
        if (settings.Current.Writing.AiGrammar)
            settings.Update(st => st with { Writing = st.Writing with { AiGrammar = false } });
        var grmr = services.GetRequiredService<GrmrAnalyzer>();
        grmr.Enabled = false;
        await grmr.ReleaseModelAsync();
        return services.GetRequiredService<GrmrModelStore>().Remove();
    }

    private void ShowDiagnostics()
    {
        if (_services is null) return;

        if (_diagnosticsWindow is null)
        {
            var corrections = _services.GetRequiredService<CorrectionController>();
            _diagnosticsWindow = new DiagnosticsWindow(
                _services.GetRequiredService<DiagnosticsViewModel>(),
                issue => _ = corrections.ShowForIssueAsync(issue));
            _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
            _diagnosticsWindow.Show();
        }
        else
        {
            _diagnosticsWindow.Activate();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _updateTimer?.Stop();
        _exitWait?.Unregister(null);
        _exitSignal?.Dispose();
        _perfTimer?.Stop();
        _compatibilityTimer?.Stop();
        _services?.GetService<CompatibilityLog>()?.Save();
        LogPerfSummary();
        _logger?.LogInformation("Redline exiting");
        _hotkeys?.Dispose();
        _tray?.Dispose();

        // Order matters: stop event sources before the UIA thread they marshal onto.
        _services?.GetService<OverlayManager>()?.Dispose();
        _services?.GetService<SurfaceTracker>()?.Dispose();
        _services?.GetService<AnalysisPipeline>()?.Dispose();
        _services?.Dispose(); // disposes UiaDispatcher last-resolved-first

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
