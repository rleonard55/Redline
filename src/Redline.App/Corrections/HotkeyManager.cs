using Microsoft.Extensions.Logging;
using Redline.Core.Settings;

namespace Redline.App.Corrections;

/// <summary>
/// Owns the suggestion hotkey registration. Global hotkeys are first-come, so if the configured
/// one belongs to another app, a fallback is used and the caller is told. UI thread only.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    /// <summary>Tried in order when the configured hotkey is taken.</summary>
    public static readonly Hotkey[] Fallbacks =
    [
        new(HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x20),       // Win+Alt+Space
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0xBA),   // Ctrl+Alt+;
    ];

    private readonly Action _pressed;
    private readonly ILogger _logger;
    private GlobalHotkey? _registration;

    public HotkeyManager(Action pressed, ILogger logger)
    {
        _pressed = pressed;
        _logger = logger;
    }

    /// <summary>While true (the settings window is capturing a new hotkey), presses are ignored.</summary>
    public bool Suspended { get; set; }

    /// <summary>The hotkey actually registered, or null if none could be.</summary>
    public Hotkey? Active { get; private set; }

    /// <summary>Raised after <see cref="Active"/> changes.</summary>
    public event Action<Hotkey?>? ActiveChanged;

    /// <summary>
    /// Registers <paramref name="configured"/>, or the first free fallback if it's taken.
    /// Returns a message for the user when the configured hotkey couldn't be used, else null.
    /// </summary>
    public string? RegisterConfigured(Hotkey configured)
    {
        if (TryChangeHotkey(configured))
            return null;

        foreach (var fallback in Fallbacks.Where(f => f != configured))
        {
            if (TryChangeHotkey(fallback))
                return $"{configured} is used by another app; Redline's suggestion hotkey is {fallback}.";
        }

        return Active is { } kept
            ? $"{configured} is used by another app; keeping {kept}."
            : "No suggestion hotkey is available; double-click issues in the Diagnostics window instead.";
    }

    /// <summary>
    /// Switches to <paramref name="hotkey"/>. The current registration stays in force until the new
    /// one succeeds, so a failure leaves the old hotkey working. Call before saving the setting.
    /// </summary>
    public bool TryChangeHotkey(Hotkey hotkey)
    {
        if (Active == hotkey) return true;

        GlobalHotkey registration;
        try
        {
            registration = new GlobalHotkey((uint)hotkey.Modifiers, (uint)hotkey.VirtualKey, OnPressed);
        }
        catch (InvalidOperationException)
        {
            _logger.LogInformation("Hotkey {Hotkey} is owned by another application", hotkey.ToString());
            return false;
        }

        _registration?.Dispose();
        _registration = registration;
        Active = hotkey;
        _logger.LogInformation("Suggestion hotkey: {Hotkey}", hotkey.ToString());
        ActiveChanged?.Invoke(hotkey);
        return true;
    }

    private void OnPressed()
    {
        if (!Suspended) _pressed();
    }

    public void Dispose()
    {
        _registration?.Dispose();
        _registration = null;
    }
}
