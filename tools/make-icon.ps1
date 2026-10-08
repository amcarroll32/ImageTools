<#
.SYNOPSIS
  Draws the app icon (two overlapping photos on the family's dark rounded tile) and writes Assets\app.ico.

.DESCRIPTION
  Same tile as Disk Visualizer, different motif: a photo with its duplicate behind it.
  Each size is drawn natively rather than downscaled, so small sizes stay crisp:
  16-24 px use a simpler layout on whole pixels with 1 px gaps. Colors are the family's
  dark-theme categorical slots 1-4. Run from anywhere:  powershell -File tools\make-icon.ps1
#>
param([string]$OutFile = (Join-Path $PSScriptRoot '..\Assets\app.ico'))

Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

$tileFill   = [System.Drawing.ColorTranslator]::FromHtml('#262624')
$tileBorder = [System.Drawing.ColorTranslator]::FromHtml('#55544f')
$blue   = [System.Drawing.ColorTranslator]::FromHtml('#3987e5')
$orange = [System.Drawing.ColorTranslator]::FromHtml('#d95926')
$aqua   = [System.Drawing.ColorTranslator]::FromHtml('#199e70')
$yellow = [System.Drawing.ColorTranslator]::FromHtml('#c98500')

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0) { $path.AddRectangle((New-Object System.Drawing.RectangleF $x, $y, $w, $h)); return $path }
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Fill-Block($g, [System.Drawing.Color]$color, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $brush = New-Object System.Drawing.SolidBrush $color
    $path = New-RoundedRect $x $y $w $h $r
    $g.FillPath($brush, $path)
    $path.Dispose(); $brush.Dispose()
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $small = $size -le 24
    $s = $size / 256.0

    # Tile
    $margin = if ($small) { 0.5 } else { 8 * $s }
    $radius = if ($small) { [Math]::Max(2, $size * 0.18) } else { 48 * $s }
    $tile = New-RoundedRect $margin $margin ($size - 2 * $margin) ($size - 2 * $margin) $radius
    $g.FillPath((New-Object System.Drawing.SolidBrush $tileFill), $tile)
    $penWidth = if ($small) { 1 } else { [Math]::Max(1, 6 * $s) }
    $g.DrawPath((New-Object System.Drawing.Pen $tileBorder, $penWidth), $tile)

    if ($small) {
        # Back photo (orange) top-right, front photo (blue) bottom-left with a landscape strip.
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
        $pad = [Math]::Round($size * 0.2)
        $inner = $size - 2 * $pad
        $w = [Math]::Round($inner * 0.72)
        $h = [Math]::Round($inner * 0.62)
        $bx = $size - $pad - $w; $by = $pad
        $fx = $pad; $fy = $size - $pad - $h
        Fill-Block $g $orange $bx $by $w $h 0
        Fill-Block $g $tileFill ($fx) ($fy - 1) ($w + 1) ($h + 1) 0
        Fill-Block $g $blue $fx $fy $w $h 0
        $strip = [Math]::Max(1, [Math]::Round($h * 0.34))
        Fill-Block $g $aqua $fx ($fy + $h - $strip) $w $strip 0
    }
    else {
        $pad = 40 * $s; $gap = 10 * $s; $r = 12 * $s
        $inner = $size - 2 * $pad
        $w = $inner * 0.76; $h = $inner * 0.66
        $bx = $size - $pad - $w; $by = $pad
        $fx = $pad; $fy = $size - $pad - $h

        # Back photo, then a tile-colored halo so the front photo reads as a separate print.
        Fill-Block $g $orange $bx $by $w $h $r
        Fill-Block $g $tileFill ($fx - $gap) ($fy - $gap) ($w + 2 * $gap) ($h + 2 * $gap) ($r + $gap)
        Fill-Block $g $blue $fx $fy $w $h $r

        # Inside the front photo: a sun and a landscape band.
        $sun = $h * 0.24
        Fill-Block $g $yellow ($fx + $w - $sun - $h * 0.16) ($fy + $h * 0.16) $sun $sun ($sun / 2)
        $band = $h * 0.32
        $clip = New-RoundedRect $fx $fy $w $h $r
        $g.SetClip($clip)
        Fill-Block $g $aqua $fx ($fy + $h - $band) $w $band 0
        $g.ResetClip()
        $clip.Dispose()
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

# Pack PNG-compressed images into an .ico (supported since Windows Vista).
$images = foreach ($size in $sizes) { , (Draw-Icon $size) }
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()

$OutFile = [System.IO.Path]::GetFullPath($OutFile)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($OutFile)) | Out-Null
[System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
"Wrote $OutFile ($($sizes -join ', ') px)"
