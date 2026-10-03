using Redline.Core.Models;

namespace Redline.Core.Corrections;

/// <summary>
/// User decisions to stop showing issues. "Ignore" is session-scoped and applies to one rule on one
/// word (every occurrence, in every app, until Redline restarts). "Ignore rule" persists one rule id
/// across sessions, one id per line in a UTF-8 file. Thread-safe.
/// </summary>
public sealed class IgnoreList
{
    private readonly object _gate = new();
    private readonly HashSet<(string RuleId, string Text)> _session = new();
    private readonly HashSet<string> _rules = new(StringComparer.Ordinal);
    private readonly string? _path;

    /// <param name="path">File for persisted rule ids; null keeps them in memory only.</param>
    public IgnoreList(string? path)
    {
        _path = path;
        if (path is not null && File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length > 0)
                    _rules.Add(line);
            }
        }
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Redline", "ignored_rules.txt");

    /// <summary>Raised after any ignore is added or removed.</summary>
    public event Action? Changed;

    public IReadOnlyCollection<string> IgnoredRules
    {
        get { lock (_gate) return _rules.ToArray(); }
    }

    public bool IsIgnored(TextIssue issue)
    {
        lock (_gate)
            return _rules.Contains(issue.RuleId) || _session.Contains((issue.RuleId, issue.OriginalText));
    }

    /// <summary>Hide this rule's finding on this exact text for the rest of the session.</summary>
    public void IgnoreInSession(TextIssue issue)
    {
        lock (_gate)
        {
            if (!_session.Add((issue.RuleId, issue.OriginalText)))
                return;
        }
        Changed?.Invoke();
    }

    /// <summary>Hide every finding from this rule, persistently.</summary>
    public void IgnoreRule(string ruleId)
    {
        lock (_gate)
        {
            if (!_rules.Add(ruleId))
                return;
            Save();
        }
        Changed?.Invoke();
    }

    public bool RestoreRule(string ruleId)
    {
        lock (_gate)
        {
            if (!_rules.Remove(ruleId))
                return false;
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Caller holds _gate.</summary>
    private void Save()
    {
        if (_path is null) return;

        // Rule ids embed lint messages; strip line breaks so one id stays one line.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllLines(temp, _rules.Select(r => r.ReplaceLineEndings(" ")).Order(StringComparer.Ordinal));
        File.Move(temp, _path, overwrite: true);
    }
}
