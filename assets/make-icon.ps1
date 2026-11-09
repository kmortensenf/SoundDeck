param([string]$Out)
# Draws the SoundDeck logo (violet->pink rounded square with a music note) and writes a multi-size .ico (PNG entries).
Add-Type -AssemblyName System.Drawing
$font = 'Segoe Fluent Icons'
$test = New-Object System.Drawing.Font($font, 10); if ($test.Name -ne $font) { $font = 'Segoe MDL2 Assets' }
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $m = [Math]::Max(1, [int]($s * 0.04)); $w = $s - 2 * $m; $r = $w * 0.28
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($m, $m, 2*$r, 2*$r, 180, 90); $p.AddArc($m + $w - 2*$r, $m, 2*$r, 2*$r, 270, 90)
    $p.AddArc($m + $w - 2*$r, $m + $w - 2*$r, 2*$r, 2*$r, 0, 90); $p.AddArc($m, $m + $w - 2*$r, 2*$r, 2*$r, 90, 90); $p.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF $m, $m, $w, $w
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(139,92,246)), ([System.Drawing.Color]::FromArgb(236,72,153)), 45
    $g.FillPath($brush, $p)
    $f = New-Object System.Drawing.Font($font, [single]($s * 0.42), [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString([string][char]0xEC4F, $f, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($s * 0.03), $s, $s), $sf)
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $pngs += ,$ms.ToArray()
    $g.Dispose(); $bmp.Dispose()
}
$fs = [System.IO.File]::Create($Out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $bw.Write($png) }
$bw.Close()
