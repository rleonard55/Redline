# AI grammar (GRMR-V3) end to end, in a throwaway WinForms window. Needs the model downloaded and
# Settings > Writing > "Also check grammar with an AI model" on (the script checks both).
#   wait for the model's underline on "go" -> pill -> click "goes" (applied, verified by the engine)
#   then the same for "was" -> "were". Every click is guarded (form in front, pointer over the pill).
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
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
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
# Physical pixels for SetCursorPos/BitBlt, like the overlay (otherwise squiggles can be missing from shots).
[void][M]::SetProcessDPIAware()
$model = Join-Path $env:LOCALAPPDATA "Redline\models\GRMR-V3-G1B-Q4_K_M.gguf"
$settings = Get-Content (Join-Path $env:LOCALAPPDATA "Redline\settings.json") -Raw | ConvertFrom-Json
if (-not (Test-Path $model)) { "SKIPPED: model not downloaded"; return }
if (-not $settings.writing.aiGrammar) { "SKIPPED: AI grammar is off in settings"; return }
$log = Join-Path $env:LOCALAPPDATA ("Redline\logs\redline-" + (Get-Date -Format yyyyMMdd) + ".log")
$logStart = if (Test-Path $log) { (Get-Content $log).Count } else { 0 }
$redline = Start-Process -FilePath "$PSScriptRoot\..\..\..\src\Redline.App\bin\Debug\net9.0-windows10.0.19041.0\Redline.exe" -PassThru
Start-Sleep -Seconds 3

$form = New-Object System.Windows.Forms.Form
$form.Text = "Redline GRMR e2e"; $form.Width = 620; $form.Height = 200
# Not TopMost: the overlay sits just above its target in the normal z-order, so a topmost target hides the underlines.
$form.StartPosition = "Manual"; $form.Left = 200; $form.Top = 200
$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true; $tb.Dock = "Fill"; $tb.Font = New-Object System.Drawing.Font("Segoe UI", 14)
$tb.Text = "She go to school every day. The results was better than expected. I will send the repot tomorrow."
$form.Controls.Add($tb)
$script:step = 0; $script:results = @(); $script:waits = 0
$shots = Join-Path $env:TEMP "redline_grmr"; New-Item -ItemType Directory -Force $shots | Out-Null

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

# The model needs a moment (load ~1 s, ~0.5 s per sentence): retry the hover until the pill appears.
function WaitForPill($word, $label) {
  $p = PillState
  if ($p) { $script:results += "pill over '$word': shown after $($script:waits) retries"; Shot $label; ClickPill "apply"; $script:waits = 0; return $true }
  $script:waits++
  if ($script:waits -gt 8) { Abort "no pill over '$word' (model suggestion never arrived)"; return $true }
  Hover $word 0
  return $false
}

$timer = New-Object System.Windows.Forms.Timer; $timer.Interval = 1500
$timer.Add_Tick({
  switch ($script:step) {
    0 { $tb.SelectionStart = $tb.Text.Length; Shot "0_start"; $script:step++ }
    1 { if (++$script:waits -ge 4) { $script:waits = 0; Shot "0_underlines"; Hover "go" 0; $script:step++ } }  # ~6 s for the model
    2 { if (WaitForPill "go" "1_pill_go") { $script:step++ } }
    3 { $script:results += "after apply: " + $tb.Text
        $script:results += "form still in front: " + ([M]::GetForegroundWindow() -eq $form.Handle); Shot "2_after_go"
        Hover "was" 0; $script:step++ }
    4 { if (WaitForPill "was" "3_pill_was") { $script:step++ } }
    5 { $script:results += "after apply: " + $tb.Text; Shot "4_after_was"; $script:step++ }
    6 { $timer.Stop(); $form.Close() }
  }
})
$form.Add_Shown({ $form.Activate(); $tb.Focus(); $timer.Start() })
[void]$form.ShowDialog()
Start-Sleep -Milliseconds 500
Stop-Process -Id $redline.Id
$script:results
$ok = $tb.Text -eq "She goes to school every day. The results were better than expected. I will send the repot tomorrow."
"RESULT: " + $(if ($ok) { "PASS" } else { "FAIL" })
"screenshots: $shots"
"--- Redline log:"
Get-Content $log | Select-Object -Skip $logStart | Select-String "Grammar model|Correction|Pill|Error|Warn" | ForEach-Object { $_.Line -replace ' via GenericUia.*','' }
