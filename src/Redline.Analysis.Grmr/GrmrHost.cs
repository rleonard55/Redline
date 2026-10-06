using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Redline.Core.Settings;

namespace Redline.Analysis.Grmr;

/// <summary>
/// The grammar model's own process: <c>Redline.exe --grammar-host</c>, started by <see cref="HostedSentenceCorrector"/>.
/// Loading the model in Redline itself kept memory that unloading couldn't give back (the Vulkan runtime holds
/// ~400 MB until the process exits; on an integrated GPU the loaded model is another ~1.2 GB of RAM). In its own
/// process, ending the process frees all of it, and a crash in llama.cpp or the GPU driver only ends this process.
/// Talks JSON lines over two anonymous pipes (stdout stays free for whatever native code prints); exits when
/// Redline closes its end, including when Redline itself ends.
/// </summary>
public static class GrmrHost
{
    public const string Argument = "--grammar-host";

    public static bool IsHostCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], Argument, StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<string> Arguments(string modelPath, AiDevice device, string inHandle, string outHandle) =>
        [Argument, "--model", modelPath, "--device", device.ToString(), "--in", inHandle, "--out", outHandle];

    /// <summary>Runs the host until Redline closes the pipe. Returns the process exit code.</summary>
    public static int Run(IReadOnlyList<string> args)
    {
        string? model = Value(args, "--model"), inHandle = Value(args, "--in"), outHandle = Value(args, "--out");
        if (model is null || inHandle is null || outHandle is null) return 2;
        var device = Enum.TryParse<AiDevice>(Value(args, "--device"), out var d) ? d : AiDevice.Cpu;

        using var input = new AnonymousPipeClientStream(PipeDirection.In, inHandle);
        using var output = new AnonymousPipeClientStream(PipeDirection.Out, outHandle);
        // Off the caller's thread: the WPF startup thread has a synchronization context to deadlock on.
        return Task.Run(() => ServeAsync(input, output,
            logger => LlamaSentenceCorrector.Create(model, device, new GpuGuard(null), logger))).GetAwaiter().GetResult();
    }

    /// <summary>Loads the model, says "ready" (or "error"), then answers requests until the input ends.</summary>
    internal static async Task<int> ServeAsync(Stream input, Stream output, Func<ILogger, ISentenceCorrector> load)
    {
        var channel = new HostChannel(output);
        ISentenceCorrector corrector;
        try
        {
            corrector = load(new ChannelLogger(channel));
        }
        catch (Exception ex)
        {
            channel.Send(new HostMessage { Type = HostMessage.ErrorType, Error = ex.GetType().Name + ": " + ex.Message });
            return 1;
        }

        using (corrector)
        {
            channel.Send(new HostMessage { Type = HostMessage.ReadyType, Device = corrector.Device });
            using var reader = new StreamReader(input, HostMessage.Encoding);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (HostMessage.Parse(line) is not { Type: HostMessage.RequestType, Text: { } sentence } request) continue;
                HostMessage answer;
                try
                {
                    answer = new HostMessage { Type = HostMessage.AnswerType, Id = request.Id, Text = await corrector.CorrectAsync(sentence, CancellationToken.None).ConfigureAwait(false) };
                }
                catch (Exception ex)
                {
                    answer = new HostMessage { Type = HostMessage.AnswerType, Id = request.Id, Error = ex.GetType().Name }; // never the sentence
                }
                channel.Send(answer);
            }
        }
        return 0;
    }

    private static string? Value(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    /// <summary>Writes whole lines; the model's log callback and the request loop can write at the same time.</summary>
    private sealed class HostChannel(Stream output)
    {
        private readonly StreamWriter _writer = new(output, HostMessage.Encoding) { AutoFlush = true };

        public void Send(HostMessage message)
        {
            lock (_writer)
            {
                try
                {
                    _writer.WriteLine(message.ToJson());
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Redline is gone; the read loop ends on its own.
                }
            }
        }
    }

    /// <summary>The model's log lines (runtime and device, never text) go to Redline's log.</summary>
    private sealed class ChannelLogger(HostChannel channel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = formatter(state, exception);
            if (exception is not null) text += " (" + exception.GetType().Name + ": " + exception.Message + ")";
            channel.Send(new HostMessage { Type = HostMessage.LogType, Level = logLevel.ToString(), Text = text });
        }
    }
}

/// <summary>One line of the host protocol.</summary>
internal sealed record HostMessage
{
    public const string ReadyType = "ready", ErrorType = "error", LogType = "log", RequestType = "request", AnswerType = "answer";

    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Type { get; init; } = "";
    public long Id { get; init; }
    /// <summary>Request: the sentence. Answer: the correction (null = nothing usable). Log: the line.</summary>
    public string? Text { get; init; }
    public string? Device { get; init; }
    public string? Error { get; init; }
    public string? Level { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static HostMessage? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<HostMessage>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
