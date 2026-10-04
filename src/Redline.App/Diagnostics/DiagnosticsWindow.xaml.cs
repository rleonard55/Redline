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
        Closed += (_, _) => viewModel.Log.CollectionChanged -= scroll;
    }

    private void IssueGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IssueGrid.SelectedItem is IssueRow row)
            _fixIssue(row.Issue);
    }
}
