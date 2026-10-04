# Settings window and tray, end to end, against a running build-folder Redline (started with --settings):
#   pause/resume (checkbox, then the tray menu), hotkey capture with real key presses + Reset,
#   Start with Windows on/off (Run key), diagnostics mode (file log level), About > Check now.
# Only Redline's own windows receive input; every keystroke and click checks what is in front first.
# Usage: powershell -ExecutionPolicy Bypass -File tools/manual-tests/scripts/redline_settings_e2e.ps1 [-Exe path]
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\..\src\Redline.App\bin\Debug\net9.0-windows10.0.19041.0\Redline.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class K {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  public static uint ForegroundPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }
  public static void Down(byte vk) { keybd_event(vk,0,0,UIntPtr.Zero); }
  public static void Up(byte vk) { keybd_event(vk,0,2,UIntPtr.Zero); }
  public static void RightClick() { mouse_event(8,0,0,0,UIntPtr.Zero); mouse_event(16,0,0,0,UIntPtr.Zero); }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$dash = [string][char]0x2014  # kept out of the source: PowerShell 5.1 reads BOM-less scripts as ANSI
$results = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $line = ('{0} {1}{2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { " - $detail" } else { '' }))
    $results.Add($line); Write-Host $line -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
}
function Settings { Get-Content "$env:LOCALAPPDATA\Redline\settings.json" -Raw | ConvertFrom-Json }
function RunValue { (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Redline -ErrorAction SilentlyContinue).Redline }
function LogSince([datetime]$since) {
    $log = Get-ChildItem "$env:LOCALAPPDATA\Redline\logs" -Filter 'redline-*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
    Get-Content $log.FullName | Where-Object { $_ -match '^(\d\d:\d\d:\d\d\.\d{3})' -and [datetime]::ParseExact($Matches[1], 'HH:mm:ss.fff', $null).TimeOfDay -ge $since.TimeOfDay }
}
function WaitFor([scriptblock]$cond, [int]$ms = 3000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 100 }
    return [bool](& $cond)
}
function ById($root, [string]$id) {
    $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)))
}
function ByName($root, [string]$name) {
    $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)))
}
function SetToggle($el, [bool]$on) {
    $p = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $want = if ($on) { 'On' } else { 'Off' }
    if ($p.Current.ToggleState.ToString() -ne $want) { $p.Toggle() }
}
function SelectTab($win, [string]$header) {
    $tab = ByName $win $header
    $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
}
function Invoke($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

# ---- start ----
if (Get-Process Redline -ErrorAction SilentlyContinue) { throw 'Redline is already running; exit it from the tray first.' }
$exePath = (Resolve-Path $Exe).Path
$runBefore = RunValue
$proc = Start-Process $exePath -ArgumentList '--settings' -PassThru
$win = $null
WaitFor { $script:win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,
    (New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Redline $dash Settings"))))); $null -ne $script:win } 15000 | Out-Null
if (-not $win) { throw 'Settings window did not appear.' }
Start-Sleep 2
$hwnd = [IntPtr]$win.Current.NativeWindowHandle

try {
    # 1. Pause / resume from the General tab
    SelectTab $win 'General'
    $t = Get-Date; SetToggle (ById $win 'EnabledBox') $false
    Check 'pause via checkbox' ((WaitFor { -not (Settings).general.enabled }) -and (WaitFor { LogSince $t | Select-String 'Checking paused' }))
    $t = Get-Date; SetToggle (ById $win 'EnabledBox') $true
    Check 'resume via checkbox' ((WaitFor { (Settings).general.enabled }) -and (WaitFor { LogSince $t | Select-String 'Checking resumed' }))

    # 2. Hotkey capture with real key presses (Ctrl+Alt+K), then Reset
    $box = ById $win 'HotkeyBox'
    $box.SetFocus(); Start-Sleep -Milliseconds 300
    if ([K]::GetForegroundWindow() -ne $hwnd -or -not $box.Current.HasKeyboardFocus) {
        Check 'hotkey capture' $false 'Settings window or hotkey box not focused; nothing typed'
    } else {
        $t = Get-Date
        [K]::Down(0x11); [K]::Down(0x12); [K]::Down(0x4B); Start-Sleep -Milliseconds 60
        [K]::Up(0x4B); [K]::Up(0x12); [K]::Up(0x11)
        $ok = WaitFor { (Settings).general.hotkey -eq 'Ctrl+Alt+K' }
        $logged = WaitFor { LogSince $t | Select-String 'Suggestion hotkey: Ctrl\+Alt\+K' }
        $text = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        Check 'hotkey capture' ($ok -and $logged -and $text -eq 'Ctrl+Alt+K') "box='$text' saved=$((Settings).general.hotkey)"
    }
    $t = Get-Date; Invoke (ByName $win 'Reset')
    Check 'hotkey reset' ((WaitFor { (Settings).general.hotkey -eq 'Ctrl+Alt+.' }) -and (WaitFor { LogSince $t | Select-String 'Suggestion hotkey: Ctrl\+Alt\+\.' }))

    # 3. Start with Windows writes and removes the Run value for this exe. The box mirrors the Run key at
    #    startup, so an existing entry (e.g. from an installed copy) shows as checked: clear it first.
    SetToggle (ById $win 'StartupBox') $false
    WaitFor { -not (RunValue) } | Out-Null
    SetToggle (ById $win 'StartupBox') $true
    Check 'start with Windows on' (WaitFor { (RunValue) -eq "`"$exePath`"" }) "run=[$(RunValue)]"
    SetToggle (ById $win 'StartupBox') $false
    Check 'start with Windows off' (WaitFor { -not (RunValue) }) "run=[$(RunValue)]"

    # 4. Diagnostics mode switches the file log level (Debug lines appear only while it's on)
    SelectTab $win 'Diagnostics'
    $t = Get-Date; SetToggle (ById $win 'DiagnosticsBox') $true
    Check 'diagnostics mode on' ((WaitFor { (Settings).general.diagnosticsMode }) -and (WaitFor { LogSince $t | Select-String 'Diagnostics mode: True' }))
    SetToggle (ById $win 'DiagnosticsBox') $false
    Check 'diagnostics mode off' (WaitFor { -not (Settings).general.diagnosticsMode })

    # 5. About: version shown, Check now completes
    SelectTab $win 'About'
    $version = (ById $win 'VersionText').Current.Name
    Invoke (ById $win 'CheckNowButton')
    $statusOk = WaitFor { (ById $win 'UpdateStatus').Current.Name -match 'up to date|available|ready|Couldn' } 20000
    Check 'about: check for updates' $statusOk "$version; status='$((ById $win 'UpdateStatus').Current.Name)'"

    # 6. Pause from the real tray menu; opens the hidden-icons overflow first if the icon lives there
    $trayName = "Redline $dash Ctrl+Alt+. for suggestions"
    # Windows 11 names tray buttons "<app> <tooltip>" (class SystemTray.NormalButton); the chevron is
    # "Show Hidden Icons" (+ " Hide" while the overflow is open).
    $trayButtons = { $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'SystemTray.NormalButton'))) }
    $findTray = { & $trayButtons | Where-Object { $_.Current.Name -like "*$trayName" } | Select-Object -First 1 }
    $tray = & $findTray
    if (-not $tray -or $tray.Current.IsOffscreen) {
        $chevron = & $trayButtons | Where-Object { $_.Current.Name -like 'Show Hidden Icons*' } | Select-Object -First 1
        if ($chevron) {
            Invoke $chevron
            WaitFor { $t2 = & $findTray; $t2 -and -not $t2.Current.IsOffscreen } 3000 | Out-Null
            $tray = & $findTray
        }
    }
    if (-not $tray -or $tray.Current.IsOffscreen) {
        Check 'tray pause' $false 'tray icon not found, even in the hidden-icons overflow; skipped'
    } else {
        $r = $tray.Current.BoundingRectangle
        [void][K]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)); Start-Sleep -Milliseconds 200
        [K]::RightClick()
        $pause = $null
        WaitFor { $script:pause = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)),
                (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Pause'))))); $null -ne $script:pause } 3000 | Out-Null
        if (-not $pause -or [K]::ForegroundPid() -ne $proc.Id) {
            Check 'tray pause' $false 'tray menu did not open in front'
            [K]::Down(0x1B); [K]::Up(0x1B)
        } else {
            SelectTab $win 'General' # the checkbox is only in the UIA tree while its tab is shown
            $t = Get-Date; Invoke $pause
            $paused = (WaitFor { -not (Settings).general.enabled }) -and (WaitFor { LogSince $t | Select-String 'Checking paused' })
            $boxOff = WaitFor { (ById $win 'EnabledBox').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString() -eq 'Off' }
            Check 'tray pause (settings window follows)' ($paused -and $boxOff)
            SetToggle (ById $win 'EnabledBox') $true
            Check 'resume after tray pause' (WaitFor { (Settings).general.enabled })
        }
    }
}
finally {
    # Leave things as they were: running state is the caller's choice, the Run value is restored.
    if ($runBefore) { Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Redline -Value $runBefore }
    elseif (RunValue) { Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Redline }
}

Write-Host ''
Write-Host ("{0} passed, {1} failed" -f ($results | Where-Object { $_ -like 'PASS*' }).Count, ($results | Where-Object { $_ -like 'FAIL*' }).Count)
