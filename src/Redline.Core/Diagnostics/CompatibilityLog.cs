using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Redline.Core.Corrections;
using Redline.Core.Models;

namespace Redline.Core.Diagnostics;

/// <summary>
/// How well Redline works in one kind of text field: an app plus the control's UIA type, class and
/// framework. Counts only — never text, window titles or addresses — so it is safe to show, copy
/// or (some day, opt-in) send as-is.
/// </summary>
public sealed record AppCompatibilityEntry
{
    public required string Process { get; init; }
    public string ControlType { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string Framework { get; init; } = string.Empty;

    /// <summary>The target app's product version (from its exe), when readable.</summary>
    public string? AppVersion { get; set; }

    public string FirstSeen { get; set; } = string.Empty; // yyyy-MM-dd
    public string LastSeen { get; set; } = string.Empty;

    public int Attaches { get; set; }
    public List<string> Patterns { get; set; } = new();
    public int TextReads { get; set; }
    public int EmptyReads { get; set; }
    public int LayoutPasses { get; set; }
    public int IssuesPlaced { get; set; }
    public int IssuesNotPlaced { get; set; }
    public int FixesApplied { get; set; }
    public Dictionary<string, int> FixMethods { get; set; } = new();
    public Dictionary<string, int> FixProblems { get; set; } = new();
    public Dictionary<string, int> Blocked { get; set; } = new();

    [JsonIgnore]
    public string Key => CompatibilityLog.KeyOf(Process, ControlType, ClassName, Framework);

    public AppCompatibilityEntry Copy() => this with
    {
        Patterns = new(Patterns),
        FixMethods = new(FixMethods),
        FixProblems = new(FixProblems),
        Blocked = new(Blocked),
    };
}

/// <summary>The whole local record, as stored and as shown by "View report".</summary>
public sealed record CompatibilityReport
{
    public int Schema { get; init; } = 1;
    public string RedlineVersion { get; init; } = string.Empty;
    public string Windows { get; init; } = string.Empty;
    public string Generated { get; init; } = string.Empty;
    public List<AppCompatibilityEntry> Apps { get; init; } = new();
}

/// <summary>
/// The local, per-app compatibility record behind Settings > Compatibility. Recording is cheap
/// (counter bumps under a lock); <see cref="Save"/> writes %LOCALAPPDATA%\Redline\compatibility.json
/// when something changed. Entries not seen for <see cref="RetentionDays"/> days are dropped.
/// Thread-safe.
/// </summary>
public sealed class CompatibilityLog
{
    public const int RetentionDays = 30;
    public const int MaxEntries = 200;
    private const int MaxReasons = 20; // per dictionary, so an odd app can't grow the file without bound

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string? _path;
    private readonly string _redlineVersion;
    private readonly Func<DateTime> _now;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, AppCompatibilityEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public CompatibilityLog(string? path, string redlineVersion, ILogger<CompatibilityLog>? logger = null, Func<DateTime>? now = null)
    {
        _path = path;
        _redlineVersion = redlineVersion;
        _now = now ?? (() => DateTime.Now);
        _logger = logger ?? NullLogger<CompatibilityLog>.Instance;
        Load();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Redline", "compatibility.json");

    /// <summary>Raised (on the recording thread) when entries are added or removed — not on every counter bump.</summary>
    public event Action? Changed;

    public static string KeyOf(string process, string controlType, string className, string framework) =>
        string.Join("|", process, controlType, className, framework);

    public void Attached(TextSurfaceContext surface, TextSurfaceCapabilities capabilities, string? appVersion) =>
        Record(surface.ProcessName, surface.ControlType, surface.ClassName, surface.FrameworkId, e =>
        {
            e.Attaches++;
            if (appVersion is not null) e.AppVersion = appVersion;
            foreach (var p in capabilities.SupportedPatterns)
                if (!e.Patterns.Contains(p)) e.Patterns.Add(p);
            e.Patterns.Sort(StringComparer.Ordinal);
        });

    public void TextRead(TextSurfaceContext surface, bool empty) =>
        Record(surface.ProcessName, surface.ControlType, surface.ClassName, surface.FrameworkId, e =>
        {
            e.TextReads++;
            if (empty) e.EmptyReads++;
        });

    /// <summary>One overlay pass: <paramref name="placed"/> of <paramref name="total"/> issues got screen positions.</summary>
    public void Layout(TextSurfaceContext surface, int placed, int total) =>
        Record(surface.ProcessName, surface.ControlType, surface.ClassName, surface.FrameworkId, e =>
        {
            e.LayoutPasses++;
            e.IssuesPlaced += placed;
            e.IssuesNotPlaced += Math.Max(0, total - placed);
        });

    public void Correction(TextSurfaceContext surface, CorrectionResult result) =>
        Record(surface.ProcessName, surface.ControlType, surface.ClassName, surface.FrameworkId, e =>
        {
            if (result.Outcome == CorrectionOutcome.Applied)
            {
                e.FixesApplied++;
                Bump(e.FixMethods, result.Method);
            }
            else
            {
                // Messages are Redline's own fixed sentences ("The text changed after…"), never document text.
                Bump(e.FixProblems, $"{result.Outcome}: {result.Message}");
            }
        });

    /// <summary>A field Redline declined to read (password, terminal, read-only, excluded app…).</summary>
    public void Blocked(string process, string controlType, string className, string framework, string reason) =>
        Record(process, controlType, className, framework, e => Bump(e.Blocked, reason));

    public IReadOnlyList<AppCompatibilityEntry> Entries()
    {
        lock (_gate)
            return _entries.Values.OrderByDescending(e => e.LastSeen, StringComparer.Ordinal)
                .ThenBy(e => e.Process, StringComparer.OrdinalIgnoreCase)
                .Select(e => e.Copy()).ToList();
    }

    /// <summary>Exactly what is stored (and what an opt-in upload would send), as indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(Report(), Json);

    public CompatibilityReport Report() => new()
    {
        RedlineVersion = _redlineVersion,
        Windows = Environment.OSVersion.Version.ToString(),
        Generated = _now().ToString("yyyy-MM-dd"),
        Apps = Entries().ToList(),
    };

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _dirty = true;
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>Writes the file if anything changed since the last save.</summary>
    public void Save()
    {
        if (_path is null) return;
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            Prune();
            _dirty = false;
        }
        json = ToJson();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Couldn't save the compatibility record: {Reason}", ex.Message);
            lock (_gate) _dirty = true;
        }
    }

