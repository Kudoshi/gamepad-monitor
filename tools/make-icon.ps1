param([string]$OutIco, [string]$OutPng)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    # Background tile
    $bg = RoundRect 8 8 240 240 52
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 8), (New-Object System.Drawing.PointF 0, 248), ([System.Drawing.Color]::FromArgb(255, 46, 49, 56)), ([System.Drawing.Color]::FromArgb(255, 22, 23, 26))
    $g.FillPath($bgBrush, $bg)
    if ($size -ge 32) {
        $rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 70, 75, 84)), 4
        $g.DrawPath($rim, $bg)
    }

    # Gamepad silhouette (body + two grips), accent gradient
    $pad = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pad.FillMode = 'Winding'
    $pad.AddPath((RoundRect 40 82 176 84 42), $false)
    $pad.AddEllipse(34, 100, 74, 104)
    $pad.AddEllipse(148, 100, 74, 104)
    $padBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 80), (New-Object System.Drawing.PointF 0, 206), ([System.Drawing.Color]::FromArgb(255, 120, 214, 255)), ([System.Drawing.Color]::FromArgb(255, 40, 150, 225))
    $g.FillPath($padBrush, $pad)

    $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 24, 26, 30))
    # D-pad
    $g.FillPath($dark, (RoundRect 62 114 50 16 4))
    $g.FillPath($dark, (RoundRect 79 97 16 50 4))
    # Face buttons
    if ($size -ge 24) {
        foreach ($b in @(@(171, 101), @(171, 135), @(154, 118), @(188, 118))) {
            $g.FillEllipse($dark, $b[0] - 9, $b[1] - 9, 18, 18)
        }
    } else {
        $g.FillEllipse($dark, 154, 103, 34, 34)
    }

    # Live "signal" badge (green dot with ring) top-right
    $ring = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 22, 23, 26))
    $g.FillEllipse($ring, 168, 14, 74, 74)
    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 59, 209, 111))
    $g.FillEllipse($green, 180, 26, 50, 50)
    if ($size -ge 48) {
        $pulse = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 22, 23, 26)), 6
        $pulse.LineJoin = 'Round'; $pulse.StartCap = 'Round'; $pulse.EndCap = 'Round'
        $pts = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF 186, 52), (New-Object System.Drawing.PointF 196, 52),
            (New-Object System.Drawing.PointF 202, 38), (New-Object System.Drawing.PointF 210, 64),
            (New-Object System.Drawing.PointF 216, 52), (New-Object System.Drawing.PointF 225, 52))
        $g.DrawLines($pulse, $pts)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($sz in $sizes) {
    $b = Render $sz
    $ms = New-Object System.IO.MemoryStream
    if ($sz -ge 256) {
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Classic DIB entry: BITMAPINFOHEADER + bottom-up BGRA rows + 1-bit AND mask
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([UInt32]40); $bw.Write([Int32]$sz); $bw.Write([Int32]($sz * 2))
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
        $maskStride = [int]([Math]::Ceiling($sz / 32.0) * 4)
        $bw.Write([UInt32]($sz * $sz * 4 + $maskStride * $sz))
        $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($yy = $sz - 1; $yy -ge 0; $yy--) {
            for ($xx = 0; $xx -lt $sz; $xx++) {
                $c = $b.GetPixel($xx, $yy)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        for ($yy = $sz - 1; $yy -ge 0; $yy--) {
            $row = New-Object byte[] $maskStride
            for ($xx = 0; $xx -lt $sz; $xx++) {
                if ($b.GetPixel($xx, $yy).A -eq 0) { $row[[int][Math]::Floor($xx / 8)] = $row[[int][Math]::Floor($xx / 8)] -bor (0x80 -shr ($xx % 8)) }
            }
            $bw.Write($row)
        }
        $bw.Flush()
    }
    $pngs += , $ms.ToArray()
    if ($sz -eq 256) { $b.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
}

# ICO container with PNG-compressed entries
$fs = [System.IO.File]::Create($OutIco)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"ico: $((Get-Item $OutIco).Length) bytes"
