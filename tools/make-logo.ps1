<#
  Draws the AttractorLock logo from scratch: the Lorenz attractor ("butterfly"), computed
  from its equations and drawn with GDI+. No third-party artwork or fonts are used in the
  logo itself, so it is covered by the repository's MIT licence like the rest of the code.

  Usage (Windows PowerShell 5.1, from the repository root):
    powershell -ExecutionPolicy Bypass -File tools\make-logo.ps1 -Preview <sheet.png>
        Renders every variant at 256/48/32/16 px on dark and light backgrounds.
    powershell -ExecutionPolicy Bypass -File tools\make-logo.ps1 -Variant A
        Writes AttractorLock\Assets\AttractorLock.ico, docs\logo.png and docs\logo.svg.
#>
param(
    [ValidateSet('A', 'B', 'C')]
    [string]$Variant = 'A',
    [string]$Preview
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$Root = Split-Path -Parent $PSScriptRoot

# ---------- The Lorenz attractor ----------
# dx/dt = s(y - x), dy/dt = x(r - z) - y, dz/dt = xy - bz, with the classic s=10, r=28, b=8/3.
# Integrated with RK4 from a fixed start, so the drawing is identical on every run.
function Get-LorenzPoints([int]$Steps, [double]$Dt = 0.01, [int]$Skip = 300) {
    $s = 10.0; $r = 28.0; $b = 8.0 / 3.0
    $x = 0.1; $y = 0.0; $z = 0.0
    $pts = New-Object 'System.Collections.Generic.List[double[]]'
    for ($i = 0; $i -lt $Steps + $Skip; $i++) {
        $k1x = $s * ($y - $x);               $k1y = $x * ($r - $z) - $y;               $k1z = $x * $y - $b * $z
        $x2 = $x + $k1x * $Dt / 2; $y2 = $y + $k1y * $Dt / 2; $z2 = $z + $k1z * $Dt / 2
        $k2x = $s * ($y2 - $x2);             $k2y = $x2 * ($r - $z2) - $y2;            $k2z = $x2 * $y2 - $b * $z2
        $x3 = $x + $k2x * $Dt / 2; $y3 = $y + $k2y * $Dt / 2; $z3 = $z + $k2z * $Dt / 2
        $k3x = $s * ($y3 - $x3);             $k3y = $x3 * ($r - $z3) - $y3;            $k3z = $x3 * $y3 - $b * $z3
        $x4 = $x + $k3x * $Dt; $y4 = $y + $k3y * $Dt; $z4 = $z + $k3z * $Dt
        $k4x = $s * ($y4 - $x4);             $k4y = $x4 * ($r - $z4) - $y4;            $k4z = $x4 * $y4 - $b * $z4
        $x += ($k1x + 2 * $k2x + 2 * $k3x + $k4x) * $Dt / 6
        $y += ($k1y + 2 * $k2y + 2 * $k3y + $k4y) * $Dt / 6
        $z += ($k1z + 2 * $k2z + 2 * $k3z + $k4z) * $Dt / 6
        if ($i -ge $Skip) { $pts.Add([double[]]@($x, $z)) }   # x-z projection: the butterfly
    }
    return , $pts
}

# For small icons: a stretch of the trail that visits the left wing and then the right wing
# once each, as close as possible to $Target points long, so both wings show with only a
# few loops. (A plain short trail can stay on one wing the whole time.)
function Get-TwoWingSegment($pts, [int]$Target) {
    $switches = @()
    for ($i = 1; $i -lt $pts.Count; $i++) {
        if ([Math]::Sign($pts[$i][0]) -ne [Math]::Sign($pts[$i - 1][0])) { $switches += $i }
    }
    $best = $null; $bestErr = [double]::MaxValue
    for ($j = 0; $j + 2 -lt $switches.Count; $j++) {
        $len = $switches[$j + 2] - $switches[$j]
        $err = [Math]::Abs($len - $Target)
        if ($err -lt $bestErr) { $bestErr = $err; $best = $j }
    }
    $from = $switches[$best]; $to = [Math]::Min($pts.Count - 1, $switches[$best + 2] + 8)
    $seg = New-Object 'System.Collections.Generic.List[double[]]'
    for ($i = $from; $i -le $to; $i++) { $seg.Add($pts[$i]) }
    return , $seg
}

# Fits the points into a box (left, top, width, height), z pointing up.
function Fit-Points($pts, [double]$L, [double]$T, [double]$W, [double]$H) {
    $minX = ($pts | ForEach-Object { $_[0] } | Measure-Object -Minimum -Maximum)
    $minZ = ($pts | ForEach-Object { $_[1] } | Measure-Object -Minimum -Maximum)
    $sx = $W / ($minX.Maximum - $minX.Minimum); $sz = $H / ($minZ.Maximum - $minZ.Minimum)
    $k = [Math]::Min($sx, $sz)
    $ox = $L + ($W - ($minX.Maximum - $minX.Minimum) * $k) / 2
    $oz = $T + ($H - ($minZ.Maximum - $minZ.Minimum) * $k) / 2
    $out = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
    foreach ($p in $pts) {
        $out.Add((New-Object System.Drawing.PointF ([float]($ox + ($p[0] - $minX.Minimum) * $k)),
                                                    ([float]($oz + ($minZ.Maximum - $p[1]) * $k))))
    }
    return , $out
}

# ---------- Colours ----------
function Rgb([string]$hex, [int]$a = 255) {
    $v = [Convert]::ToInt32($hex, 16)
    return [System.Drawing.Color]::FromArgb($a, ($v -shr 16) -band 255, ($v -shr 8) -band 255, $v -band 255)
}
function Mix($c1, $c2, [double]$t) {
    return [System.Drawing.Color]::FromArgb(
        [int]($c1.A + ($c2.A - $c1.A) * $t), [int]($c1.R + ($c2.R - $c1.R) * $t),
        [int]($c1.G + ($c2.G - $c1.G) * $t), [int]($c1.B + ($c2.B - $c1.B) * $t))
}
$Cyan = Rgb '22D3EE'; $Violet = Rgb 'A855F7'; $Pink = Rgb 'F472B6'
$Tile = Rgb '14141A'; $TileTop = Rgb '23232E'; $Edge = Rgb '3A3A44'; $Steel = Rgb 'C8CAD6'

# How much of the attractor to draw, and how thick, by final icon size. Small icons get a
# shorter, bolder trail so the two wings stay readable instead of turning into a blob.
function Get-Style([string]$v, [int]$size) {
    if ($size -le 20)     { $steps = 220;  $w = 0.080 }
    elseif ($size -le 32) { $steps = 320;  $w = 0.058 }
    elseif ($size -le 64) { $steps = 1600; $w = 0.032 }
    else                  { $steps = 3200; $w = 0.0105 }
    if ($v -eq 'B') { $w *= 1.35 }
    return @{ Steps = $steps; Width = $w }
}

function New-RoundedRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Draws the trail segment by segment. With a split point, each wing gets its own colour
# (variant B); otherwise the colour runs from cyan to violet along the trail.
function Draw-Trail($g, $pts, [double]$width, $splitX = $null) {
    $n = $pts.Count
    for ($i = 1; $i -lt $n; $i++) {
        $c = if ($null -ne $splitX) { if ($pts[$i].X -lt $splitX) { $Cyan } else { $Pink } }
             else { Mix $Cyan $Violet ($i / $n) }
        $pen = New-Object System.Drawing.Pen $c, ([float]$width)
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawLine($pen, $pts[$i - 1], $pts[$i])
        $pen.Dispose()
    }
}

# Renders one variant at one size. Drawn 4x larger and scaled down for smooth edges.
function Render([string]$v, [int]$size) {
    $ss = 4; $S = $size * $ss
    $big = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Dark rounded tile, like the curtain's Access dialog.
    $m = [Math]::Max(1, $S * 0.03)
    $tilePath = New-RoundedRect $m $m ($S - 2 * $m) ($S - 2 * $m) ($S * 0.22)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new(0, [float]$S), $TileTop, $Tile)
    $g.FillPath($brush, $tilePath)
    if ($size -ge 32) { $g.DrawPath((New-Object System.Drawing.Pen $Edge, ([float]($S * 0.012))), $tilePath) }

    $st = Get-Style $v $size
    $raw = if ($size -le 32) { Get-TwoWingSegment (Get-LorenzPoints 6000) $st.Steps } else { Get-LorenzPoints $st.Steps }
    if ($v -eq 'C') {
        # Padlock: steel shackle, dark body, butterfly inside the body.
        $sw = $S * 0.075
        $shackle = New-Object System.Drawing.Pen $Steel, ([float]$sw)
        $g.DrawArc($shackle, [float]($S * 0.31), [float]($S * 0.13), [float]($S * 0.38), [float]($S * 0.40), 180, 180)
        $g.DrawLine($shackle, [float]($S * 0.31), [float]($S * 0.33), [float]($S * 0.31), [float]($S * 0.45))
        $g.DrawLine($shackle, [float]($S * 0.69), [float]($S * 0.33), [float]($S * 0.69), [float]($S * 0.45))
        $body = New-RoundedRect ($S * 0.19) ($S * 0.42) ($S * 0.62) ($S * 0.46) ($S * 0.08)
        $g.FillPath((New-Object System.Drawing.SolidBrush (Rgb '0B0B10')), $body)
        $g.DrawPath((New-Object System.Drawing.Pen $Steel, ([float]($S * 0.035))), $body)
        $pts = Fit-Points $raw ($S * 0.25) ($S * 0.47) ($S * 0.50) ($S * 0.36)
        Draw-Trail $g $pts ($S * $st.Width * 0.8)
    } else {
        $pts = Fit-Points $raw ($S * 0.15) ($S * 0.17) ($S * 0.70) ($S * 0.66)
        $split = if ($v -eq 'B') { $S / 2 } else { $null }   # the wings sit either side of the centre
        Draw-Trail $g $pts ($S * $st.Width) $split
    }
    $g.Dispose()

    $out = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = 'HighQualityBicubic'
    $g2.PixelOffsetMode = 'HighQuality'
    $g2.CompositingQuality = 'HighQuality'
    $g2.DrawImage($big, 0, 0, $size, $size)
    $g2.Dispose(); $big.Dispose()
    return $out
}

