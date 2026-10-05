# Paragraph gutter pills end to end, in a throwaway WinForms window (build-folder Redline).
#   Paragraphs 1 and 3 have several fixes (pill), paragraph 2 has one (no pill).
#   Click the first pill -> fix popup -> Enter: only paragraph 1 changes and its pill goes away.
# Clicks are guarded (form in front, pointer over the pill); Enter only while Redline's popup is in front.
# Stops an installed Redline first (--exit) and starts it again at the end.
param([int]$Indent = 0)  # left padding around the text box (0 = text right at the window edge)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class M {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint op);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  public static uint ForegroundPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }
  public static void Click() { mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero); }
  public static void Tap(byte vk) { keybd_event(vk,0,0,UIntPtr.Zero); keybd_event(vk,0,2,UIntPtr.Zero); }
  public static bool ForceFront(IntPtr h) {
    uint p; uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out p); uint me = GetCurrentThreadId();
    if (fgThread != me) AttachThreadInput(me, fgThread, true);
    BringWindowToTop(h); SetForegroundWindow(h);
    if (fgThread != me) AttachThreadInput(me, fgThread, false);
    return GetForegroundWindow() == h;
  }
  // Visible gutter pills of the given process, top to bottom.
  public static List<IntPtr> Pills(uint pid) {
    var found = new List<IntPtr>();
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      var sb = new StringBuilder(64); GetWindowText(h, sb, 64);
      if (p == pid && sb.ToString() == "Redline paragraph fix" && IsWindowVisible(h)) found.Add(h);
      return true; }, IntPtr.Zero);
    found.Sort((a, b) => { RECT ra, rb; GetWindowRect(a, out ra); GetWindowRect(b, out rb); return ra.Top.CompareTo(rb.Top); });
    return found;
  }
  public static bool Over(IntPtr h, int x, int y) { var p = new POINT { X = x, Y = y }; return WindowFromPoint(p) == h; }
}
"@
[void][M]::SetProcessDPIAware()

$installed = Join-Path $env:LOCALAPPDATA "Programs\Redline\Redline.exe"
$wasRunning = $null -ne (Get-Process Redline -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installed })
if ($wasRunning) { & $installed --exit | Out-Null; Start-Sleep -Seconds 2 }
if (Get-Process Redline -ErrorAction SilentlyContinue) { "ABORTED: another Redline is still running"; return }

$log = Join-Path $env:LOCALAPPDATA ("Redline\logs\redline-" + (Get-Date -Format yyyyMMdd) + ".log")
$logStart = if (Test-Path $log) { (Get-Content $log).Count } else { 0 }
$redline = Start-Process -FilePath "$PSScriptRoot\..\..\..\src\Redline.App\bin\Debug\net9.0-windows10.0.19041.0\Redline.exe" -PassThru
Start-Sleep -Seconds 3

$p1 = "This is an tset of the new feature. She go home every day."
$p2 = "Only one wrod is wrong here."
$p3 = "The secnd paragraph has a typo and anothr one."
$form = New-Object System.Windows.Forms.Form
$form.Text = "Redline gutter e2e"; $form.Width = 760; $form.Height = 260
$form.StartPosition = "Manual"; $form.Left = 200; $form.Top = 200
$form.Padding = New-Object System.Windows.Forms.Padding($Indent, 0, 0, 0)
$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true; $tb.Dock = "Fill"; $tb.Font = New-Object System.Drawing.Font("Segoe UI", 14)
$tb.Text = $p1 + "`r`n" + $p2 + "`r`n" + $p3
$form.Controls.Add($tb)
$script:step = 0; $script:results = @(); $script:waits = 0; $script:ok = $false
$shots = Join-Path $env:TEMP "redline_gutter"; New-Item -ItemType Directory -Force $shots | Out-Null

