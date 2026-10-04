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

$ide = [IntPtr]5179018
foreach ($i in 1..3) {
  foreach ($t in @(@('Notepad',$notepad), @('Antigravity',$ide))) {
    Mark ("switch to " + $t[0]); [W]::SwitchToThisWindow($t[1], $true); Start-Sleep -Seconds 2
    if ([W]::GetForegroundWindow() -ne $t[1]) { Mark "  !! foreground switch failed" }
  }
}
[W]::SwitchToThisWindow($original, $true); Start-Sleep -Seconds 1
Stop-Process -Id $redline.Id
Get-Content $log | Select-String "TEST|Attached|Detached|Analyzed" | ForEach-Object { $_.Line -replace ' via GenericUia.*','' -replace 'Information SurfaceTracker: ','' -replace 'Debug       AnalysisPipeline: ','' }
