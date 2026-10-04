Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class K {
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static uint ForegroundPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }
  static void Down(byte vk) { keybd_event(vk,0,0,UIntPtr.Zero); }
  static void Up(byte vk) { keybd_event(vk,0,2,UIntPtr.Zero); }
  public static void Hotkey() { Down(0x11); Down(0x12); Down(0xBE); Up(0xBE); Up(0x12); Up(0x11); }   // Ctrl+Alt+.
  public static void Tap(byte vk) { Down(vk); Up(vk); }
}
"@
$log = Join-Path $env:LOCALAPPDATA ("Redline\logs\redline-" + (Get-Date -Format yyyyMMdd) + ".log")
if (Test-Path $log) { Remove-Item $log }
$redline = Start-Process -FilePath "$PSScriptRoot\..\..\..\src\Redline.App\bin\Debug\net9.0-windows10.0.19041.0\Redline.exe" -PassThru
Start-Sleep -Seconds 3

$form = New-Object System.Windows.Forms.Form
$form.Text = "Redline popup e2e"; $form.Width = 520; $form.Height = 180; $form.TopMost = $false
$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true; $tb.Dock = "Fill"; $tb.Font = New-Object System.Drawing.Font("Segoe UI", 14)
$tb.Text = "This is an tset. I saw the the cat."
$form.Controls.Add($tb)
$script:step = 0; $script:results = @(); $script:aborted = $false
function Caret($word, $nth) { $i = -1; for ($k = 0; $k -le $nth; $k++) { $i = $tb.Text.IndexOf($word, $i + 1) }; $tb.SelectionStart = $i + 1; $tb.SelectionLength = 0 }
# Guards: only send keys when the expected window is in front; otherwise stop the test.
function HotkeyIfFormFront { if ([K]::GetForegroundWindow() -eq $form.Handle) { [K]::Hotkey() } else { Abort "form not in front before hotkey" } }
function KeyIfPopupFront($vk) { if ([K]::ForegroundPid() -eq $redline.Id) { [K]::Tap($vk) } else { Abort ("popup not in front (fg pid " + [K]::ForegroundPid() + ")") } }
function Abort($why) { $script:results += "ABORTED: $why"; $script:aborted = $true; $timer.Stop(); $form.Close() }
$timer = New-Object System.Windows.Forms.Timer; $timer.Interval = 1800
$timer.Add_Tick({
  $script:step++
  switch ($script:step) {
    1 { Caret "tset" 0; HotkeyIfFormFront }
    2 { KeyIfPopupFront 0x31 }                                   # '1' -> first suggestion
    3 { $script:results += "after fix 1: " + $tb.Text; Caret "the" 1; HotkeyIfFormFront }
    4 { KeyIfPopupFront 0x31 }                                   # '1' -> (remove)
    5 { $script:results += "after fix 2: " + $tb.Text; Caret "an" 0; HotkeyIfFormFront }
    6 { KeyIfPopupFront 0x49 }                                   # 'I' -> ignore
    7 { $script:results += "after ignore: " + $tb.Text }
    8 { $timer.Stop(); $form.Close() }
  }
})
$form.Add_Shown({ $form.Activate(); $tb.Focus(); $timer.Start() })
[void]$form.ShowDialog()
Start-Sleep -Milliseconds 500
Stop-Process -Id $redline.Id
$script:results
"--- Redline log:"
Get-Content $log | Select-String "Correction|Attached|hotkey|Hotkey|Error|Warn" | ForEach-Object { $_.Line -replace ' via GenericUia.*','' }
