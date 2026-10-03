using System.Collections.Specialized;
using System.Windows;

namespace Redline.App.Diagnostics;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(DiagnosticsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Keep the log scrolled to the newest entry.
        NotifyCollectionChangedEventHandler scroll = (_, _) =>
        {
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
        viewModel.Log.CollectionChanged += scroll;
        Closed += (_, _) => viewModel.Log.CollectionChanged -= scroll;
    }
}
