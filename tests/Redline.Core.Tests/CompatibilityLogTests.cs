using Redline.Core.Corrections;
using Redline.Core.Diagnostics;
using Redline.Core.Models;
using Xunit;

namespace Redline.Core.Tests;

public sealed class CompatibilityLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"redline-compat-{Guid.NewGuid():N}", "compatibility.json");
    private DateTime _now = new(2026, 10, 4, 12, 0, 0);

    private static readonly TextSurfaceContext Teams = new()
    {
        SurfaceId = "1", ProcessName = "ms-teams.exe", ControlType = "Edit", ClassName = "ck-editor__editable",
        FrameworkId = "Chrome", WindowTitle = "Chat | Secret project", ProcessId = 42,
    };

    private static readonly TextSurfaceCapabilities Caps = new() { SupportedPatterns = ["Value", "Text"] };

    private CompatibilityLog NewLog() => new(_path, "0.7.0", now: () => _now);

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void CountsPerAppAndField_AndReportsAWorkingField()
    {
        var log = NewLog();
        log.Attached(Teams, Caps, "25.1.2");
        log.TextRead(Teams, empty: false);
        log.Layout(Teams, placed: 3, total: 3);
        log.Correction(Teams, new CorrectionResult(CorrectionOutcome.Applied, "SelectAndType", "Fixed.", TimeSpan.FromMilliseconds(120)));

        var e = Assert.Single(log.Entries());
        Assert.Equal(("ms-teams.exe", "Edit", "ck-editor__editable", "Chrome", "25.1.2"), (e.Process, e.ControlType, e.ClassName, e.Framework, e.AppVersion));
        Assert.Equal(["Text", "Value"], e.Patterns);
        Assert.Equal((1, 1, 0, 3, 0, 1), (e.Attaches, e.TextReads, e.EmptyReads, e.IssuesPlaced, e.IssuesNotPlaced, e.FixesApplied));
        Assert.Equal(1, e.FixMethods["SelectAndType"]);
        Assert.Equal("Working", CompatibilityLog.StatusOf(e));
    }

    [Fact]
    public void Report_NeverContainsTitlesOrText()
    {
        var log = NewLog();
        log.Attached(Teams, Caps, null);
        log.Correction(Teams, new CorrectionResult(CorrectionOutcome.Rejected, "none", "The text changed after this issue was found.", TimeSpan.Zero));

        var json = log.ToJson();
        Assert.DoesNotContain("Secret project", json);
        Assert.DoesNotContain("windowTitle", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("surfaceId", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"redlineVersion\": \"0.7.0\"", json);
        Assert.Contains("Rejected: The text changed after this issue was found.", json);
    }

    [Theory]
    [InlineData("no positions", "No underlines")]
    [InlineData("empty reads", "Reads as empty")]
    [InlineData("fixes fail", "Fixes failing")]
    [InlineData("partial", "Some underlines missing")]
    [InlineData("blocked only", "Skipped: Password field")]
    public void Status_NamesTheProblem(string scenario, string expected)
    {
        var log = NewLog();
        switch (scenario)
        {
            case "no positions":
                log.Attached(Teams, Caps, null);
                log.TextRead(Teams, false);
                log.Layout(Teams, 0, 4);
                break;
            case "empty reads":
                log.Attached(Teams, Caps, null);
                log.TextRead(Teams, true);
                log.TextRead(Teams, true);
                break;
            case "fixes fail":
                log.Attached(Teams, Caps, null);
                log.Layout(Teams, 2, 2);
                log.Correction(Teams, new CorrectionResult(CorrectionOutcome.Reverted, "SelectAndType", "Undone.", TimeSpan.Zero));
                break;
            case "partial":
                log.Attached(Teams, Caps, null);
                log.Layout(Teams, 1, 3);
                break;
            case "blocked only":
                log.Blocked("app.exe", "Edit", "", "Win32", "Password field");
                break;
        }
        Assert.Equal(expected, CompatibilityLog.StatusOf(Assert.Single(log.Entries())));
    }

    [Fact]
    public void SavesAndReloads_AndDropsEntriesOlderThanTheRetention()
    {
        var log = NewLog();
        log.Attached(Teams, Caps, "25.1.2");
        _now = _now.AddDays(10);
        log.Blocked("KeePass.exe", "Edit", "", "Win32", "Password manager");
        log.Save();

        var reloaded = NewLog();
        Assert.Equal(2, reloaded.Entries().Count);

        _now = _now.AddDays(CompatibilityLog.RetentionDays - 5); // Teams last seen 35 days ago, KeePass 25
        var later = NewLog();
        Assert.Equal("KeePass.exe", Assert.Single(later.Entries()).Process);
    }

    [Fact]
    public void Clear_EmptiesTheFile()
    {
        var log = NewLog();
        log.Attached(Teams, Caps, null);
        log.Save();
        log.Clear();
        Assert.Empty(NewLog().Entries());
    }

    [Fact]
    public void ReasonListsAreCapped()
    {
        var log = NewLog();
        for (int i = 0; i < 100; i++)
            log.Blocked("app.exe", "Edit", "", "Win32", $"reason {i}");
        Assert.True(Assert.Single(log.Entries()).Blocked.Count <= 20);
    }
}

public class PerfRatesTests
{
    [Fact]
    public void Rates_ArePerMinuteSinceTheLastCounts()
    {
        var perf = new PerfCounters();
        perf.Record("analysis", TimeSpan.FromMilliseconds(5));
        var before = perf.Counts();
        for (int i = 0; i < 30; i++) perf.Record("analysis", TimeSpan.FromMilliseconds(5));
        for (int i = 0; i < 6; i++) perf.Record("overlay", TimeSpan.FromMilliseconds(2));

        Assert.Equal("analysis 15.0/min; overlay 3.0/min", PerfCounters.Rates(before, perf.Counts(), TimeSpan.FromMinutes(2)));
        Assert.Equal(string.Empty, PerfCounters.Rates(perf.Counts(), perf.Counts(), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ResourceUsage_ReadsThisProcess()
    {
        var usage = ResourceUsage.Current();
        Assert.True(usage.PrivateBytes > 0 && usage.WorkingSetBytes > 0 && usage.Threads > 0);
        Assert.Contains("MB private", usage.ToString());
    }
}