# ---------- Preview sheet ----------
if ($Preview) {
    $variants = @('A', 'B', 'C')
    $names = @{ A = 'A  gradient trail'; B = 'B  two-tone wings'; C = 'C  padlock' }
    $rowH = 320; $W = 1500
    $sheet = New-Object System.Drawing.Bitmap $W, ($rowH * 3), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.TextRenderingHint = 'AntiAlias'
    $font = New-Object System.Drawing.Font 'Segoe UI', 14
    $small = New-Object System.Drawing.Font 'Segoe UI', 9
    for ($r = 0; $r -lt 3; $r++) {
        $v = $variants[$r]; $top = $r * $rowH
        $g.FillRectangle((New-Object System.Drawing.SolidBrush (Rgb '202020')), 0, $top, 900, $rowH)
        $g.FillRectangle((New-Object System.Drawing.SolidBrush (Rgb 'F3F3F3')), 900, $top, 600, $rowH)
        $g.DrawString($names[$v], $font, [System.Drawing.Brushes]::White, 12, $top + 8)
        $x = 20
        foreach ($sz in 256, 48, 32, 16) {
            $bmp = Render $v $sz
            $g.DrawImageUnscaled($bmp, $x, $top + 34)
            $g.DrawString("$sz", $small, [System.Drawing.Brushes]::Gray, $x, $top + 34 + [Math]::Max($sz, 16) + 4)
            if ($sz -eq 16 -or $sz -eq 32) {
                # Zoomed copy (nearest neighbour) so the real pixels can be inspected.
                $z = if ($sz -eq 16) { 8 } else { 4 }
                $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
                $zx = if ($sz -eq 32) { 520 } else { 680 }
                $g.DrawImage($bmp, $zx, $top + 34, $sz * $z, $sz * $z)
                $g.DrawString("$sz px zoomed x$z", $small, [System.Drawing.Brushes]::Gray, $zx, $top + 34 + 128 + 4)
                $g.InterpolationMode = 'Default'; $g.PixelOffsetMode = 'Default'
            }
            $x += $sz + 30
            $bmp.Dispose()
        }
        $g.DrawString('on a light background', $small, [System.Drawing.Brushes]::DimGray, 912, $top + 12)
        $x = 920
        foreach ($sz in 128, 48, 32, 16) {
            $bmp = Render $v $sz
            $g.DrawImageUnscaled($bmp, $x, $top + 34)
            $x += $sz + 30
            $bmp.Dispose()
        }
    }
    $g.Dispose()
    $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
    Write-Host "Preview written to $Preview"
    return
}

