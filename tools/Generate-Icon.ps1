# Generates the app icon: a chrome potentiometer knob with a gold arc, drawn purely as vectors.
# Renders several sizes and packs them into one multi-image .ico file (PNG payloads).
param([string]$Out = "MarantzController.ico")

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$GOLD      = [System.Windows.Media.Color]::FromRgb(0xF0, 0xC8, 0x7A)
$GOLD_DIM  = [System.Windows.Media.Color]::FromArgb(0x30, 0xF0, 0xC8, 0x7A)

function Get-ChromeTone([double]$deg) {
    $th = $deg * [Math]::PI / 180.0
    $lobes = [Math]::Abs([Math]::Cos(2 * ($th - 0.38)))
    $b = 0.11 + 0.84 * [Math]::Pow($lobes, 2.2)
    $topLight = (1 - [Math]::Sin($th)) / 2
    $b *= 0.76 + 0.34 * $topLight
    return [Math]::Max(0.05, [Math]::Min(1.0, $b))
}

function Get-Point([double]$cx, [double]$cy, [double]$deg, [double]$r) {
    $rad = $deg * [Math]::PI / 180.0
    return New-Object System.Windows.Point(($cx + $r * [Math]::Cos($rad)), ($cy + $r * [Math]::Sin($rad)))
}

function New-RingSegment([double]$cx, [double]$cy, [double]$a0, [double]$a1, [double]$rOut, [double]$rIn) {
    $g = New-Object System.Windows.Media.StreamGeometry
    $ctx = $g.Open()
    $ctx.BeginFigure((Get-Point $cx $cy $a0 $rOut), $true, $true)
    $ctx.ArcTo((Get-Point $cx $cy $a1 $rOut), (New-Object System.Windows.Size($rOut, $rOut)), 0, $false,
               [System.Windows.Media.SweepDirection]::Clockwise, $true, $false)
    $ctx.LineTo((Get-Point $cx $cy $a1 $rIn), $true, $false)
    $ctx.ArcTo((Get-Point $cx $cy $a0 $rIn), (New-Object System.Windows.Size($rIn, $rIn)), 0, $false,
               [System.Windows.Media.SweepDirection]::Counterclockwise, $true, $false)
    $ctx.Close()
    $g.Freeze()
    return $g
}

function New-Arc([double]$cx, [double]$cy, [double]$start, [double]$sweep, [double]$r) {
    $g = New-Object System.Windows.Media.StreamGeometry
    $ctx = $g.Open()
    $ctx.BeginFigure((Get-Point $cx $cy $start $r), $false, $false)
    $ctx.ArcTo((Get-Point $cx $cy ($start + $sweep) $r), (New-Object System.Windows.Size($r, $r)), 0,
               ($sweep -gt 180), [System.Windows.Media.SweepDirection]::Clockwise, $true, $false)
    $ctx.Close()
    $g.Freeze()
    return $g
}

function Render-Small([int]$S) {
    # 24 px alatt a 240 korcikkbol allo krom zajja esik szet: itt bold, egyszeru
    # formanyelv kell – sotet korong, egy metal-gradiens gyuru, vastag arany iv.
    $cx = $S / 2.0; $cy = $S / 2.0
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()

    $arcR = 0.42 * $S
    $arcW = 0.135 * $S
    $ringR = 0.285 * $S
    $ringW = 0.095 * $S

    # Vastag arany iv (ez az ikon felismerheto jegye)
    $penGold = New-Object System.Windows.Media.Pen((New-Object System.Windows.Media.SolidColorBrush($GOLD)), $arcW)
    $penGold.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $penGold.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $dc.DrawGeometry($null, $penGold, (New-Arc $cx $cy 135 (270 * 0.70) $arcR))

    # Sotet korong
    $dc.DrawEllipse((New-Object System.Windows.Media.SolidColorBrush(
        [System.Windows.Media.Color]::FromRgb(0x12,0x12,0x15))), $null,
        (New-Object System.Windows.Point($cx,$cy)), ($ringR + $ringW / 2), ($ringR + $ringW / 2))

    # Fem gyuru: egyszeru fuggoleges gradiens (fentrol vilagos), nem korcikkek
    $mg = New-Object System.Windows.Media.LinearGradientBrush
    $mg.StartPoint = New-Object System.Windows.Point(0.15, 0)
    $mg.EndPoint = New-Object System.Windows.Point(0.85, 1)
    [void]$mg.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0xF2,0xF2,0xF4), 0)))
    [void]$mg.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x8E,0x8E,0x96), 0.45)))
    [void]$mg.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0xD8,0xD8,0xDE), 0.72)))
    [void]$mg.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x6A,0x6A,0x72), 1)))
    $penRing = New-Object System.Windows.Media.Pen($mg, $ringW)
    $dc.DrawEllipse($null, $penRing, (New-Object System.Windows.Point($cx,$cy)), $ringR, $ringR)

    # Arany kozeppont
    $dc.DrawEllipse((New-Object System.Windows.Media.SolidColorBrush($GOLD)), $null,
        (New-Object System.Windows.Point($cx,$cy)), (0.105 * $S), (0.105 * $S))

    $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $S, $S, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($visual)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    [void]$enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

