using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
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

// usage: WebCheck <hwnd> <outDir> <prefix> <findBy:id|class> <key1> [key2 ...]
// For each field: classify, focus, let the running Redline draw, screenshot, analyze, verify geometry,
// apply the first fix through the real engine, and read back.
SetProcessDPIAware();
var hwnd = new IntPtr(long.Parse(args[0]));
var outDir = args[1];
var prefix = args[2];
var findBy = args[3];
using var uia = new UiaDispatcher();
var security = new SecurityFilter();
var selector = new AdapterSelector([new GenericUiaAdapterFactory()]);
ITextAnalyzer[] analyzers = [new SpellAnalyzer(new PersonalDictionary(null)), new HarperAnalyzer()];
await Task.Delay(1500); // Harper warm-up

foreach (var key in args.Skip(4))
{
    Console.WriteLine($"=== {key}");
    var element = await uia.InvokeAsync(() => AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants, Condition.TrueCondition)
        .Cast<AutomationElement>()
        .FirstOrDefault(e => findBy == "id" ? e.Current.AutomationId == key : e.Current.ClassName.Contains(key, StringComparison.Ordinal)));
    if (element is null) { Console.WriteLine("  not found"); continue; }

    var info = await uia.InvokeAsync(() => ElementInfo.Capture(element));
    var decision = security.Evaluate(info);
    var factory = selector.Select(info);
    Console.WriteLine($"  {info.ControlType}/{Short(info.ClassName)} fw={info.FrameworkId} text={info.SupportsTextPattern} value={info.SupportsValuePattern} focusable={info.IsKeyboardFocusable}");
    Console.WriteLine($"  security: {decision.Reason}{(decision.Sensitive ? " (sensitive)" : "")}; adapter: {factory?.Name ?? "(none)"}");
    if (!decision.Allowed || factory is null) continue;

    using var adapter = await uia.InvokeAsync(() => factory.Create(uia, element, info));
    BringToFront(hwnd);
    await Task.Delay(200);
    await adapter.FocusAsync();
    await Task.Delay(2200); // running Redline: attach, read, analyze, draw

    var text = await adapter.ReadTextAsync() ?? "";
    Console.WriteLine($"  text: \"{Esc(text)}\"  caret: {await adapter.GetCaretOffsetAsync()}  focused: {await adapter.HasKeyboardFocusAsync()}");
    Screenshot(await adapter.GetSurfaceBoundsAsync(), Path.Combine(outDir, $"{prefix}_{key}.png"));
    if (text.Length == 0) continue;

    var document = new DocumentState();
    document.Reset(info.SurfaceId);
    var snapshot = document.Update(info.SurfaceId, text)!.Value.Snapshot;
    using var pipeline = new AnalysisPipeline(analyzers, new AnalysisPipelineOptions { Debounce = TimeSpan.Zero });
    var done = new TaskCompletionSource<AnalysisResult>();
    pipeline.AnalysisCompleted += (_, r) => done.TrySetResult(r);
    pipeline.Submit(adapter.Context, snapshot);
    var issues = (await done.Task.WaitAsync(TimeSpan.FromSeconds(10))).Issues.Issues;
    var bounds = await adapter.GetBoundsAsync(issues.Select(i => i.Range).ToList(), text);
    for (int i = 0; i < issues.Count; i++)
        Console.WriteLine($"  issue '{issues[i].OriginalText}' -> {string.Join("|", issues[i].Suggestions.Take(3).Select(s => s.Length == 0 ? "(remove)" : s))}: {(bounds[i].Count > 0 ? "geometry OK" : "NO verified geometry")}");

    var fixable = issues.FirstOrDefault(i => i.Suggestions.Count > 0);
    if (fixable is null) continue;
    BringToFront(hwnd);
    await Task.Delay(200);
    var result = await new ReplacementEngine(uia, document).ApplyAsync(adapter, fixable, fixable.Suggestions[0]);
    Console.WriteLine($"  fix '{fixable.OriginalText}' -> '{fixable.Suggestions[0]}': {result}");
    Console.WriteLine($"  now: \"{Esc(await adapter.ReadTextAsync() ?? "")}\"");
}

static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;
static string Esc(string s) => s.Replace(((char)13).ToString(), "<CR>").Replace(((char)10).ToString(), "<LF>").Replace(((char)0xFFFC).ToString(), "<OBJ>");

static void Screenshot(TextBounds? surface, string path)
{
    if (surface is not { } r) { Console.WriteLine("  (no surface bounds)"); return; }
    int x = (int)r.Left - 10, y = (int)r.Top - 10, w = Math.Min(900, (int)r.Width + 20), h = Math.Min(400, (int)r.Height + 20);
    using var bmp = new Bitmap(w, h);
    using (var g = Graphics.FromImage(bmp))
    {
        var dst = g.GetHdc();
        var screen = GetDC(IntPtr.Zero);
        BitBlt(dst, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000);
        ReleaseDC(IntPtr.Zero, screen);
        g.ReleaseHdc(dst);
    }
    bmp.Save(path, ImageFormat.Png);
}

static void BringToFront(IntPtr h)
{
    uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
    AttachThreadInput(me, fg, true);
    try { BringWindowToTop(h); SetForegroundWindow(h); } finally { AttachThreadInput(me, fg, false); }
}
[DllImport("user32.dll")] static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
[DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
[DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
[DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
[DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
