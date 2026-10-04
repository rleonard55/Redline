Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  public static void SwitchToThisWindow(IntPtr h, bool unused) {
    uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
    AttachThreadInput(me, fg, true);
    try { BringWindowToTop(h); SetForegroundWindow(h); } finally { AttachThreadInput(me, fg, false); }
  }
}
"@
$notepad = [IntPtr]2950336; $word = [IntPtr]12520230
$original = [W]::GetForegroundWindow()
$log = Join-Path $env:LOCALAPPDATA ("Redline\logs\redline-" + (Get-Date -Format yyyyMMdd) + ".log")
if (Test-Path $log) { Remove-Item $log }
$redline = Start-Process -FilePath "$PSScriptRoot\..\..\..\src\Redline.App\bin\Debug\net9.0-windows10.0.19041.0\Redline.exe" -PassThru
Start-Sleep -Seconds 3

function Mark($t) { Add-Content $log ("{0:HH:mm:ss.fff} ---- TEST: {1}" -f (Get-Date), $t) }

Mark "UIA SetFocus on Notepad editor"
$root = [System.Windows.Automation.AutomationElement]::FromHandle($notepad)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, "RichEditD2DPT")
$editor = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
$editor.SetFocus()
Start-Sleep -Seconds 2
Mark ("foreground is Notepad: " + ([W]::GetForegroundWindow() -eq $notepad))

foreach ($i in 1..4) {
  Mark "switch to Word"; [W]::SwitchToThisWindow($word, $true); Start-Sleep -Seconds 2
  Mark ("switch to Notepad (foreground was Word: " + ([W]::GetForegroundWindow() -eq $word) + ")"); [W]::SwitchToThisWindow($notepad, $true); Start-Sleep -Seconds 2
  Mark ("foreground is Notepad: " + ([W]::GetForegroundWindow() -eq $notepad))
}
[W]::SwitchToThisWindow($original, $true); Start-Sleep -Seconds 1
Stop-Process -Id $redline.Id
Get-Content $log | Select-String "TEST|Attached|Detached|Analyzed" | ForEach-Object { $_.Line -replace ' via GenericUia.*','' -replace 'Information SurfaceTracker: ','' -replace 'Debug       AnalysisPipeline: ','' }