function Render-Icon([int]$S) {
    # 32 px-ig egyszeru mod: a talca ezeket hasznalja, ott a crisp forma sokkal
    # tobbet er, mint a reszletes de mottlingos krom.
    if ($S -le 32) { return ,(Render-Small $S) }

    $cx = $S / 2.0; $cy = $S / 2.0
    $arcR   = 0.435 * $S
    $arcW   = 0.075 * $S
    $rOut   = 0.355 * $S
    $rIn    = 0.215 * $S
    $discR  = 0.225 * $S

    # Detail level scaled to the size: on a small icon the fine striation would just be noise.
    if ($S -ge 128) { $segs = 240 } elseif ($S -ge 48) { $segs = 96 } else { $segs = 60 }
    $striae = ($S -ge 96)   # csak nagy ikonon: kisebben moire-zik

    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()

    # 1. Sotet korong a gyuru alatt (adjon testet vilagos hatteren is)
    $bg = New-Object System.Windows.Media.RadialGradientBrush
    $bg.GradientOrigin = New-Object System.Windows.Point(0.5, 0.3)
    $bg.Center = New-Object System.Windows.Point(0.5, 0.45)
    $bg.RadiusX = 0.75; $bg.RadiusY = 0.75
    [void]$bg.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x24,0x24,0x28), 0)))
    [void]$bg.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x0E,0x0E,0x10), 1)))
    $dc.DrawEllipse($bg, $null, (New-Object System.Windows.Point($cx, $cy)), ($rOut + $arcW * 0.35), ($rOut + $arcW * 0.35))

    # 2. Arany palya (halvany, teljes kor) + ertekiv
    $penDim = New-Object System.Windows.Media.Pen((New-Object System.Windows.Media.SolidColorBrush($GOLD_DIM)), $arcW)
    $penDim.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $penDim.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $dc.DrawGeometry($null, $penDim, (New-Arc $cx $cy 135 270 $arcR))

    $penGold = New-Object System.Windows.Media.Pen((New-Object System.Windows.Media.SolidColorBrush($GOLD)), $arcW)
    $penGold.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $penGold.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $dc.DrawGeometry($null, $penGold, (New-Arc $cx $cy 135 (270 * 0.68) $arcR))

    # 3. Krom gyuru: szog szerint valtozo tonusu korcikkek
    $step = 360.0 / $segs
    for ($i = 0; $i -lt $segs; $i++) {
        $a0 = $i * $step
        $mid = $a0 + $step / 2
        $tone = Get-ChromeTone $mid
        if ($striae) { $tone = [Math]::Max(0.05, [Math]::Min(1.0, $tone + 0.02 * [Math]::Sin($mid * 5.7))) }
        $v = [byte][Math]::Round($tone * 255)
        $col = [System.Windows.Media.Color]::FromRgb($v, [byte]($v * 0.99), [byte]($v * 0.96))
        $br = New-Object System.Windows.Media.SolidColorBrush($col)
        $dc.DrawGeometry($br, $null, (New-RingSegment $cx $cy $a0 ($a0 + $step + 0.4) $rOut $rIn))
    }

    # 4. Peremek: kivul sotet kontur, belul vilagos fazetta
    $penEdgeDark = New-Object System.Windows.Media.Pen(
        (New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(0xB0,0x08,0x08,0x0A))),
        [Math]::Max(1.0, $S / 180.0))
    $dc.DrawEllipse($null, $penEdgeDark, (New-Object System.Windows.Point($cx,$cy)), $rOut, $rOut)
    if ($S -ge 32) {
        $penEdgeLite = New-Object System.Windows.Media.Pen(
            (New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(0x80,0xFF,0xFF,0xFF))),
            [Math]::Max(1.0, $S / 220.0))
        $dc.DrawEllipse($null, $penEdgeLite, (New-Object System.Windows.Point($cx,$cy)), ($rIn + 0.5), ($rIn + 0.5))
    }

    # 5. Belso sotet korong
    $disc = New-Object System.Windows.Media.RadialGradientBrush
    $disc.GradientOrigin = New-Object System.Windows.Point(0.5, 0.28)
    $disc.Center = New-Object System.Windows.Point(0.5, 0.42)
    $disc.RadiusX = 0.8; $disc.RadiusY = 0.8
    [void]$disc.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x2C,0x2C,0x31), 0)))
    [void]$disc.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x0C,0x0C,0x0E), 1)))
    $dc.DrawEllipse($disc, $null, (New-Object System.Windows.Point($cx,$cy)), $discR, $discR)

    # 6. Arany jelolo pont a korong felso reszen (a potmeter allasa) â€“ karakter kis meretben is
    $markR = $discR * 0.30
    $markPos = Get-Point $cx $cy 270 ($discR * 0.48)
    $dc.DrawEllipse((New-Object System.Windows.Media.SolidColorBrush($GOLD)), $null, $markPos, $markR, $markR)

    $dc.Close()

    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $S, $S, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($visual)

    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    [void]$enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    # A vezeto vesszo megakadalyozza, hogy a PowerShell a byte-tombot szetszedje.
    return ,$ms.ToArray()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
    # Objektum, NEM tomb: a @($s, $tomb) belapitana a byte-tombot.
    $data = [byte[]](Render-Icon $s)
    $pngs += [pscustomobject]@{ Size = $s; Data = $data }
    Write-Output ("  renderelve: {0}x{0} ({1} byte)" -f $s, $data.Length)
}

# --- ICO osszefuzese ---
$outPath = Join-Path $PSScriptRoot $Out
$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)              # reserved
$bw.Write([UInt16]1)              # type = icon
$bw.Write([UInt16]$pngs.Count)    # kepek szama

$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p.Size; $data = $p.Data
    if ($s -ge 256) { $dim = [byte]0 } else { $dim = [byte]$s }
    $bw.Write($dim)               # width
    $bw.Write($dim)               # height
    $bw.Write([byte]0)            # paletta szinek
    $bw.Write([byte]0)            # reserved
    $bw.Write([UInt16]1)          # planes
    $bw.Write([UInt16]32)         # bit/pixel
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write($p.Data) }
$bw.Flush(); $fs.Close()
Write-Output "MENTVE: $outPath ($((Get-Item $outPath).Length) byte, $($pngs.Count) meret)"

