using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Logging;

namespace Redline.Analysis.Grmr;

/// <summary>Corrects one sentence at a time. Not thread-safe: the analyzer calls it from one worker.</summary>
public interface ISentenceCorrector : IDisposable
{
    /// <summary>The corrected sentence, or null when the model produced nothing usable.</summary>
    Task<string?> CorrectAsync(string sentence, CancellationToken ct);
}

/// <summary>GRMR-V3 through llama.cpp (LLamaSharp), on the CPU, with greedy decoding.</summary>
public sealed class LlamaSentenceCorrector : ISentenceCorrector
{
    private static int s_nativeConfigured;

    private readonly LLamaWeights _weights;
    private readonly StatelessExecutor _executor;

    /// <exception cref="Exception">The native runtime or the model file could not be loaded.</exception>
    public LlamaSentenceCorrector(string modelPath, ILogger logger)
    {
        ConfigureNative(logger);
        var parameters = new ModelParams(modelPath)
        {
            // One sentence (<= 500 chars, ~125 tokens) plus its correction fits easily.
            ContextSize = 1024,
            GpuLayerCount = 0,
            // Leave most cores to the user; generation is memory-bound beyond a few threads anyway.
            Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
        };
        _weights = LLamaWeights.LoadFromFile(parameters);
        _executor = new StatelessExecutor(_weights, parameters);
    }

    public async Task<string?> CorrectAsync(string sentence, CancellationToken ct)
    {
        int maxTokens = GrmrPrompt.MaxOutputTokens(sentence);
        var inference = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = [GrmrPrompt.EndOfTurn],
            // Greedy: the same sentence always gets the same answer, so underlines don't flicker.
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0f },
        };

        var output = new StringBuilder();
        int tokens = 0;
        await foreach (var piece in _executor.InferAsync(GrmrPrompt.Build(sentence), inference, ct).ConfigureAwait(false))
        {
            output.Append(piece);
            tokens++;
        }
        return GrmrPrompt.ParseOutput(output.ToString(), hitTokenLimit: tokens >= maxTokens);
    }

    public void Dispose() => _weights.Dispose();

    /// <summary>Once per process, before the native library loads: keep llama.cpp's console chatter out, errors in our log.</summary>
    private static void ConfigureNative(ILogger logger)
    {
        if (Interlocked.Exchange(ref s_nativeConfigured, 1) != 0) return;
        NativeLibraryConfig.All.WithLogCallback((level, message) =>
        {
            // llama.cpp messages describe the model and runtime, never the prompt.
            if (level == LLamaLogLevel.Error)
                logger.LogWarning("llama.cpp: {Message}", message.TrimEnd());
        });
    }
}
