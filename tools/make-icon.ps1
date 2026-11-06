# Draws the Revit Journal Analyzer logo in the FlexiData style (a cobalt tile with geometric letters:
# a white "R" and a light-blue "j") and writes Icons\logo.png (256 px) and Icons\app.ico (16-256 px).
# Run on Windows:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param([string]$Out = "src\RevitJournal.App\Icons")

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $Out | Out-Null

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # Tile: cobalt #2457D6, corners like the FlexiData tile.
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 28
    $tile.AddArc(0, 0, $r, $r, 180, 90); $tile.AddArc(256 - $r, 0, $r, $r, 270, 90)
    $tile.AddArc(256 - $r, 256 - $r, $r, $r, 0, 90); $tile.AddArc(0, 256 - $r, $r, $r, 90, 90)
    $tile.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x24, 0x57, 0xD6))), $tile)

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $light = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xB9, 0xCB, 0xF6))

    # "R": stem, top, bowl side, middle and a short leg, all 26 wide.
    $g.FillRectangle($white, 44, 44, 26, 168)
    $g.FillRectangle($white, 44, 44, 106, 26)
    $g.FillRectangle($white, 124, 44, 26, 74)
    $g.FillRectangle($white, 44, 96, 106, 22)
    $leg = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF 92, 118), (New-Object System.Drawing.PointF 120, 118),
        (New-Object System.Drawing.PointF 152, 166), (New-Object System.Drawing.PointF 124, 166))
    $g.FillPolygon($white, $leg)

    # "j": dot, stem and a long hook along the bottom, under the R, like the d of FlexiData.
    $g.FillRectangle($light, 186, 44, 26, 26)
    $g.FillRectangle($light, 186, 92, 26, 120)
    $g.FillRectangle($light, 96, 186, 116, 26)

    $g.Dispose()
    return $bmp
}

# logo.png for the title bar
$logo = Draw 256
$logo.Save((Join-Path $Out "logo.png"), [System.Drawing.Imaging.ImageFormat]::Png)

# app.ico with PNG-compressed images (supported since Windows Vista)
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $ms = New-Object System.IO.MemoryStream
    (Draw $s).Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
[System.IO.File]::WriteAllBytes((Join-Path $Out "app.ico"), $ico.ToArray())
"logo.png and app.ico written to $Out"
