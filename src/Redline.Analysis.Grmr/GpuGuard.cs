namespace Redline.Analysis.Grmr;

/// <summary>
/// Keeps a GPU driver crash from becoming a crash loop. A crash inside the Vulkan driver takes the whole
/// process down with no managed exception to catch, so the first GPU use of a session is bracketed by a
/// marker file: written before the model loads on the GPU, deleted after the first sentence succeeds (or
/// on a clean exit). If the next start finds the marker, that session died on the GPU: the GPU is blocked
/// (CPU only) until the user turns the GPU setting off and on again. Thread-safe.
/// </summary>
public sealed class GpuGuard : IDisposable
{
    public const string TrialFile = "gpu-trial";
    public const string BlockedFile = "gpu-blocked";

    private readonly string? _directory;
    private readonly object _gate = new();
    private bool _inTrial;
    private bool _blocked;

    /// <param name="directory">Where the markers live (the models folder); null keeps state in memory only.</param>
    public GpuGuard(string? directory)
    {
        _directory = directory;
        if (directory is null) return;

        var trial = Path.Combine(directory, TrialFile);
        if (File.Exists(trial))
        {
            CrashedLastTime = true;
            TryWrite(BlockedFile);
            TryDelete(TrialFile);
        }
        _blocked = File.Exists(Path.Combine(directory, BlockedFile));
    }

    /// <summary>The previous session ended while its first GPU use was in progress.</summary>
    public bool CrashedLastTime { get; }

    /// <summary>GPU use is off because it crashed once; cleared by <see cref="Reset"/>.</summary>
    public bool Blocked
    {
        get { lock (_gate) return _blocked; }
    }

    /// <summary>Before loading the model on the GPU. Does nothing once the GPU has worked this session.</summary>
    public void BeginTrial()
    {
        lock (_gate)
        {
            if (_inTrial || TrialPassed) return;
            _inTrial = true;
            TryWrite(TrialFile);
        }
    }

    /// <summary>The GPU produced a result (or wasn't used after all): it's safe for this session.</summary>
    public void EndTrial(bool passed = true)
    {
        lock (_gate)
        {
            if (!_inTrial) return;
            _inTrial = false;
            TrialPassed |= passed;
            TryDelete(TrialFile);
        }
    }

    /// <summary>The GPU worked once this session; later loads skip the marker.</summary>
    public bool TrialPassed { get; private set; }

    /// <summary>The user asked to try the GPU again.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _blocked = false;
            TryDelete(BlockedFile);
        }
    }

    /// <summary>Clean exit: an unfinished trial didn't crash.</summary>
    public void Dispose() => EndTrial(passed: false);

    private void TryWrite(string name)
    {
        if (_directory is null) return;
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, name), DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the marker a crash can repeat, but GPU use itself isn't affected.
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
