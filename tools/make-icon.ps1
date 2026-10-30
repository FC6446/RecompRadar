# Generates the RecompRadar application icon (PNG + multi-size ICO) with System.Drawing.
# Run from the repository root:  powershell -File tools/make-icon.ps1
# The artwork is drawn from scratch here (rings, sweep line, blip), so it carries no third-party license.

Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [float]$size

    $bg = New-RoundedRect ($s * 0.04) ($s * 0.04) ($s * 0.92) ($s * 0.92) ($s * 0.22)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 18, 21, 27))), $bg)
    $borderPen = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb(255, 42, 48, 61)), ([single][Math]::Max(1.0, $s * 0.012))
    $g.DrawPath($borderPen, $bg)

    $accent = [System.Drawing.Color]::FromArgb(255, 91, 140, 255)
    $cx = $s / 2
    $cy = $s / 2
    foreach ($ring in @(0.36, 0.25, 0.14)) {
        $r = $s * $ring
        $alpha = if ($ring -eq 0.36) { 255 } elseif ($ring -eq 0.25) { 170 } else { 110 }
        $pen = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb($alpha, 91, 140, 255)), ([single][Math]::Max(1.0, $s * 0.028))
        $g.DrawEllipse($pen, $cx - $r, $cy - $r, $r * 2, $r * 2)
    }

    # sweep line toward the upper right
    $sweep = New-Object System.Drawing.Pen -ArgumentList $accent, ([single][Math]::Max(1.0, $s * 0.034))
    $sweep.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $sweep.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($sweep, $cx, $cy, $cx + ($s * 0.26), $cy - ($s * 0.26))

    # a "blip" found by the radar
    $dot = $s * 0.075
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 63, 185, 80))), $cx + ($s * 0.12) - $dot, $cy - ($s * 0.02) - $dot, $dot * 2, $dot * 2)
    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

$assets = Join-Path (Join-Path $PSScriptRoot '..') 'src/RecompRadar.App/Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$sizes = 16, 32, 48, 64, 128, 256
$images = @{}
foreach ($size in $sizes) { $images[$size] = Get-PngBytes (New-IconBitmap $size) }

[System.IO.File]::WriteAllBytes((Join-Path $assets 'RecompRadar.png'), $images[256])

# ICO container with PNG-compressed images
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
foreach ($size in $sizes) {
    $dim = if ($size -ge 256) { 0 } else { $size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$images[$size].Length); $bw.Write([UInt32]$offset)
    $offset += $images[$size].Length
}
foreach ($size in $sizes) { $bw.Write($images[$size]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'RecompRadar.ico'), $ms.ToArray())

# README logo
$repoAssets = Join-Path (Join-Path $PSScriptRoot '..') 'assets'
New-Item -ItemType Directory -Force $repoAssets | Out-Null
Copy-Item (Join-Path $assets 'RecompRadar.png') (Join-Path $repoAssets 'logo.png') -Force
Write-Output "Icon files written to $assets and $repoAssets"
