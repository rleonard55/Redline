using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Redline.Windows.Automation;

// Logs every UIA focus event while switching Notepad -> Teams, and how each focused element
// relates to Teams' CKEditor box. Prints structure only (no names/values beyond text length).
var teams = new IntPtr(2033880);
var notepad = new IntPtr(2950336);
using var uia = new UiaDispatcher();
var sw = Stopwatch.StartNew();
var events = new List<(double Ms, AutomationElement El)>();

var editor = await uia.InvokeAsync(() =>
    AutomationElement.FromHandle(teams).FindAll(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
    .Cast<AutomationElement>().First(e => e.Current.ClassName.Contains("ck-editor__editable")));
var editorId = await uia.InvokeAsync(() => string.Join(".", editor.GetRuntimeId()));
Console.WriteLine($"CKEditor runtime id: {editorId}");

AutomationFocusChangedEventHandler handler = (s, _) => { lock (events) events.Add((sw.Elapsed.TotalMilliseconds, (AutomationElement)s)); };
await uia.InvokeAsync(() => Automation.AddAutomationFocusChangedEventHandler(handler));

Switch(notepad); await Task.Delay(1500);
lock (events) events.Clear();
sw.Restart();
Switch(teams);
await Task.Delay(60);
var mode = args.FirstOrDefault() ?? "none";
if (mode is "read" or "both")
{
    var len = await uia.InvokeAsync(() => ((TextPattern)editor.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(200_000).Length);
    lock (events) events.Add((sw.Elapsed.TotalMilliseconds, null!)); // marker: read done
}
AutomationEventHandler textHandler = (_, _) => { };
if (mode is "subscribe" or "both")
{
    await uia.InvokeAsync(() => Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, editor, TreeScope.Element, textHandler));
    lock (events) events.Add((sw.Elapsed.TotalMilliseconds, null!));
}
await Task.Delay(1500);
if (mode is "subscribe" or "both")
    await uia.InvokeAsync(() => Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, editor, textHandler));
var focusedNow = await uia.InvokeAsync(() => AutomationElement.FocusedElement);
await uia.InvokeAsync(() => Automation.RemoveAutomationFocusChangedEventHandler(handler));

List<(double, AutomationElement)> snapshot;
lock (events) snapshot = events.ToList();
foreach (var (ms, el) in snapshot.Append((-1, focusedNow)))
{
    if (el is null) { Console.WriteLine($"+{ms,6:F0} ms -- Redline-style action done ({mode})"); continue; }
    var line = await uia.InvokeAsync(() => Describe(el));
    Console.WriteLine(ms < 0 ? $"FocusedElement now: {line}" : $"+{ms,6:F0} ms focus event: {line}");
}

string Describe(AutomationElement el) => DescribeCore(el) + Chain(el);

string Chain(AutomationElement el)
{
    if (string.Join(".", el.GetRuntimeId()) == editorId) return "";
    var parts = new List<string>();
    var p = TreeWalker.RawViewWalker.GetParent(el);
    for (int i = 0; p is not null && i < 8; i++, p = TreeWalker.RawViewWalker.GetParent(p))
        parts.Add($"{p.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")}/{Trunc(p.Current.ClassName)}");
    return Environment.NewLine + "        ancestors: " + string.Join(" < ", parts);
}

string DescribeCore(AutomationElement el)
{
    try
    {
        var c = el.Current;
        var id = string.Join(".", el.GetRuntimeId());
        string text = el.TryGetCurrentPattern(TextPattern.Pattern, out var tp) ? $"textLen={((TextPattern)tp).DocumentRange.GetText(-1).Length}" : "no-text";
        var walker = TreeWalker.RawViewWalker;
        string relation = id == editorId ? "IS CKEditor" : "unrelated";
        var p = walker.GetParent(el); int depth = 1;
        while (p is not null && depth < 40 && relation == "unrelated")
        {
            if (string.Join(".", p.GetRuntimeId() ?? []) == editorId) relation = $"DESCENDANT of CKEditor (depth {depth})";
            p = walker.GetParent(p); depth++;
        }
        var parent = walker.GetParent(el);
        var parentDesc = parent is null ? "-" : $"{parent.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")}/{Trunc(parent.Current.ClassName)}";
        return $"{c.ControlType.ProgrammaticName.Replace("ControlType.", "")}/{Trunc(c.ClassName)} pid={c.ProcessId} fw={c.FrameworkId} autoId='{c.AutomationId}' hwnd={c.NativeWindowHandle} id={id} {text} focusable={c.IsKeyboardFocusable} parent={parentDesc} -> {relation}";
    }
    catch (Exception ex) { return $"<{ex.GetType().Name}>"; }
}

static string Trunc(string s) => s.Contains("ck-editor") ? "CKEditor" : (s.Length > 40 ? s[..40] + "…" : s);

static void Switch(IntPtr h)
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
