using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Redline.Analysis;
using Redline.Analysis.Harper;
using Redline.App.Diagnostics;
using Redline.App.Logging;
using Redline.App.TrayIcon;
using Redline.Core.Interfaces;
using Redline.Core.Pipeline;
using Redline.Windows;
using Redline.Windows.Automation;

namespace Redline.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private ServiceProvider? _services;
    private TrayIconHost? _tray;
    private DiagnosticsWindow? _diagnosticsWindow;
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

        var log = new DiagnosticsLog(DiagnosticsLog.DefaultDirectory);
        _services = ConfigureServices(log);
        _logger = _services.GetRequiredService<ILogger<App>>();

        DispatcherUnhandledException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unhandled UI exception");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger.LogCritical(args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogWarning(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        var tracker = _services.GetRequiredService<SurfaceTracker>();
        var pipeline = _services.GetRequiredService<AnalysisPipeline>();

        tracker.SnapshotChanged += (_, s) => pipeline.Submit(s.Surface, s.Snapshot);
        tracker.SurfaceChanged += (_, s) =>
        {
            if (s.Surface is null) pipeline.Clear();
        };

        // Constructed now so it captures events from the start, even before the window opens.
        _services.GetRequiredService<DiagnosticsViewModel>();

        foreach (var analyzer in pipeline.Analyzers)
            _logger.LogInformation("Analyzer {Name}: {State}", analyzer.Name, analyzer.IsAvailable ? "available" : "unavailable");

        _tray = new TrayIconHost(ShowDiagnostics, tracker.SetPaused, Shutdown);

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

    private static ServiceProvider ConfigureServices(DiagnosticsLog log)
    {
        var services = new ServiceCollection();

        services.AddSingleton(log);
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
        services.AddSingleton<ITextAnalyzer>(sp => new SpellAnalyzer(
            sp.GetRequiredService<IPersonalDictionary>(), "en-US", sp.GetRequiredService<ILogger<SpellAnalyzer>>()));
        services.AddSingleton<ITextAnalyzer>(sp => new HarperAnalyzer(sp.GetRequiredService<ILogger<HarperAnalyzer>>()));
        services.AddSingleton(_ => new AnalysisPipelineOptions());
        services.AddSingleton(sp => new AnalysisPipeline(
            sp.GetServices<ITextAnalyzer>(),
            sp.GetRequiredService<AnalysisPipelineOptions>(),
            sp.GetRequiredService<ILogger<AnalysisPipeline>>()));

        // UI
        services.AddSingleton(sp => new DiagnosticsViewModel(
            Current.Dispatcher,
            sp.GetRequiredService<SurfaceTracker>(),
            sp.GetRequiredService<AnalysisPipeline>(),
            sp.GetServices<ITextAnalyzer>(),
            sp.GetRequiredService<DiagnosticsLog>()));

        return services.BuildServiceProvider();
    }

    private void ShowDiagnostics()
    {
        if (_services is null) return;

        if (_diagnosticsWindow is null)
        {
            _diagnosticsWindow = new DiagnosticsWindow(_services.GetRequiredService<DiagnosticsViewModel>());
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
        _logger?.LogInformation("Redline exiting");
        _tray?.Dispose();

        // Order matters: stop event sources before the UIA thread they marshal onto.
        _services?.GetService<SurfaceTracker>()?.Dispose();
        _services?.GetService<AnalysisPipeline>()?.Dispose();
        _services?.Dispose(); // disposes UiaDispatcher last-resolved-first

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
