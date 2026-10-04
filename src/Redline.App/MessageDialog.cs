using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Redline.App;

/// <summary>
/// An OK-only message box that follows Windows light/dark (the Win32 MessageBox is always light).
/// Same per-window Fluent theme as the Settings window.
/// </summary>
internal sealed class MessageDialog : Window
{
    private const string InfoGlyph = "";
    private const string ErrorGlyph = "";

    private MessageDialog(string message, bool error, Window? owner)
    {
#pragma warning disable WPF0001 // Fluent theme is experimental in .NET 9; System = follow Windows light/dark.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        Title = "Redline";
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 320;
        MaxWidth = 480;
        Owner = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        // Without an owner (startup), the taskbar button is the only way back to it if it gets covered.
        ShowInTaskbar = owner is null;

        var icon = new TextBlock
        {
            Text = error ? ErrorGlyph : InfoGlyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 28,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty,
            error ? "SystemFillColorCriticalBrush" : "AccentTextFillColorPrimaryBrush");

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => Close();

        var body = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(icon, Dock.Left);
        body.Children.Add(icon);
        body.Children.Add(text);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(body);
        root.Children.Add(ok);
        Content = root;

        Loaded += (_, _) =>
        {
            ok.Focus();
            (error ? SystemSounds.Hand : SystemSounds.Asterisk).Play();
        };
    }

    public static void ShowInfo(string message, Window? owner = null) => new MessageDialog(message, false, owner).ShowDialog();

    public static void ShowError(string message, Window? owner = null) => new MessageDialog(message, true, owner).ShowDialog();
}
