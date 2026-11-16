# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Dominik Poschinger
# Creates the app icon (M7): small pill with "ue" in the accent color, white text.
# Well visible on light and dark backgrounds (colored area, thin dark border).
# Result: src/UeBar.App/Assets/ueBar.ico (16–256 px, PNG entries) + ueBar-256.png
# Call: powershell -ExecutionPolicy Bypass -File app/build/make-icon.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "src/UeBar.App/Assets"
New-Item -ItemType Directory -Force $assets | Out-Null

$accent = [System.Drawing.Color]::FromArgb(255, 0x3E, 0x91, 0xFF)
$rim = [System.Drawing.Color]::FromArgb(140, 0x0A, 0x2A, 0x5C)

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Pill: full width, ~62 % height, centered
    $w = $size * 0.96; $h = [Math]::Max(6, $size * 0.62)
    $x = ($size - $w) / 2; $y = ($size - $h) / 2; $r = $h
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $r, $h, 90, 180)
    $path.AddArc($x + $w - $r, $y, $r, $h, 270, 180)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $accent), $path)
    if ($size -ge 24) {
        $pen = New-Object System.Drawing.Pen $rim, ([Math]::Max(1, $size / 48))
        $g.DrawPath($pen, $path)
    }

    # "ue" in white, bold
    $fontSize = $h * 0.74
    $font = New-Object System.Drawing.Font "Segoe UI", $fontSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF $x, ($y - $h * 0.06), $w, $h
    $g.DrawString("ue", $font, [System.Drawing.Brushes]::White, $rect, $format)
    $g.Dispose()
    return $bmp
}

# Small sizes as classic 32-bit DIB (System.Drawing/NotifyIcon cannot read PNG entries there),
# large ones as PNG (smaller).
function To-Dib([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2)); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]0); $w.Write([UInt32]0); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    bottom to top, BGRA
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    AND mask (empty, alpha controls transparency)
    for ($y = 0; $y -lt $s; $y++) { $w.Write((New-Object byte[] $maskRow)) }
    $w.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Frame $s
    if ($s -le 64) {
        $pngs += , (To-Dib $bmp)
    }
    else {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , $ms.ToArray()
    }

    if ($s -eq 256) { $bmp.Save((Join-Path $assets "ueBar-256.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# ICO file: header + directory + PNG data
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets "ueBar.ico"), $out.ToArray())
"ueBar.ico written ($($out.Length) bytes, $($sizes.Count) sizes)"