# ---------- Icon, PNG and SVG for the chosen variant ----------
function Get-Bgra($bmp) {
    $rect = New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
    $data = $bmp.LockBits($rect, 'ReadOnly', ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $bytes = New-Object byte[] ($data.Stride * $bmp.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    return , $bytes
}

# ICO: 32-bit DIB frames for the small sizes (what the shell and the tray use), PNG for 256.
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = @()
foreach ($sz in $sizes) {
    $bmp = Render $Variant $sz
    $ms = New-Object System.IO.MemoryStream
    if ($sz -eq 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        $bw = New-Object System.IO.BinaryWriter $ms
        $maskRow = [int]([Math]::Ceiling($sz / 32.0) * 4)
        $bw.Write([int]40); $bw.Write([int]$sz); $bw.Write([int]($sz * 2))
        $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
        $bw.Write([int]($sz * $sz * 4 + $maskRow * $sz))
        $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        $px = Get-Bgra $bmp
        for ($row = $sz - 1; $row -ge 0; $row--) { $bw.Write($px, $row * $sz * 4, $sz * 4) }   # bottom-up
        $bw.Write((New-Object byte[] ($maskRow * $sz)))                                          # alpha does the masking
        $bw.Flush()
    }
    $frames += , @($sz, $ms.ToArray())
    $bmp.Dispose()
}
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $d = if ($f[0] -ge 256) { 0 } else { $f[0] }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$f[1].Length); $w.Write([int]$offset)
    $offset += $f[1].Length
}
foreach ($f in $frames) { $w.Write($f[1]) }
$w.Flush()

