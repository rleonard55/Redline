using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Redline.Core.Settings;

namespace Redline.Analysis.Grmr;

/// <summary>The grammar model can't be used this session (it failed to load, or its process keeps dying).</summary>
public sealed class GrammarModelLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Runs the model in its own process (<see cref="GrmrHost"/>): loading starts the process, <see cref="Dispose"/>
/// ends it, so an unloaded model costs no memory at all. If the process dies, the next sentence starts a new one;
/// if it died during the session's first GPU use, the GPU is blocked (<see cref="GpuGuard"/>) and the new one runs
/// on the CPU. Not thread-safe, like every <see cref="ISentenceCorrector"/>.
/// </summary>
public sealed class HostedSentenceCorrector : ISentenceCorrector
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SentenceTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);
    private const int MaxFailuresInARow = 3;

    private readonly Func<AiDevice, HostProcess> _start;
    private readonly AiDevice _wanted;
    private readonly GpuGuard _guard;
    private readonly ILogger _logger;
    private Session? _session;
    private string _device = "CPU";
    private long _nextId;
    private int _failuresInARow;

    internal HostedSentenceCorrector(Func<AiDevice, HostProcess> start, AiDevice wanted, GpuGuard guard, ILogger logger)
    {
        _start = start;
        _wanted = wanted;
        _guard = guard;
        _logger = logger;
        _session = StartSession();
    }

    /// <summary>Starts <paramref name="hostExe"/> <c>--grammar-host</c> and waits until the model is loaded.</summary>
    /// <exception cref="GrammarModelLoadException">The process couldn't start or the model couldn't load.</exception>
    public static HostedSentenceCorrector Start(string hostExe, string modelPath, AiDevice device, GpuGuard guard, ILogger logger) =>
        new(d => ProcessHost.Start(hostExe, modelPath, d), device, guard, logger);

    public string Device => _device;

    public async Task<string?> CorrectAsync(string sentence, CancellationToken ct)
    {
        var session = _session ??= StartSession();
        HostMessage answer;
        try
        {
            answer = await session.RequestAsync(++_nextId, sentence, SentenceTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HostExitedException or TimeoutException)
        {
            _session = null;
            session.Dispose();
            if (session.InTrial)
            {
                _guard.Block();
                _logger.LogWarning("The grammar model's process stopped on its first sentence on the GPU; using the processor from now on");
            }
            else
            {
                _logger.LogWarning("The grammar model's process {What}", ex is TimeoutException ? "didn't answer in time and was stopped" : "stopped unexpectedly");
            }
            if (++_failuresInARow >= MaxFailuresInARow)
                throw new GrammarModelLoadException($"The grammar model's process failed {_failuresInARow} times in a row", ex);
            throw;
        }

        _failuresInARow = 0;
        if (session.InTrial)
        {
            session.InTrial = false;
            _guard.EndTrial();
        }
        if (answer.Error is { } error) throw new InvalidOperationException("The grammar model failed: " + error);
        return answer.Text;
    }

    /// <summary>Ends the process: closing its input lets it free the model and exit.</summary>
    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }

    /// <summary>On the GPU when wanted and not blocked; a process that dies while loading there is retried on the CPU.</summary>
    private Session StartSession()
    {
        bool gpu = _wanted != AiDevice.Cpu && !_guard.Blocked;
        while (true)
        {
            bool trial = gpu && !_guard.TrialPassed;
            if (trial) _guard.BeginTrial();
            Session? session = null;
            try
            {
                session = new Session(_start(gpu ? _wanted : AiDevice.Cpu), _logger);
                var ready = session.WaitReady(LoadTimeout);
                if (ready.Type == HostMessage.ErrorType)
                    throw new GrammarModelLoadException("Couldn't load the grammar model: " + ready.Error);
                _device = ready.Device ?? "CPU";
                session.InTrial = trial && _device.StartsWith("GPU", StringComparison.Ordinal);
                if (trial && !session.InTrial) _guard.EndTrial(passed: false); // no usable GPU: nothing to guard
                return session;
            }
            catch (Exception ex)
            {
                session?.Dispose();
                if (trial) _guard.EndTrial(passed: false);
                if (gpu && ex is HostExitedException)
                {
                    _guard.Block();
                    _logger.LogWarning("The grammar model's process stopped while loading on the GPU; using the processor from now on");
                    gpu = false;
                    continue;
                }
                if (ex is GrammarModelLoadException) throw;
                throw new GrammarModelLoadException("The grammar model's process couldn't start", ex);
            }
        }
    }

    /// <summary>The process stopped (or closed its pipe) before answering.</summary>
    private sealed class HostExitedException() : IOException("The grammar model's process stopped");

    /// <summary>One host process: the read loop delivers "ready", answers and log lines.</summary>
    private sealed class Session : IDisposable
    {
        private readonly HostProcess _process;
        private readonly ILogger _logger;
        private readonly StreamWriter _writer;
        private readonly TaskCompletionSource<HostMessage> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private TaskCompletionSource<HostMessage>? _pending; // guarded by _gate
        private bool _closed;                                // guarded by _gate

        public Session(HostProcess process, ILogger logger)
        {
            _process = process;
            _logger = logger;
            _writer = new StreamWriter(process.Input, HostMessage.Encoding) { AutoFlush = true };
            _ = Task.Run(ReadLoopAsync);
        }

        /// <summary>This session's first GPU sentence hasn't come back yet (a crash now blocks the GPU).</summary>
        public bool InTrial { get; set; }

        public HostMessage WaitReady(TimeSpan timeout)
        {
            try
            {
                if (!_ready.Task.Wait(timeout)) throw new TimeoutException("The grammar model didn't load in time");
                return _ready.Task.Result;
            }
            catch (AggregateException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }

        public async Task<HostMessage> RequestAsync(long id, string sentence, TimeSpan timeout, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<HostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_closed) throw new HostExitedException();
                _pending = answer;
            }
            try
            {
                await _writer.WriteLineAsync(new HostMessage { Type = HostMessage.RequestType, Id = id, Text = sentence }.ToJson().AsMemory(), ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new HostExitedException();
            }

            while (true)
            {
                var message = await answer.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
                if (message.Id == id) return message;
                lock (_gate) // a late answer to an earlier request: keep waiting for ours
                {
                    answer = new TaskCompletionSource<HostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (_closed) throw new HostExitedException();
                    _pending = answer;
                }
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                using var reader = new StreamReader(_process.Output, HostMessage.Encoding);
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    switch (HostMessage.Parse(line))
                    {
                        case { Type: HostMessage.LogType } log:
                            var level = Enum.TryParse<LogLevel>(log.Level, out var parsed) ? parsed : LogLevel.Information;
                            _logger.Log(level, "{Message}", log.Text);
                            break;
                        case { Type: HostMessage.ReadyType or HostMessage.ErrorType } ready:
                            _ready.TrySetResult(ready);
                            break;
                        case { Type: HostMessage.AnswerType } answer:
                            lock (_gate) _pending?.TrySetResult(answer);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The pipe broke: same as the process ending.
            }
            finally
            {
                lock (_gate)
                {
                    _closed = true;
                    _pending?.TrySetException(new HostExitedException());
                }
                _ready.TrySetException(new HostExitedException());
            }
        }

        public void Dispose()
        {
            try
            {
                _writer.Dispose(); // end of input: the host frees the model and exits
            }
            catch (IOException)
            {
            }
            if (!_process.WaitForExit(ExitTimeout))
            {
                _logger.LogWarning("The grammar model's process didn't exit; stopping it");
                _process.Kill();
            }
            _process.Dispose();
        }
    }
}

