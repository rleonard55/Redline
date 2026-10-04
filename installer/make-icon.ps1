# Regenerates src/Redline.App/Redline.ico: the tray icon's design (white disc, red zigzag
# "squiggle"; see TrayIconHost.CreateIcon) at 16/24/32/48/64/256 px. 256 is stored as PNG, the rest as
# 32-bit DIBs (older APIs such as System.Drawing.Icon only read PNG frames at 256 px).
# Usage: powershell -ExecutionPolicy Bypass -File installer/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\Redline.App\Redline.ico'
$sizes = 16, 24, 32, 48, 64, 256
$red = [System.Drawing.Color]::FromArgb(0xD1, 0x24, 0x24)

$frames = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 32.0
    $g.FillEllipse([System.Drawing.Brushes]::White, 1 * $s, 1 * $s, 30 * $s, 30 * $s)
    $pen = New-Object System.Drawing.Pen $red, ([Math]::Max(1.5, 4 * $s))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $points = @(@(5, 18), @(10, 12), @(16, 20), @(22, 12), @(27, 18)) |
        ForEach-Object { New-Object System.Drawing.PointF ($_[0] * $s), ($_[1] * $s) }
    $g.DrawLines($pen, [System.Drawing.PointF[]]$points)
    $pen.Dispose(); $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # BITMAPINFOHEADER (height doubled for the AND mask), BGRA rows bottom-up, then an all-zero AND mask.
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([UInt32]40); $bw.Write([Int32]$size); $bw.Write([Int32]($size * 2))
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
        $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
        $bw.Write([UInt32]($size * $size * 4 + $maskStride * $size))
        $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $bw.Write((New-Object byte[] ($maskStride * $size)))
        $bw.Flush()
    }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICONDIR + ICONDIRENTRY[] + image payloads.
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$frames[$i].Length); $w.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Dispose()
Write-Host "Wrote $out"
