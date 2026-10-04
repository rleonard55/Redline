using System.Windows.Media;
using Microsoft.Win32;

namespace Redline.Annotations;

/// <summary>
/// The Windows app light/dark preference (Settings > Personalization > Colors > "Choose your app mode").
/// Redline's own flyouts (suggestion popup, hover pill) read <see cref="Current"/> each time they show;
/// the Settings and Diagnostics windows use WPF's Fluent theme, which follows the same preference.
/// </summary>
public static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True when Windows apps are set to dark mode. Missing key/value = light (the Windows default).</summary>
    public static bool IsDark
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static FlyoutPalette Current => IsDark ? FlyoutPalette.Dark : FlyoutPalette.Light;
}

/// <summary>Colors for Redline's small flyouts, close to the Windows 11 context-menu look.</summary>
public sealed record FlyoutPalette(
    Brush Background,
    Brush Border,
    Brush Divider,
    Brush Text,
    Brush SecondaryText,
    Brush MutedText,
    Brush Hover,
    Brush KeyboardFocus,
    bool IsDark)
{
    public static readonly FlyoutPalette Light = new(
        Background: Frozen(0xFF, 0xFF, 0xFF, 0xFF),
        Border: Frozen(0xFF, 0xC8, 0xC8, 0xC8),
        Divider: Frozen(0xFF, 0xE0, 0xE0, 0xE0),
        Text: Frozen(0xFF, 0x1A, 0x1A, 0x1A),
        SecondaryText: Frozen(0xC0, 0x00, 0x00, 0x00),
        MutedText: Frozen(0x80, 0x00, 0x00, 0x00),
        Hover: Frozen(0x14, 0x00, 0x00, 0x00),
        KeyboardFocus: Frozen(0x1F, 0x00, 0x67, 0xC0),
        IsDark: false);

    public static readonly FlyoutPalette Dark = new(
        Background: Frozen(0xFF, 0x2C, 0x2C, 0x2C),
        Border: Frozen(0xFF, 0x48, 0x48, 0x48),
        Divider: Frozen(0xFF, 0x40, 0x40, 0x40),
        Text: Frozen(0xFF, 0xF2, 0xF2, 0xF2),
        SecondaryText: Frozen(0xC8, 0xFF, 0xFF, 0xFF),
        MutedText: Frozen(0x8C, 0xFF, 0xFF, 0xFF),
        Hover: Frozen(0x1A, 0xFF, 0xFF, 0xFF),
        KeyboardFocus: Frozen(0x40, 0x60, 0xCD, 0xFF),
        IsDark: true);

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
