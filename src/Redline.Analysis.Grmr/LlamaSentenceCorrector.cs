using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>Where the model runs, for Settings and Diagnostics (e.g. "GPU: Intel(R) Iris(R) Xe Graphics").</summary>
    string Device => "CPU";
}

/// <summary>
/// GRMR-V3 through llama.cpp (LLamaSharp), with greedy decoding, on the GPU through Vulkan when allowed
/// and available (any NVIDIA, AMD or Intel GPU with a Vulkan driver), otherwise on the CPU.
/// </summary>
public sealed class LlamaSentenceCorrector : ISentenceCorrector
{
    /// <summary>More than the model has (Gemma 3 1B: 26 + output): everything goes to the GPU.</summary>
    private const int AllLayers = 999;

    // "using device Vulkan0 (Intel(R) Iris(R) Xe Graphics) - 8053 MiB free": names can contain parentheses.
    internal static readonly Regex DeviceLine = new(@"using device \S+ \((?<name>.*)\) - \d+ MiB free", RegexOptions.Compiled);

    private static readonly object s_nativeGate = new();
    private static bool s_nativeConfigured;
    private static bool s_vulkanAllowed;
    private static volatile bool s_gpuLoadFailed;
    private static volatile string? s_deviceName;

    private readonly LLamaWeights _weights;
    private readonly StatelessExecutor _executor;
    private Action? _firstSuccess;

    private LlamaSentenceCorrector(string modelPath, bool onGpu, Action? firstSuccess)
    {
        var parameters = new ModelParams(modelPath)
        {
            // One sentence (<= 500 chars, ~125 tokens) plus its correction fits easily.
            ContextSize = 1024,
            GpuLayerCount = onGpu ? AllLayers : 0,
            // Leave most cores to the user; generation is memory-bound beyond a few threads anyway.
            Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
        };
        if (onGpu)
        {
            // The 1B model fits on any GPU: keep it on one (ggml-vulkan leaves integrated GPUs out when a
            // discrete one exists). Not on the CPU: with no GPU devices, llama.cpp rejects main_gpu 0.
            parameters.SplitMode = GPUSplitMode.None;
            parameters.MainGpu = 0;
        }
        _weights = LLamaWeights.LoadFromFile(parameters);
        _executor = new StatelessExecutor(_weights, parameters);
        _firstSuccess = firstSuccess;
        Device = onGpu ? "GPU: " + (s_deviceName ?? "Vulkan") : "CPU";
    }

    public string Device { get; }

    /// <summary>
    /// True when the GPU is wanted but this process loaded the CPU-only runtime (llama.cpp's native
    /// library can be chosen once per process), so using the GPU needs a restart.
    /// </summary>
    public static bool GpuNeedsRestart
    {
        get { lock (s_nativeGate) return s_nativeConfigured && !s_vulkanAllowed; }
    }

    /// <summary>
    /// Loads the model, on the GPU if <paramref name="preferGpu"/> and the guard allow it and a Vulkan GPU is
    /// present; falls back to the CPU if loading on the GPU fails.
    /// </summary>
    /// <exception cref="Exception">The native runtime or the model file could not be loaded.</exception>
    public static LlamaSentenceCorrector Create(string modelPath, bool preferGpu, GpuGuard guard, ILogger logger)
    {
        bool tryGpu = preferGpu && !guard.Blocked && !s_gpuLoadFailed;
        ConfigureNative(allowVulkan: tryGpu, logger);
        tryGpu &= NativeApi.llama_supports_gpu_offload(); // false: the CPU runtime was loaded (no Vulkan GPU, or the GPU was off at first load)

        if (tryGpu)
        {
            guard.BeginTrial();
            try
            {
                var corrector = new LlamaSentenceCorrector(modelPath, onGpu: true, () => guard.EndTrial());
                logger.LogInformation("Grammar model on {Device}", corrector.Device);
                return corrector;
            }
            catch (Exception ex)
            {
                guard.EndTrial(passed: false);
                s_gpuLoadFailed = true;
                logger.LogWarning(ex, "Couldn't load the grammar model on the GPU; using the CPU until Redline restarts");
            }
        }
        return new LlamaSentenceCorrector(modelPath, onGpu: false, firstSuccess: null);
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

        Interlocked.Exchange(ref _firstSuccess, null)?.Invoke();
        return GrmrPrompt.ParseOutput(output.ToString(), hitTokenLimit: tokens >= maxTokens);
    }

    public void Dispose() => _weights.Dispose();

    /// <summary>
    /// Once per process, before the native library loads: pick the Vulkan or the CPU-only runtime, keep
    /// llama.cpp's console chatter out and errors in our log, and note the GPU's name.
    /// </summary>
    private static void ConfigureNative(bool allowVulkan, ILogger logger)
    {
        lock (s_nativeGate)
        {
            if (s_nativeConfigured) return;
            s_nativeConfigured = true;
            s_vulkanAllowed = allowVulkan;
        }
        NativeLibraryConfig.All
            .WithCuda(false)
            .WithVulkan(allowVulkan)
            .WithLogCallback((level, message) =>
            {
                // llama.cpp messages describe the model and runtime, never the prompt.
                if (level == LLamaLogLevel.Error)
                    logger.LogWarning("llama.cpp: {Message}", message.TrimEnd());
                else if (message.Contains("using device", StringComparison.Ordinal) && DeviceLine.Match(message) is { Success: true } m)
                    s_deviceName = m.Groups["name"].Value.Trim();
            });
    }
}
