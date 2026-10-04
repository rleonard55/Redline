using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Redline.Windows;
using Redline.Windows.Automation;
using Redline.Windows.Corrections;
using Redline.Windows.Input;

// usage:
//   VisualCheck <hwnd> <class> append <outPng> <text> <line|end> [cleanup]
//   VisualCheck <hwnd> <class> remove-at <offset> <text>     (engine-deletes <text> found exactly at <offset>)
SetProcessDPIAware();
var hwnd = new IntPtr(long.Parse(args[0]));
using var uia = new UiaDispatcher();
var element = await uia.InvokeAsync(() => AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants, Condition.TrueCondition)
    .Cast<AutomationElement>()
    .First(e => e.Current.ClassName.Contains(args[1], StringComparison.Ordinal) &&
                (e.Current.ControlType == ControlType.Edit || e.Current.ControlType == ControlType.Document)));
var info = await uia.InvokeAsync(() => ElementInfo.Capture(element));
using var adapter = new GenericUiaAdapterFactory().Create(uia, element, info);

async Task<bool> Focus()
{
    BringToFront(hwnd);
    await Task.Delay(200);
    await adapter.FocusAsync();
    await Task.Delay(300);
    return await adapter.HasKeyboardFocusAsync();
}

async Task<CorrectionResultLike> EngineDelete(int at, string expected)
{
    var text = await adapter.ReadTextAsync() ?? "";
    if (at < 0 || at + expected.Length > text.Length || text.Substring(at, expected.Length) != expected)
        return new("Rejected: text at offset doesn't match");
    var document = new DocumentState();
    document.Reset(info.SurfaceId);
    var snap = document.Update(info.SurfaceId, text)!.Value.Snapshot;
    var issue = new TextIssue { StartOffset = at, Length = expected.Length, OriginalText = expected, Category = IssueCategory.Other, Message = "cleanup", SnapshotVersion = snap.Version };
    BringToFront(hwnd);
    await Task.Delay(200);
    return new((await new ReplacementEngine(uia, document).ApplyAsync(adapter, issue, "")).ToString());
}

if (args[2] == "remove-at")
{
    Console.WriteLine(await EngineDelete(int.Parse(args[3]), args[4]));
    Console.WriteLine($"first line now: '{FirstLine(await adapter.ReadTextAsync() ?? "")}'");
    return;
}

if (args[2] == "delete-raw")
{
    // delete-raw <text>: select the last occurrence via the adapter and press Delete once; report the tail. No undo.
    var txt = await adapter.ReadTextAsync() ?? "";
    int s0 = txt.LastIndexOf(args[3], StringComparison.Ordinal);
    if (s0 < 0 || !await Focus()) { Console.WriteLine("not found or no focus"); return; }
    if (!await adapter.SelectAsync(new TextRange(s0, args[3].Length), txt)) { Console.WriteLine("could not select"); return; }
    if (!await adapter.HasKeyboardFocusAsync()) { Console.WriteLine("ABORT: focus moved"); return; }
    KeyboardInput.Press(KeyboardInput.VK_DELETE);
    await Task.Delay(600);
    var after = await adapter.ReadTextAsync() ?? "";
    string Tail(string t) => t[Math.Max(0, t.Length - 40)..].Replace(((char)10).ToString(), "<LF>").Replace(((char)0xFFFC).ToString(), "<OBJ>");
    Console.WriteLine($"before tail: {Tail(txt)}");
    Console.WriteLine($"after  tail: {Tail(after)}");
    Console.WriteLine($"expected   : {Tail(txt[..s0] + txt[(s0 + args[3].Length)..])}");
    return;
}

if (args[2] == "measure")
{
    // measure <outPng> <text> [cleanup]: find the last occurrence of <text>, verify geometry, screenshot, optionally delete it.
    var txt = await adapter.ReadTextAsync() ?? "";
    int s0 = txt.LastIndexOf(args[4], StringComparison.Ordinal);
    Console.WriteLine($"'{args[4]}' at {s0}; objects (U+FFFC) before it: {(s0 > 0 ? txt[..s0].Count(c => c == '￼') : 0)}");
    if (s0 < 0) return;
    if (!await Focus()) { Console.WriteLine("ABORT: no focus"); return; }
    await Task.Delay(1500); // let Redline draw for the now-foreground window
    await Measure(txt, s0, args[4], args[3]);
    if (args.Length > 5 && args[5] == "cleanup") Console.WriteLine($"cleanup: {await EngineDelete(s0, args[4])}");
    return;
}

