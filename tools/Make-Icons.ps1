<#
.SYNOPSIS
    Draws EdgeGlide's icons and writes them to src\Assets as .ico files.

.DESCRIPTION
    EdgeGlide.ico       the app / file icon, 16-256 px
    TrayActive.ico      notification-area icons, one per state, 16-64 px (100-400 % display scaling)
    TraySuspended.ico
    TrayPaused.ico

    The artwork is vector (WPF, built into Windows), so this runs in Windows PowerShell with nothing to install.
    Large sizes show the full scene (a cursor gliding from a tall monitor onto a shorter one); 24 px and below use
    a simplified glyph (the cursor and its glide trail) that stays readable at 16 px. In the tray the trail's
    colour is the state: teal = Active, amber = Suspended, grey = Paused.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Make-Icons.ps1
    powershell -ExecutionPolicy Bypass -File tools\Make-Icons.ps1 -Preview icon-preview.png
#>
param([string] $Preview)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase, System.Xaml

Add-Type -ReferencedAssemblies ([Windows.Media.Visual].Assembly.Location), ([Windows.DependencyObject].Assembly.Location),
    ([System.Xaml.XamlReader].Assembly.Location) -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

public static class IcoFile
{
    // Below 256 px as classic 32-bit DIBs (the most compatible form), 256 px as PNG.
    public static void Write(string path, IList<BitmapSource> images)
    {
        var blobs = new List<byte[]>();
        foreach (var image in images) blobs.Add(image.PixelWidth >= 256 ? Png(image) : Dib(image));
        using (var file = File.Create(path))
        using (var w = new BinaryWriter(file))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
            int offset = 6 + 16 * images.Count;
            for (int i = 0; i < images.Count; i++)
            {
                int s = images[i].PixelWidth;
                w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
                w.Write(blobs[i].Length); w.Write(offset);
                offset += blobs[i].Length;
            }
            foreach (var blob in blobs) w.Write(blob);
        }
    }

    public static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var ms = new MemoryStream()) { encoder.Save(ms); return ms.ToArray(); }
    }

    static byte[] Dib(BitmapSource image)
    {
        int s = image.PixelWidth, stride = s * 4, maskStride = (s + 31) / 32 * 4;
        var px = new byte[stride * s];
        image.CopyPixels(px, stride, 0);   // Pbgra32, premultiplied
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((ushort)1); w.Write((ushort)32);
            w.Write(0); w.Write(stride * s + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = s - 1; y >= 0; y--)   // bottom-up, straight alpha
                for (int x = 0; x < s; x++)
                {
                    int i = y * stride + x * 4;
                    byte a = px[i + 3];
                    for (int c = 0; c < 3; c++) w.Write(a == 0 ? (byte)0 : (byte)Math.Min(255, (px[i + c] * 255 + a / 2) / a));
                    w.Write(a);
                }
            for (int y = s - 1; y >= 0; y--)   // AND mask: 1 = transparent
            {
                var row = new byte[maskStride];
                for (int x = 0; x < s; x++) if (px[y * stride + x * 4 + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                w.Write(row);
            }
            return ms.ToArray();
        }
    }
}
'@

# ---- drawing helpers ----
function Col([string] $hex) { [Windows.Media.Color][Windows.Media.ColorConverter]::ConvertFromString($hex) }
function Pt([double] $x, [double] $y) { New-Object Windows.Point $x, $y }
function Box([double] $x, [double] $y, [double] $w, [double] $h) { New-Object Windows.Rect $x, $y, $w, $h }
function Solid([string] $hex) { $b = New-Object Windows.Media.SolidColorBrush (Col $hex); $b.Freeze(); $b }
function Linear([string] $from, [string] $to, $start, $end, [switch] $Absolute) {
    $b = New-Object Windows.Media.LinearGradientBrush (Col $from), (Col $to), $start, $end
    if ($Absolute) { $b.MappingMode = 'Absolute' }
    $b.Freeze(); $b
}
function Pen($brush, [double] $width) {
    $p = New-Object Windows.Media.Pen $brush, $width
    $p.LineJoin = 'Round'; $p.StartLineCap = 'Round'; $p.EndLineCap = 'Round'
    $p.Freeze(); $p
}
function Geo([string] $data) { $g = [Windows.Media.Geometry]::Parse($data); $g.Freeze(); $g }
function Transparent([string] $hex) { '#00' + $hex.Substring(3) }

# The classic pointer arrow, tip at (0,0), about 13.6 x 21.8 units.
$ArrowShape = Geo 'M 0,0 L 0,19 L 4.5,14.8 L 7.6,21.8 L 10.6,20.5 L 7.6,13.6 L 13.6,13.6 Z'

function Draw-Arrow($dc, [double] $x, [double] $y, [double] $scale, [double] $outlineWidth, $fill) {
    $dc.PushTransform((New-Object Windows.Media.MatrixTransform $scale, 0, 0, $scale, $x, $y))
    $dc.DrawGeometry($fill, (Pen (Solid '#FF0F1217') ($outlineWidth / $scale)), $ArrowShape)
    $dc.Pop()
}