function Abort($why) { $script:results += "ABORTED: $why"; $timer.Stop(); $form.Close() }
function Shot($name) {
  $bmp = New-Object System.Drawing.Bitmap 900, 700
  $g = [System.Drawing.Graphics]::FromImage($bmp); $dst = $g.GetHdc(); $src = [M]::GetDC([IntPtr]::Zero)
  [void][M]::BitBlt($dst, 0, 0, 900, 700, $src, 170, 180, 0x40CC0020)
  [void][M]::ReleaseDC([IntPtr]::Zero, $src); $g.ReleaseHdc($dst)
  $bmp.Save((Join-Path $shots "$name.png")); $g.Dispose(); $bmp.Dispose()
}
function Pills { return ,[M]::Pills([uint32]$redline.Id) }
function Describe($pills) {
  ($pills | ForEach-Object { $r = New-Object M+RECT; [void][M]::GetWindowRect($_, [ref]$r); "($($r.Left),$($r.Top) $($r.Right - $r.Left)x$($r.Bottom - $r.Top))" }) -join " "
}

$timer = New-Object System.Windows.Forms.Timer; $timer.Interval = 1500
$timer.Add_Tick({
  switch ($script:step) {
    0 { if (-not [M]::ForceFront($form.Handle)) { if (++$script:waits -ge 5) { Abort "could not bring the form to the front" }; return }
        $script:waits = 0; $tb.Focus(); $tb.SelectionStart = $tb.Text.Length; $script:step++ }
    1 { if (++$script:waits -ge 3) { $script:waits = 0; $script:step++ } }   # analysis + layout
    2 { $pills = Pills; Shot "0_pills"
        $script:results += "pills before: $($pills.Count) " + (Describe $pills)
        if ($pills.Count -ne 2) { Abort "expected 2 pills (paragraphs 1 and 3), got $($pills.Count)"; return }
        $r = New-Object M+RECT; [void][M]::GetWindowRect($pills[0], [ref]$r)
        $x = [int](($r.Left + $r.Right) / 2); $y = [int](($r.Top + $r.Bottom) / 2)
        [void][M]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 300
        Shot "1_pill_hover"
        if ([M]::GetForegroundWindow() -ne $form.Handle) { Abort "form not in front before click"; return }
        if (-not [M]::Over($pills[0], $x, $y)) { Abort "pointer not over the pill"; return }
        [M]::Click(); $script:step++ }
    3 { if ([M]::ForegroundPid() -ne [uint32]$redline.Id) { Abort "no fix popup after the click"; return }
        Shot "2_fix_popup"; [M]::Tap(0x0D); $script:step++ }   # Enter
    4 { if (++$script:waits -ge 3) { $script:waits = 0; $script:step++ } }  # typing, verification, re-analysis
    5 { $lines = $tb.Text -split "`r`n"; $pills = Pills; Shot "3_after"
        $script:results += "paragraph 1 -> " + $lines[0]
        $script:results += "paragraphs 2 and 3 untouched: " + ($lines[1] -eq $p2 -and $lines[2] -eq $p3)
        $script:results += "form in front after apply: " + ([M]::GetForegroundWindow() -eq $form.Handle)
        $script:results += "pills after: $($pills.Count) " + (Describe $pills)
        $script:ok = $lines[0].StartsWith("This is a test of") -and $lines[1] -eq $p2 -and $lines[2] -eq $p3 -and $pills.Count -eq 1
        $timer.Stop(); $form.Close() }
  }
})
$form.Add_Shown({ $form.Activate(); $tb.Focus(); $timer.Start() })
[void]$form.ShowDialog()
Start-Sleep -Milliseconds 500
Stop-Process -Id $redline.Id
if ($wasRunning) { Start-Process -FilePath $installed }
$script:results
"RESULT: " + $(if ($script:ok) { "PASS" } else { "FAIL" })
"screenshots: $shots"
"--- Redline log:"
Get-Content $log | Select-Object -Skip $logStart | Select-String "Fix popup|Combined correction|Error|Warn" | ForEach-Object { $_.Line }
