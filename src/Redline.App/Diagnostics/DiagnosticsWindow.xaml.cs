using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
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

        // Fluent list items are ~40 px tall; the log wants dense rows. Built in code because a XAML
        // BasedOn resolves before the Fluent dictionary is merged (it would fall back to the Aero style).
        Loaded += (_, _) =>
        {
            var dense = new Style(typeof(ListBoxItem), (Style)LogList.FindResource(typeof(ListBoxItem)));
            dense.Setters.Add(new Setter(MinHeightProperty, 0.0));
            dense.Setters.Add(new Setter(PaddingProperty, new Thickness(6, 1, 6, 1)));
            dense.Setters.Add(new Setter(MarginProperty, new Thickness(0)));
            LogList.ItemContainerStyle = dense;
        };

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
