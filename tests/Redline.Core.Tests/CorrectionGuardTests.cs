using Redline.Core.Corrections;
using Xunit;

namespace Redline.Core.Tests;

public sealed class CorrectionGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Redline.Tests", "guard-" + Guid.NewGuid().ToString("N"));

    private static readonly string[] Strategies = ["EditMessage", "SelectAndType", "SelectAndPaste"];

    [Fact]
    public void CleanCorrection_LeavesNoCrash()
    {
        var guard = new CorrectionGuard(_dir);
        guard.Mark("SelectAndType", "input", "Notepad.exe");
        guard.Finish("SelectAndType");

        var next = new CorrectionGuard(_dir);
        Assert.Null(next.LastCrash);
        Assert.Empty(next.Suspects);
    }

    [Fact]
    public void DeathWhileTyping_IsReported_AndThatStrategyGoesLast()
    {
        new CorrectionGuard(_dir).Mark("SelectAndType", "input", "Notepad.exe"); // the process "dies" here

        var next = new CorrectionGuard(_dir);
        Assert.Equal(new CorrectionCrash("SelectAndType", "input", "Notepad.exe"), next.LastCrash);
        Assert.True(next.LastCrash!.DuringEdit);
        Assert.Equal(["EditMessage", "SelectAndPaste", "SelectAndType"], next.Order(Strategies, s => s));

        // Reported once; the suspect persists across starts.
        var third = new CorrectionGuard(_dir);
        Assert.Null(third.LastCrash);
        Assert.Equal(["SelectAndType"], third.Suspects);
    }

    [Fact]
    public void DeathWhileSelecting_IsReported_ButBlamesNoStrategy()
    {
        new CorrectionGuard(_dir).Mark("SelectAndType", "select", "WINWORD.exe");

        var next = new CorrectionGuard(_dir);
        Assert.False(next.LastCrash!.DuringEdit);
        Assert.Empty(next.Suspects);
        Assert.Equal(Strategies, next.Order(Strategies, s => s));
    }

    [Fact]
    public void SuspectThatWorksAgain_IsCleared()
    {
        new CorrectionGuard(_dir).Mark("SelectAndPaste", "verify", "chrome.exe");
        var guard = new CorrectionGuard(_dir);
        Assert.Equal(["SelectAndPaste"], guard.Suspects);

        guard.Mark("SelectAndPaste", "input", "chrome.exe");
        guard.Finish("SelectAndPaste");

        Assert.Empty(guard.Suspects);
        Assert.Empty(new CorrectionGuard(_dir).Suspects);
    }

    [Fact]
    public void FailedCorrection_KeepsTheSuspect()
    {
        new CorrectionGuard(_dir).Mark("SelectAndType", "undo", "Notepad.exe");
        var guard = new CorrectionGuard(_dir);
        guard.Mark("SelectAndType", "input", "Notepad.exe");
        guard.Finish(appliedWith: null);

        Assert.Equal(["SelectAndType"], new CorrectionGuard(_dir).Suspects);
    }

    [Fact]
    public void NoDirectory_KeepsStateInMemory()
    {
        var guard = new CorrectionGuard(null);
        guard.Mark("SelectAndType", "input", "Notepad.exe");
        guard.Finish(null);
        Assert.Null(guard.LastCrash);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