    /// <summary>A short verdict for the Settings list.</summary>
    public static string StatusOf(AppCompatibilityEntry e)
    {
        if (e.Attaches == 0)
            return e.Blocked.Count > 0 ? "Skipped: " + e.Blocked.MaxBy(kv => kv.Value).Key : "Not checked";
        if (e.TextReads > 0 && e.EmptyReads == e.TextReads)
            return "Reads as empty";
        int problems = e.FixProblems.Values.Sum();
        if (problems > 0 && e.FixesApplied == 0)
            return "Fixes failing";
        if (e.IssuesPlaced == 0 && e.IssuesNotPlaced > 0)
            return "No underlines";
        if (e.IssuesNotPlaced > e.IssuesPlaced)
            return "Some underlines missing";
        if (problems > e.FixesApplied)
            return "Fixes often fail";
        if (e.LayoutPasses == 0 && e.FixesApplied == 0)
            return "Not enough use yet";
        return "Working";
    }

    private void Record(string process, string controlType, string className, string framework, Action<AppCompatibilityEntry> update)
    {
        bool added = false;
        lock (_gate)
        {
            var key = KeyOf(process, controlType, className, framework);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new AppCompatibilityEntry { Process = process, ControlType = controlType, ClassName = className, Framework = framework };
                entry.FirstSeen = Today();
                _entries[key] = entry;
                added = true;
            }
            entry.LastSeen = Today();
            update(entry);
            _dirty = true;
            if (added && _entries.Count > MaxEntries) Prune();
        }
        if (added) Changed?.Invoke();
    }

    private string Today() => _now().ToString("yyyy-MM-dd");

    private static void Bump(Dictionary<string, int> counts, string key)
    {
        if (counts.TryGetValue(key, out int n)) counts[key] = n + 1;
        else if (counts.Count < MaxReasons) counts[key] = 1;
    }

    /// <summary>Caller holds _gate.</summary>
    private void Prune()
    {
        var cutoff = _now().AddDays(-RetentionDays).ToString("yyyy-MM-dd");
        foreach (var stale in _entries.Values.Where(e => string.CompareOrdinal(e.LastSeen, cutoff) < 0).ToList())
            _entries.Remove(stale.Key);
        foreach (var extra in _entries.Values.OrderBy(e => e.LastSeen, StringComparer.Ordinal).Take(Math.Max(0, _entries.Count - MaxEntries)).ToList())
            _entries.Remove(extra.Key);
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var report = JsonSerializer.Deserialize<CompatibilityReport>(File.ReadAllText(_path), Json);
            foreach (var e in report?.Apps ?? [])
                if (!string.IsNullOrEmpty(e.Process)) _entries[e.Key] = e;
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("Couldn't read the compatibility record; starting a new one: {Reason}", ex.Message);
        }
    }
}