/// <summary>A started host: its pipes and its lifetime. Tests run the host in-process.</summary>
internal abstract class HostProcess : IDisposable
{
    /// <summary>To the host.</summary>
    public abstract Stream Input { get; }
    /// <summary>From the host.</summary>
    public abstract Stream Output { get; }
    public abstract bool WaitForExit(TimeSpan timeout);
    public abstract void Kill();

    public virtual void Dispose()
    {
        Input.Dispose();
        Output.Dispose();
    }
}

/// <summary><c>Redline.exe --grammar-host</c> with an anonymous pipe each way.</summary>
internal sealed class ProcessHost : HostProcess
{
    private readonly Process _process;
    private readonly AnonymousPipeServerStream _toHost;
    private readonly AnonymousPipeServerStream _fromHost;

    private ProcessHost(Process process, AnonymousPipeServerStream toHost, AnonymousPipeServerStream fromHost)
    {
        _process = process;
        _toHost = toHost;
        _fromHost = fromHost;
    }

    public static ProcessHost Start(string hostExe, string modelPath, AiDevice device)
    {
        var toHost = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var fromHost = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(hostExe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in GrmrHost.Arguments(modelPath, device, toHost.GetClientHandleAsString(), fromHost.GetClientHandleAsString()))
                start.ArgumentList.Add(argument);
            process = Process.Start(start) ?? throw new InvalidOperationException("The grammar model's process didn't start");
        }
        finally
        {
            // Only the host may hold the other ends, or its exit wouldn't close the pipe.
            toHost.DisposeLocalCopyOfClientHandle();
            fromHost.DisposeLocalCopyOfClientHandle();
            if (process is null)
            {
                toHost.Dispose();
                fromHost.Dispose();
            }
        }

        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal; // background work: the user's apps come first
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        return new ProcessHost(process, toHost, fromHost);
    }

    public override Stream Input => _toHost;
    public override Stream Output => _fromHost;

    public override bool WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout);

    public override void Kill()
    {
        try
        {
            _process.Kill();
            _process.WaitForExit(ExitWait);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    public override void Dispose()
    {
        base.Dispose();
        _process.Dispose();
    }
}
