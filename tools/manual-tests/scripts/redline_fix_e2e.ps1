# Paragraph and selection fixes end to end, in a throwaway WinForms window (build-folder Redline).
#   1. caret on "tset" -> hotkey -> suggestion popup -> F -> fix popup (paragraph) -> Enter: the first
#      paragraph is fixed in one batch, the second is untouched.
#   2. select the second paragraph -> hotkey -> fix popup (selection) -> Enter: only it is fixed.
# Every keystroke is guarded: the form must be in front before the hotkey, and Redline's popup before F/Enter.
# Stops an installed Redline first (--exit) and starts it again at the end.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class M {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint op);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  public static uint ForegroundPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }
  // A script started in the background may not take the foreground; attaching to the foreground thread's input lets it.
  public static bool ForceFront(IntPtr h) {
    uint p; uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out p); uint me = GetCurrentThreadId();
    if (fgThread != me) AttachThreadInput(me, fgThread, true);
    BringWindowToTop(h); SetForegroundWindow(h);
    if (fgThread != me) AttachThreadInput(me, fgThread, false);
    return GetForegroundWindow() == h;
  }
  public static void Tap(byte vk) { keybd_event(vk,0,0,UIntPtr.Zero); keybd_event(vk,0,2,UIntPtr.Zero); }
  // Ctrl+Alt+. (the default suggestion hotkey)
  public static void Hotkey() {
    keybd_event(0x11,0,0,UIntPtr.Zero); keybd_event(0x12,0,0,UIntPtr.Zero);
    keybd_event(0xBE,0,0,UIntPtr.Zero); keybd_event(0xBE,0,2,UIntPtr.Zero);
    keybd_event(0x12,0,2,UIntPtr.Zero); keybd_event(0x11,0,2,UIntPtr.Zero);
  }
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

$first = "This is an tset of the new feature. She go home every day."
$second = "The secnd paragraph has a wrod in it."
$form = New-Object System.Windows.Forms.Form
$form.Text = "Redline fix e2e"; $form.Width = 760; $form.Height = 220
$form.StartPosition = "Manual"; $form.Left = 200; $form.Top = 200
$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true; $tb.Dock = "Fill"; $tb.Font = New-Object System.Drawing.Font("Segoe UI", 14)
$tb.Text = $first + "`r`n" + $second
$form.Controls.Add($tb)
$script:step = 0; $script:results = @(); $script:waits = 0
$shots = Join-Path $env:TEMP "redline_fix"; New-Item -ItemType Directory -Force $shots | Out-Null

function Abort($why) { $script:results += "ABORTED: $why"; $timer.Stop(); $form.Close() }
function Shot($name) {
  $bmp = New-Object System.Drawing.Bitmap 900, 700
  $g = [System.Drawing.Graphics]::FromImage($bmp); $dst = $g.GetHdc(); $src = [M]::GetDC([IntPtr]::Zero)
  [void][M]::BitBlt($dst, 0, 0, 900, 700, $src, 170, 180, 0x40CC0020)
  [void][M]::ReleaseDC([IntPtr]::Zero, $src); $g.ReleaseHdc($dst)
  $bmp.Save((Join-Path $shots "$name.png")); $g.Dispose(); $bmp.Dispose()
}
function FormInFront { return [M]::GetForegroundWindow() -eq $form.Handle }
function PopupInFront { return [M]::ForegroundPid() -eq [uint32]$redline.Id }

$timer = New-Object System.Windows.Forms.Timer; $timer.Interval = 1500
$timer.Add_Tick({
  switch ($script:step) {
    0 { if (-not [M]::ForceFront($form.Handle)) { if (++$script:waits -ge 5) { Abort "could not bring the form to the front" }; return }
        $script:waits = 0; $tb.Focus(); $tb.SelectionStart = $tb.Text.IndexOf("tset") + 1; $tb.SelectionLength = 0; $script:step++ }
    1 { if (++$script:waits -ge 3) { $script:waits = 0; Shot "0_underlines"; $script:step++ } }   # spelling + Harper
    2 { if (-not (FormInFront)) { Abort "form not in front before hotkey"; return }; [M]::Hotkey(); $script:step++ }
    3 { if (-not (PopupInFront)) { Abort "no suggestion popup"; return }; Shot "1_suggestion_popup"; [M]::Tap(0x46); $script:step++ }  # F
    4 { if (-not (PopupInFront)) { Abort "no fix popup"; return }; Shot "2_fix_paragraph"; $script:step++ }
    5 { if (-not (PopupInFront)) { Abort "fix popup closed"; return }; Shot "3_fix_paragraph_later"; [M]::Tap(0x0D); $script:step++ }  # Enter
    6 { if (++$script:waits -ge 3) { $script:waits = 0; $script:step++ } }   # typing + verification
    7 { $lines = $tb.Text -split "`r`n"
        $script:results += "paragraph fix -> " + $lines[0]
        $script:results += "second paragraph untouched: " + ($lines[1] -eq $second)
        $script:results += "form in front after apply: " + (FormInFront)
        $script:paragraphOk = $lines[0].StartsWith("This is a test of") -and $lines[1] -eq $second
        Shot "4_after_paragraph"
        $start = $tb.Text.IndexOf($second); $tb.Select($start, $second.Length); $script:step++ }
    8 { if (++$script:waits -ge 3) { $script:waits = 0; $script:step++ } }   # re-analysis after the edits
    9 { if (-not (FormInFront)) { Abort "form not in front before hotkey"; return }; [M]::Hotkey(); $script:step++ }
    10 { if (-not (PopupInFront)) { Abort "no fix popup for the selection"; return }; Shot "5_fix_selection"; [M]::Tap(0x0D); $script:step++ }
    11 { if (++$script:waits -ge 3) { $script:waits = 0; $script:step++ } }
    12 { $lines = $tb.Text -split "`r`n"
         $script:results += "selection fix -> " + $lines[1]
         $script:results += "first paragraph kept: " + $lines[0]
         $script:selectionOk = $lines[1] -eq "The second paragraph has a word in it."
         Shot "6_after_selection"; $timer.Stop(); $form.Close() }
  }
})
$form.Add_Shown({ $form.Activate(); $tb.Focus(); $timer.Start() })
[void]$form.ShowDialog()
Start-Sleep -Milliseconds 500
Stop-Process -Id $redline.Id
if ($wasRunning) { Start-Process -FilePath $installed }
$script:results
"RESULT: " + $(if ($script:paragraphOk -and $script:selectionOk) { "PASS" } else { "FAIL" })
"screenshots: $shots"
"--- Redline log:"
Get-Content $log | Select-Object -Skip $logStart | Select-String "Fix popup|Combined correction|Correction|Popup choice|Error|Warn" | ForEach-Object { $_.Line }
