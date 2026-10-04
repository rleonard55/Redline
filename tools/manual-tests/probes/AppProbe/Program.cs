using System.Windows.Automation;
using Redline.Analysis;
using Redline.Analysis.Harper;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Redline.Windows;
using Redline.Windows.Automation;

// Runs Redline's real pipeline against a specific window's text surface without changing focus.
using var uia = new UiaDispatcher();
var security = new SecurityFilter();
var selector = new AdapterSelector([new GenericUiaAdapterFactory()]);
ITextAnalyzer[] analyzers = [new SpellAnalyzer(new PersonalDictionary(null)), new HarperAnalyzer()];
await Task.Delay(1500); // let Harper warm up

foreach (var arg in args)
{
    var hwnd = new IntPtr(long.Parse(arg));
    Console.WriteLine($"==================== HWND {hwnd}");

    var candidates = await uia.InvokeAsync(() =>
    {
        var root = AutomationElement.FromHandle(hwnd);
        var cond = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        return root.FindAll(TreeScope.Descendants, cond).Cast<AutomationElement>()
            .Select(e => (Element: e, Info: ElementInfo.Capture(e), Offscreen: e.Current.IsOffscreen))
            .ToList();
    });

    foreach (var c in candidates)
    {
        var info = c.Info;
        var decision = security.Evaluate(info);
        var factory = selector.Select(info);
        Console.WriteLine($"-- {info.ProcessName} {info.ControlType}/{info.ClassName} fw={info.FrameworkId} text={info.SupportsTextPattern} value={info.SupportsValuePattern} ro={info.ValueIsReadOnly} offscreen={c.Offscreen}");
        Console.WriteLine($"   security: {decision.Reason}; adapter: {factory?.Name ?? "(none)"}");
        if (!decision.Allowed || factory is null) continue;

        using var adapter = await uia.InvokeAsync(() => factory.Create(uia, c.Element, info));
        var text = await adapter.ReadTextAsync();
        Console.WriteLine($"   capabilities: {string.Join(",", adapter.Capabilities.SupportedPatterns)}");
        Console.WriteLine($"   text ({text?.Length ?? -1} chars): {(text is not null && text.Contains("tset") ? Show(text) : "<not shown>")}");
        Console.WriteLine($"   caret offset: {await adapter.GetCaretOffsetAsync()}");
        if (string.IsNullOrEmpty(text) || !text.Contains("tset")) continue; // only analyze the test text

        using var pipeline = new AnalysisPipeline(analyzers, new AnalysisPipelineOptions { Debounce = TimeSpan.Zero });
        var done = new TaskCompletionSource<AnalysisResult>();
        pipeline.AnalysisCompleted += (_, r) => done.TrySetResult(r);
        pipeline.Submit(adapter.Context, new TextSnapshot(text, 1, DateTimeOffset.UtcNow));
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Console.WriteLine($"   analysis: {result.Duration.TotalMilliseconds:F1} ms ({string.Join(", ", result.AnalyzerDurations.Select(kv => $"{kv.Key} {kv.Value.TotalMilliseconds:F1}"))})");
        foreach (var issue in result.Issues.Issues)
        {
            var actual = text.Substring(issue.StartOffset, issue.Length);
            var bounds = await adapter.GetBoundsAsync(issue.Range);
            Console.WriteLine($"   [{issue.Analyzer}/{issue.Category}] @{issue.StartOffset} '{issue.OriginalText}'{(actual == issue.OriginalText ? "" : $" MISMATCH '{actual}'")} -> {string.Join(" | ", issue.Suggestions.Select(s => s.Length == 0 ? "(remove)" : s))}");
            Console.WriteLine($"       {issue.Message}");
            Console.WriteLine($"       bounds: {(bounds.Count == 0 ? "(none)" : string.Join(" ", bounds))}");
        }
    }
}

static string Show(string? s) => s is null ? "(null)" : "\"" + s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\v", "\\v") + "\"";
