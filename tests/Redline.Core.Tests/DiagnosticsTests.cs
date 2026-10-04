using Redline.Core.Diagnostics;
using Xunit;

namespace Redline.Core.Tests;

public class PerfCountersTests
{
    [Fact]
    public void ComputesPercentilesOverRecentSamples()
    {
        var perf = new PerfCounters();
        for (int ms = 1; ms <= 100; ms++)
            perf.Record("analysis", TimeSpan.FromMilliseconds(ms));

        var stat = Assert.Single(perf.Snapshot());
        Assert.Equal("analysis", stat.Name);
        Assert.Equal(100, stat.Count);
        Assert.Equal(50, stat.P50Ms, 3);
        Assert.Equal(95, stat.P95Ms, 3);
        Assert.Equal(100, stat.MaxMs, 3);
        Assert.Equal(50.5, stat.MeanMs, 3);
    }

    [Fact]
    public void WindowForgetsOldSamplesButKeepsTotalCount()
    {
        var perf = new PerfCounters();
        perf.Record("read", TimeSpan.FromSeconds(10)); // an old outlier
        for (int i = 0; i < PerfCounters.Window; i++)
            perf.Record("read", TimeSpan.FromMilliseconds(5));

        var stat = Assert.Single(perf.Snapshot());
        Assert.Equal(PerfCounters.Window + 1, stat.Count);
        Assert.Equal(5, stat.MaxMs, 3);
    }

    [Fact]
    public void SummaryListsCountersByName()
    {
        var perf = new PerfCounters();
        Assert.Equal(string.Empty, perf.Summary());
        perf.Record("overlay", TimeSpan.FromMilliseconds(3));
        perf.Record("analysis", TimeSpan.FromMilliseconds(7));
        Assert.Equal("analysis 7/7/7 ms (n=1); overlay 3/3/3 ms (n=1)", perf.Summary());
    }
}

public sealed class LogFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Redline.Tests", "logs-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 3, 9, 0, 0);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private LogFiles Create(long maxFile = 1000, long maxTotal = 5000) =>
        new(_dir, maxFile, maxTotal, TimeSpan.FromDays(7), () => _now);

    private string Touch(string name, int bytes, DateTime written)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, new string('x', bytes));
        File.SetLastWriteTime(path, written);
        return path;
    }

    [Fact]
    public void WritesToTodaysFileAndRollsOverAtMidnight()
    {
        var logs = Create();
        logs.Append("a\n");
        Assert.EndsWith("redline-20261003.log", logs.CurrentPath);

        _now = _now.AddDays(1);
        logs.Append("b\n");
        Assert.EndsWith("redline-20261004.log", logs.CurrentPath);
        Assert.Equal("a\n", File.ReadAllText(Path.Combine(_dir, "redline-20261003.log")));
    }

    [Fact]
    public void RollsToNumberedFileWhenFull()
    {
        var logs = Create(maxFile: 10);
        logs.Append("123456\n");
        logs.Append("abcdef\n"); // 14 bytes > 10
        Assert.EndsWith("redline-20261003-1.log", logs.CurrentPath);
        logs.Append("x\n");
        Assert.EndsWith("redline-20261003-1.log", logs.CurrentPath);
    }

    [Fact]
    public void ContinuesAnExistingFileWithRoom()
    {
        Touch("redline-20261003.log", 5, _now);
        var logs = Create(maxFile: 100);
        logs.Append("more\n");
        Assert.Equal(10, new FileInfo(Path.Combine(_dir, "redline-20261003.log")).Length);
    }

    [Fact]
    public void PrunesFilesOlderThanMaxAge()
    {
        var old = Touch("redline-20260920.log", 10, _now.AddDays(-13));
        var oldCrash = Touch("crash-20260921-101010-000.txt", 10, _now.AddDays(-12));
        var recent = Touch("redline-20261001.log", 10, _now.AddDays(-2));
        var unrelated = Touch("notes.txt", 10, _now.AddDays(-30));

        Assert.Equal(2, Create().Prune());
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(oldCrash));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void PrunesOldestUntilUnderTotalSize()
    {
        var a = Touch("redline-20261001.log", 400, _now.AddDays(-2));
        var b = Touch("redline-20261002.log", 400, _now.AddDays(-1));
        var c = Touch("redline-20261002-1.log", 400, _now.AddHours(-1));

        Assert.Equal(1, Create(maxTotal: 1000).Prune());
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.True(File.Exists(c));
    }

    [Fact]
    public void NeverPrunesTheCurrentFile()
    {
        var logs = Create(maxFile: 1000, maxTotal: 10);
        logs.Append(new string('y', 50) + "\n");
        Assert.Equal(0, logs.Prune());
        Assert.True(File.Exists(logs.CurrentPath));
    }
}

public sealed class CrashReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Redline.Tests", "crash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static Exception Thrown()
    {
        try
        {
            try { throw new InvalidOperationException("inner boom"); }
            catch (Exception inner) { throw new ApplicationException("outer boom", inner); }
        }
        catch (Exception ex) { return ex; }
    }

    [Fact]
    public void FormatIncludesTypesStackAndInnerExceptions()
    {
        var text = CrashReport.Format(Thrown(), "UI thread", DateTimeOffset.Now, "1.2.3", TimeSpan.FromMinutes(5));
        Assert.Contains("Version: 1.2.3", text);
        Assert.Contains("Context: UI thread", text);
        Assert.Contains("System.ApplicationException", text);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.Contains("--- inner ---", text);
        Assert.Contains(nameof(Thrown), text); // stack frame
    }

    [Fact]
    public void FormatsAggregateChildren()
    {
        var agg = new AggregateException(new TimeoutException("t"), new FormatException("f"));
        var text = CrashReport.Format(agg, "task", DateTimeOffset.Now, "v", TimeSpan.Zero);
        Assert.Contains("System.TimeoutException", text);
        Assert.Contains("System.FormatException", text);
    }

    [Fact]
    public void TerminatingCrashIsReportedOnceOnNextStart()
    {
        var path = CrashReport.Write(_dir, Thrown(), "AppDomain", terminating: true, "v", TimeSpan.Zero);
        Assert.Matches(@"crash-\d{8}-\d{6}-\d{3}\.txt$", path);

        Assert.Equal(path, CrashReport.TakePending(_dir));
        Assert.Null(CrashReport.TakePending(_dir));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void NonTerminatingReportLeavesNoMarker()
    {
        CrashReport.Write(_dir, Thrown(), "UI thread", terminating: false, "v", TimeSpan.Zero);
        Assert.Null(CrashReport.TakePending(_dir));
    }
}
