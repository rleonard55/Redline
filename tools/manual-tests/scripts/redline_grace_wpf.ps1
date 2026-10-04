Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class F {
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  public static void Front(IntPtr h) { uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId(); AttachThreadInput(me, fg, true); BringWindowToTop(h); SetForegroundWindow(h); AttachThreadInput(me, fg, false); }
}
"@
$log = Join-Path $env:LOCALAPPDATA ("Redline\logs\redline-" + (Get-Date -Format yyyyMMdd) + ".log")
if (Test-Path $log) { Remove-Item $log }
$redline = Start-Process -FilePath "$PSScriptRoot\..\..\..\src\Redline.App\bin\Debug\net9.0-windows10.0.19041.0\Redline.exe" -PassThru
Start-Sleep -Seconds 3
function Mark($t) { Add-Content $log ("{0:HH:mm:ss.fff} ---- TEST: {1}" -f (Get-Date), $t) }

$win = New-Object System.Windows.Window
$win.Title = "Redline grace test (WPF)"; $win.Width = 500; $win.Height = 220
$panel = New-Object System.Windows.Controls.StackPanel
$edit = New-Object System.Windows.Controls.TextBox; $edit.Text = "Editable tset text."; $edit.Height = 40
$btn = New-Object System.Windows.Controls.Button; $btn.Content = "A button"
$ro = New-Object System.Windows.Controls.TextBox; $ro.Text = "Read-only box"; $ro.IsReadOnly = $true
$pw = New-Object System.Windows.Controls.PasswordBox; $pw.Password = "secret"
foreach ($c in @($edit, $btn, $ro, $pw)) { [void]$panel.Children.Add($c) }
$win.Content = $panel
$script:step = 0
$timer = New-Object System.Windows.Threading.DispatcherTimer; $timer.Interval = [TimeSpan]::FromMilliseconds(1500)
$bounce = New-Object System.Windows.Threading.DispatcherTimer; $bounce.Interval = [TimeSpan]::FromMilliseconds(100)
$bounce.Add_Tick({ $bounce.Stop(); [void]$edit.Focus() })
$timer.Add_Tick({
  $script:step++
  switch ($script:step) {
    1 { Mark "bounce to button and back within 100 ms"; [void]$btn.Focus(); $bounce.Start() }
    2 { Mark "move to button and stay"; [void]$btn.Focus() }
    3 { Mark ("still on button: " + $btn.IsKeyboardFocused); [void]$edit.Focus() }
    4 { Mark "move to read-only box and stay"; [void]$ro.Focus() }
    5 { Mark ("still on read-only: " + $ro.IsKeyboardFocused); [void]$edit.Focus() }
    6 { Mark "move to password box"; [void]$pw.Focus() }
    7 { $timer.Stop(); $win.Close() }
  }
})
$win.Add_ContentRendered({ [F]::Front((New-Object System.Windows.Interop.WindowInteropHelper($win)).Handle); [void]$edit.Focus(); $timer.Start() })
[void]$win.ShowDialog()
Start-Sleep -Milliseconds 300
Stop-Process -Id $redline.Id
