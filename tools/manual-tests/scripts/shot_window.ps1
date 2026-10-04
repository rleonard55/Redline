# Captures a top-level window (visible frame only, no drop shadow) to a PNG.
# Usage: shot_window.ps1 -Title "Redline - Settings" -Out shot.png [-Pad 0]
# Brings the window to the front first; BitBlt with CAPTUREBLT so layered windows are included.
param([Parameter(Mandatory)][string]$Title, [Parameter(Mandatory)][string]$Out, [int]$Pad = 0, [IntPtr]$Handle = [IntPtr]::Zero)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ShotNative {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint op);
  public static IntPtr Find(string part) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      if (!IsWindowVisible(h)) return true;
      var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
      if (sb.ToString().IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@
$h = $Handle
if ($h -eq [IntPtr]::Zero) { $h = [ShotNative]::Find($Title) }
if ($h -eq [IntPtr]::Zero) { Write-Error "No visible window titled like '$Title'"; exit 1 }
[void][ShotNative]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 400
$r = New-Object ShotNative+RECT
[void][ShotNative]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) # DWMWA_EXTENDED_FRAME_BOUNDS
$x = $r.L - $Pad; $y = $r.T - $Pad; $w = $r.R - $r.L + 2 * $Pad; $hgt = $r.B - $r.T + 2 * $Pad
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dst = $g.GetHdc(); $src = [ShotNative]::GetDC([IntPtr]::Zero)
[void][ShotNative]::BitBlt($dst, 0, 0, $w, $hgt, $src, $x, $y, 0x40CC0020) # SRCCOPY | CAPTUREBLT
[void][ShotNative]::ReleaseDC([IntPtr]::Zero, $src); $g.ReleaseHdc($dst); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Write-Output "saved $Out ($w x $hgt)"
