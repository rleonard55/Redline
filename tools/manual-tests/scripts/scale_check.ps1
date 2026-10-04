# Runs the overlay checks with the primary display one scaling step up (e.g. 100% -> 125%) and always
# restores the original scaling. Uses SPI_GET/SETLOGICALDPIOVERRIDE (what Settings > Display uses;
# undocumented). The value is ONE int: the current step relative to Windows' *recommended* scale
# (e.g. -2 = 100% on a display that recommends 150%). It is not a min/max/current struct.
# Usage: scale_check.ps1 [-Steps 1]
param([int]$Steps = 1)
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Dpi {
  [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] public static extern bool Get(uint action, uint param, ref int value, uint winIni);
  [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] public static extern bool Set(uint action, int param, IntPtr unused, uint winIni);
}
"@
$SPI_GET = 0x009E; $SPI_SET = 0x009F

# System DPI as a fresh DPI-aware process sees it (this process keeps the DPI it started with).
function SystemDpi { [int](powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\system_dpi.ps1") }

$original = 0
if (-not [Dpi]::Get($SPI_GET, 0, [ref]$original, 0)) { throw "Couldn't read the scaling step" }
$startDpi = SystemDpi
$target = $original + $Steps
"scaling step $original (DPI $startDpi) -> $target"
$scripts = $PSScriptRoot
try {
  [void][Dpi]::Set($SPI_SET, $target, [IntPtr]::Zero, 1)
  Start-Sleep -Seconds 3
  $dpi = SystemDpi
  "system DPI now: $dpi"
  if ($dpi -ne $startDpi + 24 * $Steps) { throw "Unexpected DPI $dpi; stopping" } # each step is 25% = 24 DPI
  # A DPI-aware target: a fresh process (started after the switch) that calls SetProcessDPIAware.
  powershell -NoProfile -ExecutionPolicy Bypass -File "$scripts\redline_overlay_shot.ps1" -Out "$env:TEMP\overlay_scaled_aware.png" | Select-Object -Last 2
  Get-Process Redline -ErrorAction SilentlyContinue | Stop-Process
  Start-Sleep -Seconds 1
  # A DPI-virtualized target: this process started before the switch, so Windows bitmap-scales its windows.
  & "$scripts\redline_overlay_shot.ps1" -Out "$env:TEMP\overlay_scaled_virtualized.png" | Select-Object -Last 2
  Get-Process Redline -ErrorAction SilentlyContinue | Stop-Process
  Start-Sleep -Seconds 1
  # Hover pill + apply in a virtualized window (this process's forms are bitmap-scaled too).
  & "$scripts\redline_hover_e2e.ps1" | Select-Object -First 8
}
finally {
  Get-Process Redline -ErrorAction SilentlyContinue | Stop-Process
  [void][Dpi]::Set($SPI_SET, $original, [IntPtr]::Zero, 1)
  Start-Sleep -Seconds 3
  $after = SystemDpi
  "restored: DPI $after (was $startDpi)"
  if ($after -ne $startDpi) { Write-Warning "Scaling not restored - set it back in Settings > Display" }
}