$assets = Join-Path $Root 'AttractorLock\Assets'
$docs = Join-Path $Root 'docs'
New-Item -ItemType Directory -Force $assets, $docs | Out-Null
[IO.File]::WriteAllBytes((Join-Path $assets 'AttractorLock.ico'), $ico.ToArray())

$png = Render $Variant 256
$png.Save((Join-Path $docs 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$png.Dispose()

# SVG of the large icon for the README: the same points and layout as Render at 256 px.
if ($Variant -eq 'C') {
    Write-Host "Wrote AttractorLock\Assets\AttractorLock.ico and docs\logo.png (variant C has no SVG version)"
    return
}
$st = Get-Style $Variant 256
$pts = Fit-Points (Get-LorenzPoints $st.Steps) 38.4 43.52 179.2 168.96
$inv = [Globalization.CultureInfo]::InvariantCulture
function P($p) { $p.X.ToString('0.##', $inv) + ',' + $p.Y.ToString('0.##', $inv) }
$sw = (256 * $st.Width).ToString('0.##', $inv)
$lines = @()
if ($Variant -eq 'B') {
    # One polyline per run of segments on the same wing, coloured like Draw-Trail.
    $run = @(P $pts[0]); $left = $pts[1].X -lt 128
    for ($i = 1; $i -lt $pts.Count; $i++) {
        $isLeft = $pts[$i].X -lt 128
        if ($isLeft -ne $left) {
            $lines += '<polyline points="{0}" stroke="{1}"/>' -f ($run -join ' '), $(if ($left) { '#22D3EE' } else { '#F472B6' })
            $run = @(P $pts[$i - 1]); $left = $isLeft
        }
        $run += P $pts[$i]
    }
    $lines += '<polyline points="{0}" stroke="{1}"/>' -f ($run -join ' '), $(if ($left) { '#22D3EE' } else { '#F472B6' })
} else {
    $lines += '<polyline points="{0}" stroke="url(#trail)"/>' -f (($pts | ForEach-Object { P $_ }) -join ' ')
}
$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  <defs>
    <linearGradient id="tile" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#23232E"/><stop offset="1" stop-color="#14141A"/></linearGradient>
    <linearGradient id="trail" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#22D3EE"/><stop offset="1" stop-color="#A855F7"/></linearGradient>
  </defs>
  <rect x="7.68" y="7.68" width="240.64" height="240.64" rx="56.32" fill="url(#tile)" stroke="#3A3A44" stroke-width="3.07"/>
  <g fill="none" stroke-width="$sw" stroke-linecap="round" stroke-linejoin="round">
    $($lines -join "`n    ")
  </g>
</svg>
"@
[IO.File]::WriteAllText((Join-Path $docs 'logo.svg'), $svg)
Write-Host "Wrote AttractorLock\Assets\AttractorLock.ico, docs\logo.png, docs\logo.svg (variant $Variant)"
