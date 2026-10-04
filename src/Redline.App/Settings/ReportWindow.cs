using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Redline.App.Settings;

/// <summary>Shows a report exactly as stored, read-only, with a Copy button.</summary>
internal sealed class ReportWindow : Window
{
    public ReportWindow(string title, string intro, string content)
    {
#pragma warning disable WPF0001 // Fluent theme is experimental in .NET 9; System = follow Windows light/dark.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        Title = title;
        Width = 720;
        Height = 560;
        MinWidth = 420;
        MinHeight = 300;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var text = new TextBox
        {
            Text = content,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true,
        };
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var copy = new Button { Content = "Copy", Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(content);
                status.Text = "Copied.";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                status.Text = "The clipboard is busy; try again.";
            }
        };
        var close = new Button { Content = "Close", Padding = new Thickness(14, 2, 14, 2), IsCancel = true };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(status);
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(12) };
        var header = new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(buttons);
        root.Children.Add(text);
        Content = root;
    }
}
