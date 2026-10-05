using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Redline.Core.Interfaces;
using Redline.Core.Models;

namespace Redline.Analysis.Harper;

/// <summary>Grammar and style checking via harper-core (native/harper-ffi).</summary>
public sealed class HarperAnalyzer : ITextAnalyzer
{
    private readonly ILogger _logger;

    public HarperAnalyzer(ILogger<HarperAnalyzer>? logger = null)
    {
        _logger = logger ?? NullLogger<HarperAnalyzer>.Instance;
        try
        {
            Version = HarperInterop.Version();
            IsAvailable = true;
            _logger.LogInformation("Loaded {Version}", Version);

            // Building the curated dictionary + rule set costs ~0.5 s on first lint; pay it now,
            // off-thread, instead of on the user's first keystroke.
            _ = Task.Run(() => HarperInterop.Lint("Warm up."));
        }
        catch (Exception ex)
        {
            // Any failure here (missing DLL, wrong build, or a security policy such as AppLocker blocking an unsigned
            // native DLL) must only cost grammar checking: this runs while the analysis pipeline is being built.
            _logger.LogWarning("harper_ffi.dll not loadable ({Reason}: {Message}); grammar checking disabled", ex.GetType().Name, ex.Message);
            UnavailableReason = $"The grammar engine (harper_ffi.dll) couldn't load ({ex.GetType().Name}); a security policy may be blocking it.";
        }
    }

    public string Name => "Harper";
    public bool IsAvailable { get; }
    public string? UnavailableReason { get; }
    public string? Version { get; }

    public Task<IReadOnlyList<TextIssue>> AnalyzeAsync(TextAnalysisRequest request, CancellationToken ct)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(request.Text))
            return Task.FromResult<IReadOnlyList<TextIssue>>(Array.Empty<TextIssue>());

        // The native call can't be interrupted; cancellation only skips work not yet started.
        return Task.Run(() => HarperResultMapper.Map(HarperInterop.Lint(request.Text), request), ct);
    }
}
