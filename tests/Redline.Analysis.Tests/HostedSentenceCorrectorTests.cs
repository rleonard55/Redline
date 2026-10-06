using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Redline.Analysis.Grmr;
using Redline.Core.Settings;
using Xunit;

namespace Redline.Analysis.Tests;

/// <summary>The model's own process, with the host run in-process over real anonymous pipes and a fake model.</summary>
public sealed class HostedSentenceCorrectorTests : IDisposable
{
    private sealed class FakeModel(string device, Action<string>? onSentence = null) : ISentenceCorrector
    {
        public string Device => device;
        public bool Disposed { get; private set; }

        public Task<string?> CorrectAsync(string sentence, CancellationToken ct)
        {
            onSentence?.Invoke(sentence);
            if (sentence == "boom") throw new InvalidOperationException("model error");
            return Task.FromResult<string?>(sentence.Replace(" go ", " goes "));
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary><see cref="GrmrHost.ServeAsync"/> on a background task; <see cref="Crash"/> closes its output like a dying process.</summary>
    private sealed class InProcessHost : HostProcess
    {
        private readonly AnonymousPipeServerStream _toHost = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _fromHost = new(PipeDirection.In);
        private readonly AnonymousPipeClientStream _hostOut;
        private readonly Task<int> _served;

        public InProcessHost(Func<InProcessHost, ILogger, ISentenceCorrector> load)
        {
            var hostIn = new AnonymousPipeClientStream(PipeDirection.In, _toHost.ClientSafePipeHandle);
            _hostOut = new AnonymousPipeClientStream(PipeDirection.Out, _fromHost.ClientSafePipeHandle);
            _served = Task.Run(async () =>
            {
                try { return await GrmrHost.ServeAsync(hostIn, _hostOut, logger => load(this, logger)); }
                finally { hostIn.Dispose(); _hostOut.Dispose(); }
            });
        }

        public void Crash() => _hostOut.Dispose();

        public int? ExitCode => _served.IsCompletedSuccessfully ? _served.Result : null;

        public override Stream Input => _toHost;
        public override Stream Output => _fromHost;
        public override bool WaitForExit(TimeSpan timeout) => ((Task)_served).Wait(timeout);
        public override void Kill() => Crash();
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "redline-host-" + Guid.NewGuid().ToString("N"));
    private readonly List<(bool Gpu, InProcessHost Host)> _started = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private HostedSentenceCorrector Create(Func<bool, InProcessHost, ILogger, ISentenceCorrector> load, bool preferGpu, GpuGuard guard) =>
        new(device =>
        {
            bool gpu = device != AiDevice.Cpu;
            var host = new InProcessHost((h, logger) => load(gpu, h, logger));
            _started.Add((gpu, host));
            return host;
        }, preferGpu ? AiDevice.AnyGpu : AiDevice.Cpu, guard, NullLogger.Instance);

    [Fact]
    public async Task AnswersComeBackFromTheHost_AndDisposeEndsIt()
    {
        FakeModel? model = null;
        var corrector = Create((_, _, _) => model = new FakeModel("CPU"), preferGpu: false, new GpuGuard(null));

        Assert.Equal("CPU", corrector.Device);
        Assert.Equal("She goes to school.", await corrector.CorrectAsync("She go to school.", default));
        Assert.Equal("Fine.", await corrector.CorrectAsync("Fine.", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => corrector.CorrectAsync("boom", default)); // a model error is per sentence
        Assert.Equal("Still fine.", await corrector.CorrectAsync("Still fine.", default));

        corrector.Dispose();
        Assert.True(model!.Disposed);
        Assert.Equal(0, Assert.Single(_started).Host.ExitCode);
    }

    [Fact]
    public void ModelThatFailsToLoad_Throws()
    {
        var ex = Assert.Throws<GrammarModelLoadException>(() =>
            Create((_, _, _) => throw new FileNotFoundException("no model"), preferGpu: false, new GpuGuard(null)));
        Assert.Contains("no model", ex.Message);
    }

    [Fact]
    public async Task HostThatDiesOnTheFirstGpuSentence_BlocksTheGpu_AndTheNextStartUsesTheCpu()
    {
        var guard = new GpuGuard(_dir);
        var corrector = Create((gpu, host, _) => new FakeModel(gpu ? "GPU: Fake" : "CPU", s => { if (gpu) host.Crash(); }), preferGpu: true, guard);
        Assert.Equal("GPU: Fake", corrector.Device);
        Assert.True(File.Exists(Path.Combine(_dir, GpuGuard.TrialFile)));

        await Assert.ThrowsAnyAsync<IOException>(() => corrector.CorrectAsync("She go to school.", default));
        Assert.True(guard.Blocked);
        Assert.False(File.Exists(Path.Combine(_dir, GpuGuard.TrialFile)));

        Assert.Equal("She goes to school.", await corrector.CorrectAsync("She go to school.", default));
        Assert.Equal("CPU", corrector.Device);
        Assert.Equal([true, false], _started.Select(s => s.Gpu));
        corrector.Dispose();
    }

    [Fact]
    public async Task HostThatDiesWhileLoadingOnTheGpu_IsRetriedOnTheCpu()
    {
        var guard = new GpuGuard(_dir);
        var corrector = Create((gpu, host, _) =>
        {
            if (gpu) { host.Crash(); throw new InvalidOperationException("driver crash"); }
            return new FakeModel("CPU");
        }, preferGpu: true, guard);

        Assert.Equal("CPU", corrector.Device);
        Assert.True(guard.Blocked);
        Assert.Equal("She goes to school.", await corrector.CorrectAsync("She go to school.", default));
        corrector.Dispose();
    }

    [Fact]
    public async Task AfterTheGpuWorked_ACrashDoesNotBlockIt()
    {
        var guard = new GpuGuard(_dir);
        int sentences = 0;
        var corrector = Create((gpu, host, _) => new FakeModel("GPU: Fake", _ => { if (++sentences == 2) host.Crash(); }), preferGpu: true, guard);

        Assert.Equal("She goes to school.", await corrector.CorrectAsync("She go to school.", default));
        await Assert.ThrowsAnyAsync<IOException>(() => corrector.CorrectAsync("Second.", default));
        Assert.False(guard.Blocked);
        Assert.Equal("Third.", await corrector.CorrectAsync("Third.", default)); // a new host, still on the GPU
        Assert.Equal([true, true], _started.Select(s => s.Gpu));
        corrector.Dispose();
    }

    [Fact]
    public async Task HostThatKeepsDying_StopsTheModelForTheSession()
    {
        var corrector = Create((_, host, _) => new FakeModel("CPU", _ => host.Crash()), preferGpu: false, new GpuGuard(null));

        await Assert.ThrowsAnyAsync<IOException>(() => corrector.CorrectAsync("One.", default));
        await Assert.ThrowsAnyAsync<IOException>(() => corrector.CorrectAsync("Two.", default));
        await Assert.ThrowsAsync<GrammarModelLoadException>(() => corrector.CorrectAsync("Three.", default));
    }

    [Fact]
    public async Task AnalyzerTurnsItselfOff_WhenTheModelStopsWorking()
    {
        using var analyzer = new GrmrAnalyzer(() => "model.gguf",
            _ => Create((_, host, _) => new FakeModel("CPU", _ => host.Crash()), preferGpu: false, new GpuGuard(null))) { Enabled = true };

        await analyzer.AnalyzeAsync(new Redline.Core.Models.TextAnalysisRequest { Text = "One. Two. Three. Four.", SnapshotVersion = 1 }, default);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!analyzer.Failed && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(analyzer.Failed);
        Assert.False(analyzer.IsAvailable);
    }
}
