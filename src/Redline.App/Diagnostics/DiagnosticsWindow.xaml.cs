using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using Redline.Core.Models;

namespace Redline.App.Diagnostics;

public partial class DiagnosticsWindow : Window
{
    private readonly Action<TextIssue> _fixIssue;

    public DiagnosticsWindow(DiagnosticsViewModel viewModel, Action<TextIssue> fixIssue)
    {
        _fixIssue = fixIssue;
#pragma warning disable WPF0001 // Fluent theme is experimental in .NET 9; System = follow Windows light/dark.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        InitializeComponent();
        DataContext = viewModel;

        // Keep the log scrolled to the newest entry.
        NotifyCollectionChangedEventHandler scroll = (_, _) =>
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                if (LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);
            });
        };
        viewModel.Log.CollectionChanged += scroll;

        viewModel.RefreshPerf();
        var perfTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        perfTimer.Tick += (_, _) => viewModel.RefreshPerf();
        perfTimer.Start();

        Closed += (_, _) =>
        {
            viewModel.Log.CollectionChanged -= scroll;
            perfTimer.Stop();
        };
    }

    private void IssueGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IssueGrid.SelectedItem is IssueRow row)
            _fixIssue(row.Issue);
    }
}
