# Draws the Revit Doctor logo: the cobalt FlexiData tile with a white cross and a pulse line cut through it.
# Writes Icons\logo.png (256 px) and Icons\app.ico (16-256 px).
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

    # A white cross: two bars 64 wide, 168 long.
    $g.FillRectangle($white, 96, 44, 64, 168)
    $g.FillRectangle($white, 44, 96, 168, 64)

    # A pulse line cut through the cross in the tile's colour. Below 32 px it would only blur the cross.
    if ($size -ge 32) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0x24, 0x57, 0xD6)), 13
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pulse = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF 58, 128), (New-Object System.Drawing.PointF 102, 128),
            (New-Object System.Drawing.PointF 117, 99), (New-Object System.Drawing.PointF 139, 157),
            (New-Object System.Drawing.PointF 154, 128), (New-Object System.Drawing.PointF 198, 128))
        $g.DrawLines($pen, $pulse)
        $pen.Dispose()
    }

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
