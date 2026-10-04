param([string]$Text = "This is an tset.`r`nI saw the the cat yesterday.`r`nWe recieved teh package.", [int]$Delay = 3500, [string]$Out = "$env:TEMP/overlay1.png")
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class D {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  public static void Front(IntPtr h) {
    uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
    AttachThreadInput(me, fg, true); BringWindowToTop(h); SetForegroundWindow(h); AttachThreadInput(me, fg, false);
  }
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
  public static void Capture(System.Drawing.Graphics g, int x, int y, int w, int h) {
    IntPtr screen = GetDC(IntPtr.Zero); IntPtr dst = g.GetHdc();
    BitBlt(dst, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT (includes layered windows)
    g.ReleaseHdc(dst); ReleaseDC(IntPtr.Zero, screen);
  }
}
"@ -ReferencedAssemblies System.Drawing
[void][D]::SetProcessDPIAware()
$log = Join-Path $env:LOCALAPPDATA ("Redline\logs\redline-" + (Get-Date -Format yyyyMMdd) + ".log")
if (Test-Path $log) { Remove-Item $log }
$redline = Start-Process -FilePath "$PSScriptRoot\..\..\..\src\Redline.App\bin\Debug\net9.0-windows10.0.19041.0\Redline.exe" -PassThru
Start-Sleep -Seconds 3

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class S {
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] static extern short VkKeyScan(char c);
  public static void Type(string s) { foreach (char c in s) { short vk = VkKeyScan(c); bool shift = (vk & 0x100) != 0; if (shift) keybd_event(0x10,0,0,UIntPtr.Zero); keybd_event((byte)vk,0,0,UIntPtr.Zero); keybd_event((byte)vk,0,2,UIntPtr.Zero); if (shift) keybd_event(0x10,0,2,UIntPtr.Zero); } }
}
"@
$lines = @()
for ($i = 1; $i -le 30; $i++) { if ($i % 3 -eq 0) { $lines += "Line $i has a mispeled word." } else { $lines += "Line $i is fine." } }
$form = New-Object System.Windows.Forms.Form
$form.Text = "Redline overlay test"; $form.StartPosition = "Manual"; $form.Left = 120; $form.Top = 120; $form.Width = 620; $form.Height = 260
$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true; $tb.ScrollBars = "Vertical"; $tb.Dock = "Fill"; $tb.Font = New-Object System.Drawing.Font("Segoe UI", 16)
$tb.Text = "This is an tset.`r`n" + ($lines -join "`r`n")
$form.Controls.Add($tb)
function Shot($name) {
  $bmp = New-Object System.Drawing.Bitmap(900, $form.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  [D]::Capture($g, 120, $form.Top, 900, $form.Height)
  $bmp.Save("$env:TEMP/$name.png"); $g.Dispose(); $bmp.Dispose()
}
$script:step = 0
$timer = New-Object System.Windows.Forms.Timer; $timer.Interval = 1500
$timer.Add_Tick({
  $script:step++
  switch ($script:step) {
    1 { $tb.SelectionStart = 16; $tb.SelectionLength = 0 }
    2 { Shot "dyn1_initial"; [S]::Type(" Thsi") }
    3 { Shot "dyn2_typed"; $form.Left = 380 }
    4 { Shot "dyn3_moved"; $tb.SelectionStart = $tb.Text.Length; $tb.ScrollToCaret() }
    5 { Shot "dyn4_scrolled" }
    6 { $timer.Stop(); $form.Close() }
  }
})
$form.Add_Shown({ [D]::Front($form.Handle); $form.Activate(); $tb.Focus(); $timer.Start() })
[void]$form.ShowDialog()
Start-Sleep -Milliseconds 300
Stop-Process -Id $redline.Id
Get-Content $log | Select-String "Overlay|Attached powershell" | ForEach-Object { $_.Line -replace ' via GenericUia.*','' }
