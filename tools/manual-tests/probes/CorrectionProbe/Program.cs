using System.Runtime.InteropServices;
using System.Windows.Automation;
using Redline.Analysis;
using Redline.Analysis.Harper;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Redline.Windows;
using Redline.Windows.Automation;
using Redline.Windows.Corrections;

// usage: CorrectionProbe <hwnd> <classNameContains> [strategy]
// Repeatedly: read -> analyze (real analyzers) -> apply first suggestion of first issue via the real engine.
var hwnd = new IntPtr(long.Parse(args[0]));
var classFilter = args[1];
var strategies = args.Length > 2 ? new[] { Enum.Parse<ReplacementStrategy>(args[2]) } : null;

using var uia = new UiaDispatcher();
var document = new DocumentState();
ITextAnalyzer[] analyzers = [new SpellAnalyzer(new PersonalDictionary(null)), new HarperAnalyzer()];
await Task.Delay(1500); // Harper warm-up
var engine = new ReplacementEngine(uia, document, new ReplacementOptions { Strategies = strategies });

var element = await uia.InvokeAsync(() =>
    AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
        .FirstOrDefault(e => e.Current.ClassName.Contains(classFilter, StringComparison.Ordinal) &&
                             (e.Current.ControlType == ControlType.Edit || e.Current.ControlType == ControlType.Document)));
if (element is null) { Console.WriteLine("element not found"); return; }

var info = await uia.InvokeAsync(() => ElementInfo.Capture(element));
using var adapter = await uia.InvokeAsync(() => new GenericUiaAdapterFactory().Create(uia, element, info));
Console.WriteLine($"target: {info.ProcessName} {info.ControlType}/{info.ClassName[..Math.Min(40, info.ClassName.Length)]}");
document.Reset(info.SurfaceId);

for (int round = 1; round <= 4; round++)
{
    var text = await adapter.ReadTextAsync() ?? "";
    var snapshot = document.Update(info.SurfaceId, text)?.Snapshot ?? document.Current!;
    Console.WriteLine($"round {round}: text = \"{FirstLine(text)}\"");

    using var pipeline = new AnalysisPipeline(analyzers, new AnalysisPipelineOptions { Debounce = TimeSpan.Zero });
    var done = new TaskCompletionSource<AnalysisResult>();
    pipeline.AnalysisCompleted += (_, r) => done.TrySetResult(r);
    pipeline.Submit(adapter.Context, snapshot);
    var issues = (await done.Task.WaitAsync(TimeSpan.FromSeconds(10))).Issues.Issues
        .Where(i => i.Suggestions.Count > 0 && i.StartOffset < FirstLine(text).Length).ToList();
    if (issues.Count == 0) { Console.WriteLine("  no issues with suggestions left"); break; }

    // Redline itself is foreground when applying (popup/hotkey grants that); a background probe isn't,
    // so bring the target forward first the way the real flow can.
    BringToFront(hwnd);
    await Task.Delay(200);

    var issue = issues[0];
    var replacement = issue.Suggestions[0];
    var result = await engine.ApplyAsync(adapter, issue, replacement);
    Console.WriteLine($"  '{issue.OriginalText}' -> '{replacement}': {result}");
    if (!result.Succeeded) break;
    await Task.Delay(300);
}

Console.WriteLine($"final: \"{FirstLine(await adapter.ReadTextAsync() ?? "")}\"");

static string FirstLine(string s)
{
    int i = s.IndexOfAny(['\r', '\n', '\v']);
    return i < 0 ? s : s[..i];
}

static void BringToFront(IntPtr h)
{
    uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
    AttachThreadInput(me, fg, true);
    try { BringWindowToTop(h); SetForegroundWindow(h); } finally { AttachThreadInput(me, fg, false); }
}
[DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
[DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
