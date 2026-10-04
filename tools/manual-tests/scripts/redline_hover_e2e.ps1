# Hover quick-fix pill, end to end, in a throwaway WinForms window:
#   hover "tset" -> pill -> click suggestion (applied, form keeps focus, pill doesn't pop straight back)
#   hover the repeated "the" -> pill -> click "..." -> popup takes focus -> 'I' ignores.
# Every click/keystroke is guarded (form in front, pointer really over the pill). Screenshots go to %TEMP%.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
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
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint op);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  public static uint ForegroundPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }
  public static void Click() { mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero); }
  public static void Tap(byte vk) { keybd_event(vk,0,0,UIntPtr.Zero); keybd_event(vk,0,2,UIntPtr.Zero); }
  public static IntPtr FindPill(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      var sb = new StringBuilder(64); GetWindowText(h, sb, 64);
      if (p == pid && sb.ToString() == "Redline quick fix") { found = h; return false; }
      return true; }, IntPtr.Zero);
    return found;
  }
  public static bool Over(IntPtr h, int x, int y) { var p = new POINT { X = x, Y = y }; return WindowFromPoint(p) == h; }
}
"@
$log = Join-Path $env:LOCALAPPDATA ("Redline\logs\redline-" + (Get-Date -Format yyyyMMdd) + ".log")
if (Test-Path $log) { Remove-Item $log }
$redline = Start-Process -FilePath "$PSScriptRoot\..\..\..\src\Redline.App\bin\Debug\net9.0-windows10.0.19041.0\Redline.exe" -PassThru
Start-Sleep -Seconds 3

$form = New-Object System.Windows.Forms.Form
$form.Text = "Redline hover e2e"; $form.Width = 620; $form.Height = 200; $form.TopMost = $true
$form.StartPosition = "Manual"; $form.Left = 200; $form.Top = 200
$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true; $tb.Dock = "Fill"; $tb.Font = New-Object System.Drawing.Font("Segoe UI", 14)
$tb.Text = "This is an tset. I saw the the cat."
$form.Controls.Add($tb)
$script:step = 0; $script:results = @(); $script:pillRect = $null
$shots = Join-Path $env:TEMP "redline_hover"; New-Item -ItemType Directory -Force $shots | Out-Null

function Abort($why) { $script:results += "ABORTED: $why"; $timer.Stop(); $form.Close() }
function Shot($name) {
  # BitBlt with CAPTUREBLT: layered windows (overlay, pill) are missing from a plain screen copy.
  $bmp = New-Object System.Drawing.Bitmap 700, 300
  $g = [System.Drawing.Graphics]::FromImage($bmp); $dst = $g.GetHdc(); $src = [M]::GetDC([IntPtr]::Zero)
  [void][M]::BitBlt($dst, 0, 0, 700, 300, $src, 170, 190, 0x40CC0020)
  [void][M]::ReleaseDC([IntPtr]::Zero, $src); $g.ReleaseHdc($dst)
  $bmp.Save((Join-Path $shots "$name.png")); $g.Dispose(); $bmp.Dispose()
}
# Screen point at the middle of the nth occurrence of $word, a little above the baseline.
function WordPoint($word, $nth) {
  $i = -1; for ($k = 0; $k -le $nth; $k++) { $i = $tb.Text.IndexOf($word, $i + 1) }
  $a = $tb.GetPositionFromCharIndex($i); $b = $tb.GetPositionFromCharIndex($i + $word.Length - 1)
  $p = $tb.PointToScreen((New-Object System.Drawing.Point ([int](($a.X + $b.X) / 2 + 4)), ($a.Y + 14)))
  return $p
}
function Hover($word, $nth) {
  if ([M]::GetForegroundWindow() -ne $form.Handle) { Abort "form not in front before hover"; return }
  $p = WordPoint $word $nth
  [void][M]::SetCursorPos($p.X - 60, $p.Y + 50)   # move first: the pill only arms after the pointer moves
  Start-Sleep -Milliseconds 120
  [void][M]::SetCursorPos($p.X, $p.Y)
}
function PillState() {
  $h = [M]::FindPill([uint32]$redline.Id)
  if ($h -eq [IntPtr]::Zero -or -not [M]::IsWindowVisible($h)) { return $null }
  $r = New-Object M+RECT; [void][M]::GetWindowRect($h, [ref]$r)
  return @{ Handle = $h; Rect = $r }
}
# Clicks inside the visible pill: 'apply' = left part, 'more' = right end. Guarded by WindowFromPoint.
function ClickPill($part) {
  $pill = PillState
  if ($null -eq $pill) { Abort "no pill to click"; return }
  $r = $pill.Rect; $y = [int](($r.Top + $r.Bottom) / 2) - 2
  $x = if ($part -eq "apply") { $r.Left + 28 } else { $r.Right - 22 }
  # Glide inside the keep-open zone so the pill isn't dismissed on the way.
  [void][M]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 80
  if ([M]::GetForegroundWindow() -ne $form.Handle) { Abort "form not in front before click"; return }
  if (-not [M]::Over($pill.Handle, $x, $y)) { Abort "pointer not over the pill"; return }
  [M]::Click()
}

$timer = New-Object System.Windows.Forms.Timer; $timer.Interval = 1500
$timer.Add_Tick({
  $script:step++
  switch ($script:step) {
    1 { $tb.SelectionStart = $tb.Text.Length; Hover "tset" 0 }
    2 { $p = PillState; $script:results += "pill over tset: " + ($(if ($p) { "shown" } else { "MISSING" })); Shot "1_pill_tset"
        if ($p) { ClickPill "apply" } else { Abort "pill never appeared" } }
    3 { $script:results += "after pill apply: " + $tb.Text
        $script:results += "form still in front: " + ([M]::GetForegroundWindow() -eq $form.Handle)
        $script:results += "pill re-shown without moving: " + ($null -ne (PillState)); Shot "2_after_apply" }
    4 { Hover "the" 1 }
    5 { $p = PillState; $script:results += "pill over 2nd 'the': " + ($(if ($p) { "shown" } else { "MISSING" })); Shot "3_pill_the"
        if ($p) { ClickPill "more" } else { Abort "pill never appeared" } }
    6 { $popupFront = [M]::ForegroundPid() -eq $redline.Id
        $script:results += "popup in front after '...': $popupFront"; Shot "4_popup"
        if ($popupFront) { [M]::Tap(0x49) } else { Abort "popup not in front" } }   # 'I' -> ignore
    7 { $script:results += "after ignore: " + $tb.Text
        $script:results += "form back in front: " + ([M]::GetForegroundWindow() -eq $form.Handle); Shot "5_after_ignore" }
    8 { $timer.Stop(); $form.Close() }
  }
})
$form.Add_Shown({ $form.Activate(); $tb.Focus(); $timer.Start() })
[void]$form.ShowDialog()
Start-Sleep -Milliseconds 500
Stop-Process -Id $redline.Id
$script:results
"screenshots: $shots"
"--- Redline log:"
Get-Content $log | Select-String "Correction|Pill|Popup|Error|Warn" | ForEach-Object { $_.Line -replace ' via GenericUia.*','' }
