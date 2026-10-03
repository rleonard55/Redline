using System.IO;
using System.Windows;

namespace Redline.CompatibilityHarness;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try
            {
                var errorMsg = $"CRITICAL UNHANDLED EXCEPTION:\n{args.ExceptionObject}";
                File.WriteAllText("harness_crash.log", errorMsg);
                System.Windows.MessageBox.Show(errorMsg, "Redline Harness Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        };

        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                var errorMsg = $"DISPATCHER EXCEPTION:\n{args.Exception}";
                File.WriteAllText("harness_dispatcher_crash.log", errorMsg);
                System.Windows.MessageBox.Show(errorMsg, "Redline Harness Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        };
    }
}
