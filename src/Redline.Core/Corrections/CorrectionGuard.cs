namespace Redline.Core.Corrections;

/// <summary>Where the previous session died while applying a correction.</summary>
/// <param name="Strategy">The editing method in use ("none" before one was chosen).</param>
/// <param name="Step">focus, select, input, verify or undo.</param>
/// <param name="Process">The target's process name (no text, no titles).</param>
public sealed record CorrectionCrash(string Strategy, string Step, string Process)
{
    /// <summary>The method itself was changing the text when the process ended (not focus or selection).</summary>
    public bool DuringEdit => Strategy != CorrectionGuard.NoStrategy && Step is "input" or "verify" or "undo";
}

/// <summary>
/// Crash breadcrumbs for corrections, like the GPU guard: a marker file names the strategy and step in progress and
/// is deleted when the correction finishes. If the next start finds it, the process ended mid-correction (a native
/// crash, or security software killing a process that injects keystrokes - neither leaves a managed exception).
/// A strategy that died while editing becomes a <em>suspect</em> and is tried last until it works again.
/// </summary>
public sealed class CorrectionGuard
{
    public const string PendingFile = "correction-pending";
    public const string SuspectsFile = "correction-suspects";
    public const string NoStrategy = "none";

    private readonly string? _directory;
    private readonly object _gate = new();
    private readonly HashSet<string> _suspects = new(StringComparer.Ordinal);
    private bool _marked;

    /// <param name="directory">Where the markers live (Redline's data folder); null keeps state in memory only.</param>
    public CorrectionGuard(string? directory)
    {
        _directory = directory;
        if (directory is null) return;

        foreach (var line in TryReadLines(SuspectsFile))
            if (line.Length > 0) _suspects.Add(line);

        var pending = TryReadLines(PendingFile);
        if (pending.Length >= 3)
        {
            LastCrash = new CorrectionCrash(pending[0], pending[1], pending[2]);
            if (LastCrash.DuringEdit && _suspects.Add(LastCrash.Strategy))
                SaveSuspects();
        }
        TryDelete(PendingFile);
    }

    /// <summary>The previous session ended while a correction was in progress, or null.</summary>
    public CorrectionCrash? LastCrash { get; }

    public IReadOnlyCollection<string> Suspects
    {
        get { lock (_gate) return _suspects.ToArray(); }
    }

    /// <summary><paramref name="strategies"/> with the suspects moved to the end (otherwise in order).</summary>
    public IReadOnlyList<T> Order<T>(IReadOnlyList<T> strategies, Func<T, string> name)
    {
        lock (_gate)
            return strategies.Where(s => !_suspects.Contains(name(s)))
                .Concat(strategies.Where(s => _suspects.Contains(name(s))))
                .ToList();
    }

    /// <summary>Records the step about to run. Overwrites the previous step of the same correction.</summary>
    public void Mark(string strategy, string step, string process)
    {
        lock (_gate)
        {
            _marked = true;
            TryWrite(PendingFile, string.Join("\n", Clean(strategy), Clean(step), Clean(process)));
        }
    }

    /// <summary>The correction ended (either way). A suspect that just applied an edit is cleared.</summary>
    public void Finish(string? appliedWith)
    {
        lock (_gate)
        {
            if (_marked)
            {
                _marked = false;
                TryDelete(PendingFile);
            }
            if (appliedWith is not null && _suspects.Remove(appliedWith))
                SaveSuspects();
        }
    }

    private static string Clean(string value) => value.Replace('\n', ' ').Replace('\r', ' ');

    private void SaveSuspects()
    {
        if (_suspects.Count == 0) TryDelete(SuspectsFile);
        else TryWrite(SuspectsFile, string.Join("\n", _suspects));
    }

    private string[] TryReadLines(string name)
    {
        try
        {
            var path = Path.Combine(_directory!, name);
            return File.Exists(path) ? File.ReadAllText(path).Split('\n', StringSplitOptions.TrimEntries) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void TryWrite(string name, string content)
    {
        if (_directory is null) return;
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, name), content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the marker a crash can repeat, but corrections themselves aren't affected.
        }
    }

    private void TryDelete(string name)
    {
        if (_directory is null) return;
        try
        {
            File.Delete(Path.Combine(_directory, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
