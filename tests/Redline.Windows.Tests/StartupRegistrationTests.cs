using Microsoft.Win32;
using Xunit;

namespace Redline.Windows.Tests;

/// <summary>Runs against a throwaway HKCU key; the real Run key is never touched.</summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private readonly string _keyPath = @"Software\Redline.Tests\Run-" + Guid.NewGuid().ToString("N");
    private readonly StartupRegistration _startup;

    public StartupRegistrationTests() => _startup = new StartupRegistration(Registry.CurrentUser, _keyPath);

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\Redline.Tests", throwOnMissingSubKey: false);

    [Fact]
    public void DefaultsTarget_TheRealRunKey()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistration.RunKeyPath);
        Assert.Equal("Redline", StartupRegistration.DefaultValueName);
    }

    [Fact]
    public void Enable_WritesQuotedPath()
    {
        _startup.Apply(true, @"C:\Program Files\Redline\Redline.exe");

        Assert.Equal("\"C:\\Program Files\\Redline\\Redline.exe\"", _startup.RegisteredCommand);
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        Assert.Equal(RegistryValueKind.String, key!.GetValueKind("Redline"));
    }

    [Fact]
    public void Enable_UpdatesAMovedExe()
    {
        _startup.Apply(true, @"C:\Old\Redline.exe");
        _startup.Apply(true, @"C:\New\Redline.exe");

        Assert.Equal("\"C:\\New\\Redline.exe\"", _startup.RegisteredCommand);
    }

    [Fact]
    public void Disable_RemovesOnlyRedlinesValue()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_keyPath))
            key.SetValue("OtherApp", "other.exe");
        _startup.Apply(true, @"C:\Redline\Redline.exe");

        _startup.Apply(false, @"C:\Redline\Redline.exe");

        Assert.Null(_startup.RegisteredCommand);
        using var after = Registry.CurrentUser.OpenSubKey(_keyPath);
        Assert.Equal("other.exe", after!.GetValue("OtherApp"));
    }

    [Fact]
    public void Disable_WhenKeyOrValueMissing_IsANoOp()
    {
        _startup.Apply(false, @"C:\Redline\Redline.exe"); // key doesn't exist yet
        Assert.Null(_startup.RegisteredCommand);
        Assert.Null(Registry.CurrentUser.OpenSubKey(_keyPath)); // and wasn't created
    }
}
