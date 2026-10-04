using System.Text.RegularExpressions;
using Redline.Windows.Automation;

namespace Redline.Windows;

/// <param name="Sensitive">
/// True when reading must stop immediately (credentials, excluded apps). False for blocks that only
/// mean "nothing to check here" (read-only, disabled), where a brief grace period is harmless.
/// </param>
public readonly record struct SecurityDecision(bool Allowed, string Reason, bool Sensitive = false)
{
    public static SecurityDecision Allow { get; } = new(true, "Allowed");
    public static SecurityDecision Block(string reason, bool sensitive = true) => new(false, reason, sensitive);
}

/// <summary>
/// Decides whether a surface may be read at all. Runs before any text is read, so a blocked
/// control's content never enters the process. Errs on the side of blocking (risk R6).
/// </summary>
public sealed partial class SecurityFilter
{
    /// <summary>Password managers and OS credential surfaces. Compared case-insensitively against "name.exe".</summary>
    public static readonly IReadOnlyList<string> DefaultExcludedProcesses =
    [
        "KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe", "LastPass.exe",
        "Dashlane.exe", "Enpass.exe", "RoboForm.exe", "NordPass.exe",
        "CredentialUIBroker.exe", "consent.exe", "LogonUI.exe", "LockApp.exe",
    ];

    /// <summary>Class names of terminal input surfaces: xterm.js (VS Code and other Electron IDEs), Windows Terminal, conhost.</summary>
    private static readonly string[] TerminalClasses = ["xterm-helper-textarea", "TermControl", "ConsoleWindowClass"];

    private readonly HashSet<string> _excludedProcesses;
    private readonly int _ownProcessId = Environment.ProcessId;

    public SecurityFilter(IEnumerable<string>? excludedProcesses = null)
    {
        _excludedProcesses = new HashSet<string>(excludedProcesses ?? DefaultExcludedProcesses, StringComparer.OrdinalIgnoreCase);
    }

    public SecurityDecision Evaluate(ElementInfo info)
    {
        if (info.IsPassword)
            return SecurityDecision.Block("Password field");

        if (info.ProcessId == _ownProcessId)
            return SecurityDecision.Block("Redline's own window");

        if (_excludedProcesses.Contains(info.ProcessName))
            return SecurityDecision.Block($"Excluded process ({info.ProcessName})");

        if (SensitiveId().IsMatch(info.AutomationId) || SensitiveName().IsMatch(info.Name))
            return SecurityDecision.Block("Looks like a credential field");

        // Terminals receive whatever is typed at sudo/ssh/password prompts, and are never prose.
        if (TerminalClasses.Any(c => info.ClassName.Contains(c, StringComparison.Ordinal)))
            return SecurityDecision.Block("Terminal input");

        if (!info.IsEnabled)
            return SecurityDecision.Block("Control is disabled", sensitive: false);

        if (info.SupportsValuePattern && info.ValueIsReadOnly)
            return SecurityDecision.Block("Control is read-only", sensitive: false);

        return SecurityDecision.Allow;
    }

    // AutomationId is developer-chosen, so substring matching is fine ("txtPassword", "otp_input").
    [GeneratedRegex(@"pass(word|code|phrase)|pwd|\bpin\b|otp|cvv|cvc|security.?code|secret", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveId();

    // Name is often the visible label; require whole words to avoid false positives on prose.
    [GeneratedRegex(@"^\s*(password|passcode|passphrase|pin|one[- ]time code|verification code|cvv|cvc|security code)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();
}
