using Microsoft.Win32;

namespace Redline.Windows;

/// <summary>
/// "Start with Windows" via the per-user Run key. The key path is injectable so tests can use a
/// throwaway HKCU key instead of the real one.
/// </summary>
public sealed class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "Redline";

    private readonly RegistryKey _root;
    private readonly string _keyPath;
    private readonly string _valueName;

    public StartupRegistration(RegistryKey? root = null, string keyPath = RunKeyPath, string valueName = DefaultValueName)
    {
        _root = root ?? Registry.CurrentUser;
        _keyPath = keyPath;
        _valueName = valueName;
    }

    /// <summary>The registered command line, or null if Redline isn't registered.</summary>
    public string? RegisteredCommand
    {
        get
        {
            using var key = _root.OpenSubKey(_keyPath);
            return key?.GetValue(_valueName) as string;
        }
    }

    /// <summary>Quoted so paths with spaces ("C:\Program Files\...") launch correctly.</summary>
    public static string CommandFor(string exePath) => "\"" + exePath + "\"";

    /// <summary>
    /// Registers <paramref name="exePath"/> or removes the registration. Writes only when the value
    /// differs, so calling this at every start is cheap. Throws on registry access failures.
    /// </summary>
    public void Apply(bool enabled, string exePath)
    {
        if (enabled)
        {
            var command = CommandFor(exePath);
            if (RegisteredCommand == command) return;
            using var key = _root.CreateSubKey(_keyPath, writable: true);
            key.SetValue(_valueName, command, RegistryValueKind.String);
        }
        else
        {
            using var key = _root.OpenSubKey(_keyPath, writable: true);
            if (key?.GetValue(_valueName) is not null)
                key.DeleteValue(_valueName, throwOnMissingValue: false);
        }
    }
}
