namespace Redline.Core.Settings;

/// <summary>User settings, persisted as %LOCALAPPDATA%\Redline\settings.json.</summary>
public sealed record RedlineSettings
{
    public GeneralSettings General { get; init; } = new();
    public WritingSettings Writing { get; init; } = new();
    public ApplicationSettings Applications { get; init; } = new();
    public AiSettings Ai { get; init; } = new();

    /// <summary>
    /// Which one-time migrations a file has had (<see cref="CurrentRevision"/>). Files from before it existed read
    /// as 0, which is why the default here is 0 and new files are created with <see cref="CurrentRevision"/>.
    /// </summary>
    public int Revision { get; init; }

    /// <summary>1: the analysis delay default went from 300 to 150 ms. 2: the GPU switch became <see cref="WritingSettings.AiGrammarDevice"/>.</summary>
    public const int CurrentRevision = 2;

    /// <summary>Defaults for a new settings file.</summary>
    public static RedlineSettings CreateDefault() => new() { Revision = CurrentRevision };

    /// <summary>
    /// One-time changes for files written by older versions: values still at an old default move to the new one
    /// (a value the user chose is kept).
    /// </summary>
    public RedlineSettings Migrated()
    {
        var s = this;
        if (s.Revision < 1 && s.General?.AnalysisDelayMs == 300)
            s = s with { General = s.General with { AnalysisDelayMs = GeneralSettings.DefaultDelayMs } };
        if (s.Writing?.AiGrammarUseGpu is { } useGpu) // the old switch: off = processor only, on = the new default
            s = s with { Writing = s.Writing with { AiGrammarDevice = useGpu ? s.Writing.AiGrammarDevice : AiDevice.Cpu, AiGrammarUseGpu = null } };
        return s.Revision >= CurrentRevision ? s : s with { Revision = CurrentRevision };
    }

    /// <summary>Clamps out-of-range values and fills gaps so a hand-edited file can't break Redline.</summary>
    public RedlineSettings Validated() => this with
    {
        General = (General ?? new()) with
        {
            Language = string.IsNullOrWhiteSpace(General?.Language) ? "en-US" : General.Language.Trim(),
            AnalysisDelayMs = Math.Clamp(General?.AnalysisDelayMs ?? GeneralSettings.DefaultDelayMs, GeneralSettings.MinDelayMs, GeneralSettings.MaxDelayMs),
            Hotkey = Hotkey.TryParse(General?.Hotkey, out var hk) ? hk.ToString() : GeneralSettings.DefaultHotkey,
        },
        Writing = (Writing ?? new()) with
        {
            AiGrammarDevice = Enum.IsDefined(Writing?.AiGrammarDevice ?? AiDevice.Auto) ? Writing!.AiGrammarDevice : AiDevice.Auto,
        },
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
    public const int DefaultDelayMs = 150;
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
    public int AnalysisDelayMs { get; init; } = DefaultDelayMs;

    public string Hotkey { get; init; } = DefaultHotkey;

    /// <summary>Show a small quick-fix pill when the mouse pointer rests on an underline.</summary>
    public bool HoverSuggestions { get; init; } = true;

    /// <summary>Show a thin bar beside paragraphs with several fixes; clicking it opens the paragraph fix.</summary>
    public bool ParagraphGutter { get; init; } = true;

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
    /// default: it needs a ~800 MB download, and memory while in use (~150 MB on the CPU, ~1.2 GB on integrated graphics).
    /// </summary>
    public bool AiGrammar { get; init; } = false;

    /// <summary>Where the AI grammar model runs. Auto: a dedicated GPU if there is one, otherwise the processor.</summary>
    public AiDevice AiGrammarDevice { get; init; } = AiDevice.Auto;

    /// <summary>Before revision 2: the GPU on/off switch. Read once by <see cref="RedlineSettings.Migrated"/>, then dropped.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? AiGrammarUseGpu { get; init; }
}

/// <summary>Where the AI grammar model runs (stored as a number: 0, 1, 2).</summary>
public enum AiDevice
{
    /// <summary>A dedicated (discrete) GPU if there is one, otherwise the processor. On integrated graphics the model
    /// is no faster than on the processor and takes ~1 GB more RAM.</summary>
    Auto = 0,
    /// <summary>Any GPU with Vulkan, integrated graphics included: ~10x less processor time, ~1 GB more RAM there.</summary>
    AnyGpu = 1,
    /// <summary>Never a GPU.</summary>
    Cpu = 2,
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