# ---- the artwork ----

# Full scene, 256-unit canvas: a tall monitor and a shorter one, bottom-aligned, and a cursor that has just glided
# across the seam, leaving the left screen and arriving on the right one at the same relative height.
function Draw-Scene($dc) {
    $metal = Linear '#FFA7AFBC' '#FF5E6676' (Pt 0 0) (Pt 0 1)
    $dc.DrawRectangle($metal, $null, (Box 67 158 18 20))
    $dc.DrawRoundedRectangle($metal, $null, (Box 44 174 64 12), 6, 6)
    $dc.DrawRectangle($metal, $null, (Box 188 158 16 20))
    $dc.DrawRoundedRectangle($metal, $null, (Box 168 174 56 12), 6, 6)

    $bezel = Linear '#FF434B5A' '#FF1B1F26' (Pt 0 0) (Pt 0 1)
    $dc.DrawRoundedRectangle($bezel, $null, (Box 8 66 134 94), 11, 11)
    $dc.DrawRoundedRectangle($bezel, $null, (Box 146 92 102 68), 10, 10)

    $screen = Linear '#FF56A6FF' '#FF1D3FB0' (Pt 0 0) (Pt 1 1)
    $dc.DrawRoundedRectangle($screen, $null, (Box 15 73 120 76), 4, 4)
    $dc.DrawRoundedRectangle($screen, $null, (Box 153 99 88 51), 4, 4)

    # Glide trail: starts 52 % down the left screen, ends 52 % down the right one.
    $trail = Geo 'M 22,112 C 70,108 112,113 158,119 L 158,137 C 112,132 70,119 22,112 Z'
    $dc.DrawGeometry((Linear '#0067E8F9' '#FFE6FDFF' (Pt 22 0) (Pt 158 0) -Absolute), $null, $trail)

    Draw-Arrow $dc 152 104 3.1 6 (Linear '#FFFFFFFF' '#FFDDE3EA' (Pt 0 0) (Pt 0 1))
}

# Simplified glyph, 16-unit canvas: the cursor plus three glide streaks in the given colour.
function Draw-Glyph($dc, [string] $trail, [string] $arrow = '#FFFFFFFF') {
    $pen = Pen (Solid $trail) 2
    $dc.DrawLine($pen, (Pt 2.5 5), (Pt 4.5 5))
    $dc.DrawLine($pen, (Pt 1 9), (Pt 4.5 9))
    $dc.DrawLine($pen, (Pt 2 13), (Pt 4.5 13))
    Draw-Arrow $dc 6.5 1 0.65 1 (Solid $arrow)
}

# At 16 px (the tray at 100 % scaling) vector art goes soft, so that size is drawn pixel by pixel:
# the classic pointer (K = outline, W = fill) and three streaks whose tail pixel fades out.
$Arrow16 = @(
    'K',
    'KK',
    'KWK',
    'KWWK',
    'KWWWK',
    'KWWWWK',
    'KWWWWWK',
    'KWWWWWWK',
    'KWWWWWWWK',
    'KWWWWWKKKK',
    'KWWKWWK',
    'KWK KWWK',
    'KK  KWWK',
    'K    KWWK',
    '     KWWK',
    '      KK'
)
$Streaks16 = @(@{ Y = 3; From = 2 }, @{ Y = 7; From = 0 }, @{ Y = 11; From = 1 })   # 2 px tall, end at x = 4

function Pixel-Glyph16([string] $trail, [string] $arrow = '#FFFFFFFF') {
    $px = New-Object 'uint32[]' 256
    function Put([int] $x, [int] $y, [string] $hex, [double] $alpha = 1) {
        $c = Col $hex
        $a = [Math]::Round($c.A * $alpha)
        $px[$y * 16 + $x] = [uint32]([long]$a * 16777216 + [Math]::Round($c.R * $a / 255) * 65536 +
            [Math]::Round($c.G * $a / 255) * 256 + [Math]::Round($c.B * $a / 255))
    }
    foreach ($s in $Streaks16) {
        foreach ($y in $s.Y, ($s.Y + 1)) {
            for ($x = $s.From; $x -le 4; $x++) { Put $x $y $trail ($(if ($x -eq $s.From) { 0.45 } else { 1 })) }
        }
    }
    for ($y = 0; $y -lt 16; $y++) {
        $row = $Arrow16[$y]
        for ($i = 0; $i -lt $row.Length; $i++) {
            if ($row[$i] -eq 'K') { Put (6 + $i) $y '#FF0F1217' }
            elseif ($row[$i] -eq 'W') { Put (6 + $i) $y $arrow }
        }
    }
    $bitmap = New-Object Windows.Media.Imaging.WriteableBitmap 16, 16, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32), $null
    $bitmap.WritePixels((New-Object Windows.Int32Rect 0, 0, 16, 16), $px, 64, 0)
    $bitmap.Freeze()
    $bitmap
}

