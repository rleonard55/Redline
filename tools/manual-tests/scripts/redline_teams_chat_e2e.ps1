Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @"
using System; using System.Runtime.InteropServices;
public static class K {
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
  public static uint ForegroundPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }
  public static IntPtr ForegroundRoot() { return GetAncestor(GetForegroundWindow(), 2); }
  public static void Front(IntPtr h) { uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId(); AttachThreadInput(me, fg, true); BringWindowToTop(h); SetForegroundWindow(h); AttachThreadInput(me, fg, false); }
  static void Down(byte vk) { keybd_event(vk,0,0,UIntPtr.Zero); }
  static void Up(byte vk) { keybd_event(vk,0,2,UIntPtr.Zero); }
  public static void Hotkey() { Down(0x11); Down(0x12); Down(0xBE); Up(0xBE); Up(0x12); Up(0x11); }
  public static void Tap(byte vk) { Down(vk); Up(vk); }
  public static void Shot(string path, int x, int y, int w, int h) {
    using (var bmp = new System.Drawing.Bitmap(w, h)) { using (var g = System.Drawing.Graphics.FromImage(bmp)) { IntPtr dst = g.GetHdc(); IntPtr s = GetDC(IntPtr.Zero); BitBlt(dst, 0, 0, w, h, s, x, y, 0x00CC0020 | 0x40000000); ReleaseDC(IntPtr.Zero, s); g.ReleaseHdc(dst); } bmp.Save(path); }
  }
}
"@
[void][K]::SetProcessDPIAware()
$teams = [IntPtr]2033880
$log = Join-Path $env:LOCALAPPDATA ("Redline\logs\redline-" + (Get-Date -Format yyyyMMdd) + ".log")
if (Test-Path $log) { Remove-Item $log }
$redline = Start-Process -FilePath "$PSScriptRoot\..\..\..\src\Redline.App\bin\Debug\net9.0-windows10.0.19041.0\Redline.exe" -PassThru
Start-Sleep -Seconds 3

$box = [System.Windows.Automation.AutomationElement]::FromHandle($teams).FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ClassName -like '*ck-editor__editable*' } | Select-Object -First 1
$tp = $box.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
function Text { $tp.DocumentRange.GetText(-1) }
function CaretAt($offset) {
  $r = $tp.DocumentRange.Clone()
  $r.MoveEndpointByRange([System.Windows.Automation.Text.TextPatternRangeEndpoint]::End, $r, [System.Windows.Automation.Text.TextPatternRangeEndpoint]::Start)
  [void]$r.Move([System.Windows.Automation.Text.TextUnit]::Character, $offset)
  $r.MoveEndpointByRange([System.Windows.Automation.Text.TextPatternRangeEndpoint]::End, $r, [System.Windows.Automation.Text.TextPatternRangeEndpoint]::Start)
  $r.Select()
}
function Fix($word, $label) {
  $t = Text; $at = $t.IndexOf($word)
  if ($at -lt 0) { "  '$word' not found"; return $false }
  [K]::Front($teams); Start-Sleep -Milliseconds 300
  $box.SetFocus(); Start-Sleep -Milliseconds 300
  CaretAt ($at + 1); Start-Sleep -Milliseconds 1500
  if ([K]::ForegroundRoot() -ne $teams) { "  ABORT: Teams not in front before hotkey"; return $false }
  [K]::Hotkey(); Start-Sleep -Milliseconds 1200
  if ([K]::ForegroundPid() -ne $redline.Id) { "  ABORT: popup not in front (fg pid " + [K]::ForegroundPid() + ")"; return $false }
  [K]::Tap(0x31); Start-Sleep -Milliseconds 2000      # '1' = first suggestion; never Enter
  "  after fixing '$word': """ + (Text) + """"
  return $true
}

[K]::Front($teams); Start-Sleep -Milliseconds 300; $box.SetFocus(); Start-Sleep -Seconds 2
$b = $box.Current.BoundingRectangle
[K]::Shot("$env:TEMP/teams_chat_before.png", [int]$b.Left - 10, [int]$b.Top - 10, [Math]::Min(700, [int]$b.Width + 20), [int]$b.Height + 20)
"before: """ + (Text) + """"
if (Fix "tset" "spelling") { [void](Fix "an" "grammar") }
[K]::Shot("$env:TEMP/teams_chat_after.png", [int]$b.Left - 10, [int]$b.Top - 10, [Math]::Min(700, [int]$b.Width + 20), [int]$b.Height + 20)
Stop-Process -Id $redline.Id
"--- Redline log"
Get-Content $log | Select-String "Overlay|Popup choice|Correction" | ForEach-Object { $_.Line -replace ' (Information|Debug) +',' ' }