async Task Measure(string now, int start, string appended, string png)
{
    foreach (var word in new[] { "Thsi", "an", "tset" })
    {
        int at = now.IndexOf(word, start, StringComparison.Ordinal);
        var wb = (await adapter.GetBoundsAsync([new TextRange(at, word.Length)], now))[0];
        Console.WriteLine($"  '{word}' at {at}: {(wb.Count == 0 ? "NO verified geometry" : wb[0].ToString())}");
    }
    var bb = (await adapter.GetBoundsAsync([new TextRange(start, appended.Length)], now))[0];
    if (bb.Count == 0) { Console.WriteLine("  no geometry for the whole text"); return; }
    var rect = bb[0];
    int x = (int)rect.Left - 160, y = (int)rect.Top - 40, w = (int)rect.Width + 320, h = (int)rect.Height + 80;
    using var bmp = new Bitmap(w, h);
    using (var g = Graphics.FromImage(bmp))
    {
        var dst = g.GetHdc();
        var screen = GetDC(IntPtr.Zero);
        BitBlt(dst, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000);
        ReleaseDC(IntPtr.Zero, screen);
        g.ReleaseHdc(dst);
    }
    bmp.Save(png, ImageFormat.Png);
    Console.WriteLine($"  captured {w}x{h}");
}

var outPng = args[3];
var append = args[4];
bool atEnd = args[5] == "end";
if (!await Focus()) { Console.WriteLine("ABORT: target doesn't have keyboard focus; nothing typed"); return; }

var before = await adapter.ReadTextAsync() ?? "";
int insertAt = atEnd ? before.Length : FirstLine(before).Length;
Console.WriteLine($"inserting at {insertAt}; objects (U+FFFC) before it: {before[..insertAt].Count(c => c == '￼')}");
await uia.InvokeAsync(() =>
{
    var tp = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
    var r = tp.DocumentRange.Clone();
    if (atEnd)
    {
        r.MoveEndpointByRange(TextPatternRangeEndpoint.Start, r, TextPatternRangeEndpoint.End);
    }
    else
    {
        r.MoveEndpointByRange(TextPatternRangeEndpoint.End, r, TextPatternRangeEndpoint.Start);
        r.Move(TextUnit.Character, insertAt);
        r.MoveEndpointByRange(TextPatternRangeEndpoint.End, r, TextPatternRangeEndpoint.Start);
    }
    r.Select();
});
await Task.Delay(300);
if (!await adapter.HasKeyboardFocusAsync()) { Console.WriteLine("ABORT: focus moved; nothing typed"); return; }
await KeyboardInput.TypeTextAsync(append);
await Task.Delay(2500); // Redline: change detection + debounce + analysis + overlay

var now = await adapter.ReadTextAsync() ?? "";
int start = now.IndexOf(append, insertAt > 2 ? insertAt - 2 : 0, StringComparison.Ordinal);
Console.WriteLine($"appended text found at {start}");
foreach (var word in new[] { "Thsi", "an", "tset" })
{
    int at = now.IndexOf(word, start, StringComparison.Ordinal);
    var wb = (await adapter.GetBoundsAsync([new TextRange(at, word.Length)], now))[0];
    Console.WriteLine($"  '{word}' at {at}: {(wb.Count == 0 ? "NO verified geometry" : wb[0].ToString())}");
}

var b = (await adapter.GetBoundsAsync([new TextRange(start, append.Length)], now))[0];
if (b.Count > 0)
{
    var rect = b[0];
    int x = (int)rect.Left - 160, y = (int)rect.Top - 40, w = (int)rect.Width + 320, h = (int)rect.Height + 80;
    using var bmp = new Bitmap(w, h);
    using (var g = Graphics.FromImage(bmp))
    {
        var dst = g.GetHdc();
        var screen = GetDC(IntPtr.Zero);
        BitBlt(dst, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000);
        ReleaseDC(IntPtr.Zero, screen);
        g.ReleaseHdc(dst);
    }
    bmp.Save(outPng, ImageFormat.Png);
    Console.WriteLine($"captured {w}x{h}");
}

if (args.Length > 6 && args[6] == "cleanup")
    Console.WriteLine($"cleanup: {await EngineDelete(start, append)}");

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

record CorrectionResultLike(string Text)
{
    public override string ToString() => Text;
}