function Render([int] $size, [double] $units, $draw, [object[]] $with = @()) {
    if ($size -eq 16 -and $units -eq 16) { return Pixel-Glyph16 @with }
    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $k = $size / $units
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform $k, $k))
    & $draw $dc @with
    $dc.Pop()
    $dc.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap.Freeze()
    $bitmap
}

# ---- outputs ----
$states = [ordered]@{
    Active    = @('#FF2DD4BF', '#FFFFFFFF')   # teal trail
    Suspended = @('#FFF5A524', '#FFFFFFFF')   # amber trail
    Paused    = @('#FF8E96A3', '#FFD5D9DF')   # grey trail, slightly dimmed cursor
}

function App-Icon([int] $size) {
    if ($size -le 24) { Render $size 16 ${function:Draw-Glyph} $states.Active }
    else { Render $size 256 ${function:Draw-Scene} }
}

$assets = Join-Path $PSScriptRoot '..\src\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$appSizes = 16, 20, 24, 32, 40, 48, 64, 256
$traySizes = 16, 20, 24, 32, 40, 48, 64

$app = [Collections.Generic.List[Windows.Media.Imaging.BitmapSource]]::new()
foreach ($s in $appSizes) { $app.Add((App-Icon $s)) }
[IcoFile]::Write((Join-Path $assets 'EdgeGlide.ico'), $app)

$tray = @{}
foreach ($name in $states.Keys) {
    $list = [Collections.Generic.List[Windows.Media.Imaging.BitmapSource]]::new()
    foreach ($s in $traySizes) { $list.Add((Render $s 16 ${function:Draw-Glyph} $states[$name])) }
    [IcoFile]::Write((Join-Path $assets "Tray$name.ico"), $list)
    $tray[$name] = $list
}
Write-Host "Wrote EdgeGlide.ico and Tray{Active,Suspended,Paused}.ico to $((Resolve-Path $assets).Path)"

if (-not $Preview) { return }

# ---- preview sheet: every size at 1:1 on a light and a dark background, plus pixel-exact zooms ----
function Label($dc, [string] $text, [double] $x, [double] $y, [string] $color) {
    $ft = New-Object Windows.Media.FormattedText $text, ([Globalization.CultureInfo]::InvariantCulture), 'LeftToRight',
        (New-Object Windows.Media.Typeface 'Segoe UI'), 13, (Solid $color), 1.0
    $dc.DrawText($ft, (Pt $x $y))
}
function Zoom($dc, $image, [double] $x, [double] $y, [int] $factor) {
    $group = New-Object Windows.Media.DrawingGroup
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($group, 'NearestNeighbor')
    $g = $group.Open()
    $g.DrawImage($image, (Box $x $y ($image.PixelWidth * $factor) ($image.PixelHeight * $factor)))
    $g.Close()
    $dc.DrawDrawing($group)
}

$W = 1180; $band = 470
$visual = New-Object Windows.Media.DrawingVisual
$dc = $visual.RenderOpen()
foreach ($b in @(@{ Y = 0; Back = '#FFF3F3F3'; Ink = '#FF333333' }, @{ Y = $band; Back = '#FF1F1F1F'; Ink = '#FFDDDDDD' })) {
    $top = $b.Y
    $dc.DrawRectangle((Solid $b.Back), $null, (Box 0 $top $W $band))
    Label $dc 'App / file icon, actual size' 20 ($top + 12) $b.Ink
    $x = 20
    for ($i = $app.Count - 1; $i -ge 0; $i--) {
        $s = $app[$i].PixelWidth
        $dc.DrawImage($app[$i], (Box $x ($top + 40) $s $s))
        Label $dc "$s" $x ($top + 44 + $s) $b.Ink
        $x += $s + 22
    }
    Label $dc 'zoomed: 16 px x8, 32 px x4' 800 ($top + 12) $b.Ink
    Zoom $dc $app[0] 800 ($top + 40) 8
    Zoom $dc $app[3] 950 ($top + 40) 4

    Label $dc 'Tray, actual size and x8' 20 ($top + 316) $b.Ink
    $x = 20
    foreach ($name in $states.Keys) {
        $dc.DrawImage($tray[$name][0], (Box $x ($top + 344) 16 16))
        Zoom $dc $tray[$name][0] ($x + 28) ($top + 330) 8
        Label $dc $name ($x + 28) ($top + 446) $b.Ink
        $x += 190
    }
    Label $dc 'tray 24 px x4 (150 %)' 600 ($top + 316) $b.Ink
    Zoom $dc $tray.Active[2] 600 ($top + 336) 4
}
$dc.Close()
$sheet = New-Object Windows.Media.Imaging.RenderTargetBitmap $W, (2 * $band), 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
$sheet.Render($visual)
[IO.File]::WriteAllBytes($Preview, [IcoFile]::Png($sheet))
Write-Host "Preview: $Preview"
