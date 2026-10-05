namespace Redline.Core.Settings;

/// <summary>User settings, persisted as %LOCALAPPDATA%\Redline\settings.json.</summary>
public sealed record RedlineSettings
{
    public GeneralSettings General { get; init; } = new();
    public WritingSettings Writing { get; init; } = new();
    public ApplicationSettings Applications { get; init; } = new();
    public AiSettings Ai { get; init; } = new();

    /// <summary>Clamps out-of-range values and fills gaps so a hand-edited file can't break Redline.</summary>
    public RedlineSettings Validated() => this with
    {
        General = (General ?? new()) with
        {
            Language = string.IsNullOrWhiteSpace(General?.Language) ? "en-US" : General.Language.Trim(),
            AnalysisDelayMs = Math.Clamp(General?.AnalysisDelayMs ?? 300, GeneralSettings.MinDelayMs, GeneralSettings.MaxDelayMs),
            Hotkey = Hotkey.TryParse(General?.Hotkey, out var hk) ? hk.ToString() : GeneralSettings.DefaultHotkey,
        },
        Writing = Writing ?? new(),
        Applications = (Applications ?? new()) with
        {
            Excluded = (Applications?.Excluded ?? [])
                .Select(NormalizeProcessName)
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        },
        Ai = Ai ?? new(),
    };

    /// <summary>"KeePass", "keepass.exe ", "C:\...\KeePass.exe" → "KeePass.exe".</summary>
    public static string NormalizeProcessName(string? name)
    {
        var n = Path.GetFileName((name ?? string.Empty).Trim());
        if (n.Length == 0) return string.Empty;
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe";
    }
}

public sealed record GeneralSettings
{
    public const int MinDelayMs = 100;
    public const int MaxDelayMs = 2000;
    public const string DefaultHotkey = "Ctrl+Alt+.";

    /// <summary>
    /// Mirrors the HKCU Run key (synced from it at startup; the installer writes it). Toggling it
    /// registers or removes the running exe.
    /// </summary>
    public bool StartWithWindows { get; init; } = false;

    /// <summary>False = paused (also toggled from the tray).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>BCP-47 tag for spelling. Applied at startup.</summary>
    public string Language { get; init; } = "en-US";

    /// <summary>Quiet period after typing before analysis runs.</summary>
    public int AnalysisDelayMs { get; init; } = 300;

    public string Hotkey { get; init; } = DefaultHotkey;

    /// <summary>Show a small quick-fix pill when the mouse pointer rests on an underline.</summary>
    public bool HoverSuggestions { get; init; } = true;

    /// <summary>Daily check of GitHub Releases for a newer version (installing always needs a click).</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>The first-run welcome was shown (or skipped because this install predates it).</summary>
    public bool WelcomeShown { get; init; } = false;

    /// <summary>Detailed (debug-level) log files and periodic timing summaries. Still never document text.</summary>
    public bool DiagnosticsMode { get; init; } = false;
}

public sealed record WritingSettings
{
    public bool Spelling { get; init; } = true;
    public bool Grammar { get; init; } = true;

    /// <summary>Style suggestions (wordiness, readability). Off by default: they're opinions, not errors.</summary>
    public bool StyleSuggestions { get; init; } = false;

    /// <summary>
    /// Extra grammar suggestions from the on-device GRMR-V3 model (downloaded on request). Off by
    /// default: it needs a ~800 MB download and ~1 GB of memory while in use.
    /// </summary>
    public bool AiGrammar { get; init; } = false;

    /// <summary>
    /// Run the AI grammar model on the GPU (Vulkan: NVIDIA, AMD, Intel) when one is available. Same speed
    /// or faster than the CPU, and it keeps the processor free (about a tenth of the CPU time).
    /// </summary>
    public bool AiGrammarUseGpu { get; init; } = true;
}

public sealed record ApplicationSettings
{
    /// <summary>
    /// Extra processes never to read (e.g. "MyVault.exe"). Password managers and credential prompts
    /// are always excluded regardless of this list.
    /// </summary>
    public IReadOnlyList<string> Excluded { get; init; } = [];
}

/// <summary>Phase 6 (optional AI rewriting). Present so the file shape is stable.</summary>
public sealed record AiSettings
{
    public bool Enabled { get; init; }
    public string? Provider { get; init; }
    public string? Endpoint { get; init; }
}
