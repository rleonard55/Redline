using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Redline.Analysis;
using Redline.Analysis.Harper;
using Redline.Annotations;
using Redline.App.Corrections;
using Redline.App.Diagnostics;
using Redline.App.Logging;
using Redline.App.Settings;
using Redline.App.TrayIcon;
using Redline.Core.Corrections;
using Redline.Core.Diagnostics;
using Redline.Core.Interfaces;
using Redline.Core.Pipeline;
using Redline.Core.Settings;
using Redline.Windows;
using Redline.Windows.Automation;
using Redline.Windows.Corrections;

namespace Redline.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private ServiceProvider? _services;
    private TrayIconHost? _tray;
    private DiagnosticsWindow? _diagnosticsWindow;
    private SettingsWindow? _settingsWindow;
    private HotkeyManager? _hotkeys;
    private DiagnosticsLog? _log;
    private DispatcherTimer? _perfTimer;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    private static readonly TimeSpan PerfSummaryInterval = TimeSpan.FromMinutes(5);

    public static string Version { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    /// <summary>The suggestion hotkey actually registered, for UI hints.</summary>
    public static string? HotkeyName { get; private set; }
    private ILogger<App>? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Redline.App.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Redline is already running (see the system tray).", "Redline", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

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
        };

        // Constructed now so it captures events from the start, even before the window opens.
        _services.GetRequiredService<DiagnosticsViewModel>();

        foreach (var analyzer in pipeline.Analyzers)
            _logger.LogInformation("Analyzer {Name}: {State}", analyzer.Name, analyzer.IsAvailable ? "available" : "unavailable");

        var settings = _services.GetRequiredService<SettingsStore>();
        _tray = new TrayIconHost(
            ShowSettings, ShowDiagnostics,
            () => settings.Update(st => st with { General = st.General with { Enabled = !st.General.Enabled } }),
            Shutdown);

        var corrections = _services.GetRequiredService<CorrectionController>();
        corrections.Notify += (message, isError) => _tray.Notify(message, isError);

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

        ApplySettings(null, settings.Current);
        settings.Changed += (old, updated) => Dispatcher.BeginInvoke(() => ApplySettings(old, updated));

        var overlay = _services.GetRequiredService<OverlayManager>();
        overlay.ApplyRequested += issue => _ = corrections.ApplyFirstSuggestionAsync(issue);
        overlay.MoreRequested += issue => _ = corrections.ShowForIssueAsync(issue);
        overlay.Start();

        try
        {
            await tracker.StartAsync();
            _logger.LogInformation("Redline started");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to start focus tracking");
            MessageBox.Show($"Redline could not start focus tracking:\n{ex.Message}", "Redline", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (e.Args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase))
            ShowDiagnostics();
    }

    /// <summary>Applies settings at startup (<paramref name="old"/> null) and after each change. UI thread.</summary>
    private void ApplySettings(RedlineSettings? old, RedlineSettings s)
    {
        if (_services is null || _tray is null || _hotkeys is null) return;

        // Resuming schedules a focus evaluation, which must not happen before the tracker has started.
        if (old is null ? !s.General.Enabled : old.General.Enabled != s.General.Enabled)
            _services.GetRequiredService<SurfaceTracker>().SetPaused(!s.General.Enabled);
        _tray.SetPaused(!s.General.Enabled);
        _services.GetRequiredService<IssueCacheManager>().SetWriting(s.Writing);
        _services.GetRequiredService<AnalysisPipeline>().Debounce = TimeSpan.FromMilliseconds(s.General.AnalysisDelayMs);
        _services.GetRequiredService<SecurityFilter>().SetUserExclusions(s.Applications.Excluded);
        _services.GetRequiredService<OverlayManager>().HoverEnabled = s.General.HoverSuggestions;

        if (_log is not null) _log.FileLevel = s.General.DiagnosticsMode ? LogLevel.Debug : LogLevel.Information;
        if (s.General.DiagnosticsMode) _perfTimer?.Start(); else _perfTimer?.Stop();
        if (old is null ? s.General.DiagnosticsMode : old.General.DiagnosticsMode != s.General.DiagnosticsMode)
            _logger?.LogInformation("Diagnostics mode: {Enabled} (Redline {Version})", s.General.DiagnosticsMode, Version);

        // The settings window registers a new hotkey before saving it; this covers hand edits of settings.json.
        if (old is not null && old.General.Hotkey != s.General.Hotkey
            && Hotkey.TryParse(s.General.Hotkey, out var hotkey) && !_hotkeys.TryChangeHotkey(hotkey))
        {
            _tray.Notify($"{hotkey} is used by another app; keeping {_hotkeys.Active?.ToString() ?? "no hotkey"}.", true);
        }

        // At startup only refresh an existing opt-in (the exe may have moved); never remove one unasked.
        if (old is null ? s.General.StartWithWindows : old.General.StartWithWindows != s.General.StartWithWindows)
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

    private static ServiceProvider ConfigureServices(DiagnosticsLog log)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sp => new SettingsStore(SettingsStore.DefaultPath, sp.GetRequiredService<ILogger<SettingsStore>>()));
        services.AddSingleton(_ => new StartupRegistration());

        services.AddSingleton(log);
        services.AddSingleton<PerfCounters>();
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
            new ReplacementOptions(), sp.GetRequiredService<ILogger<ReplacementEngine>>(), sp.GetRequiredService<PerfCounters>()));
        services.AddSingleton<CorrectionController>();

        // Annotations
        services.AddSingleton<WindowEventMonitor>();
        services.AddSingleton(sp => new OverlayManager(
            Current.Dispatcher, sp.GetRequiredService<SurfaceTracker>(), sp.GetRequiredService<DocumentState>(),
            sp.GetRequiredService<IssueCacheManager>(), sp.GetRequiredService<WindowEventMonitor>(),
            sp.GetRequiredService<ILogger<OverlayManager>>(), sp.GetRequiredService<PerfCounters>()));

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

    /// <summary>Timing summary (counts and milliseconds only).</summary>
    private void LogPerfSummary()
    {
        var summary = _services?.GetService<PerfCounters>()?.Summary();
        if (!string.IsNullOrEmpty(summary))
            _logger?.LogInformation("Perf p50/p95/max: {Summary}", summary);
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

    private void ShowSettings()
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
                _log?.LogDirectory);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Activate();
        }
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
        _perfTimer?.Stop();
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
