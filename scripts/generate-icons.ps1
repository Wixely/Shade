$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$destination = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Shade/Assets'
New-Item -ItemType Directory -Force -Path $destination | Out-Null

# Original Shade artwork. Geometry is shared with the editable SVG below.
function Rounded([single]$x, [single]$y, [single]$width, [single]$height, [single]$radius) {
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc(($x + $width - $diameter), $y, $diameter, $diameter, 270, 90)
    $path.AddArc(($x + $width - $diameter), ($y + $height - $diameter), $diameter, $diameter, 0, 90)
    $path.AddArc($x, ($y + $height - $diameter), $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}
$svg = @'
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <title>Shade</title>
  <rect x="8" y="8" width="240" height="240" rx="48" fill="#141c24"/>
  <rect x="36" y="48" width="184" height="136" rx="20" fill="#60cbb5"/>
  <defs><clipPath id="screen"><rect x="48" y="60" width="160" height="112" rx="10"/></clipPath></defs>
  <rect x="48" y="60" width="160" height="112" rx="10" fill="#28434a"/>
  <path d="M48 60H154L102 172H48Z" fill="#c0f5e8" clip-path="url(#screen)"/>
  <rect x="122" y="184" width="12" height="24" fill="#60cbb5"/>
  <rect x="88" y="204" width="80" height="12" rx="6" fill="#60cbb5"/>
</svg>
'@
[IO.File]::WriteAllText((Join-Path $destination 'shade.svg'), $svg + "`n", [Text.UTF8Encoding]::new($false))
$frames = @()
foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $bitmap = New-Object Drawing.Bitmap($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dark = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#141c24'))
    $teal = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#60cbb5'))
    $shade = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#28434a'))
    $light = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#c0f5e8'))
    $tile = Rounded 8 8 240 240 48
    $monitor = Rounded 36 48 184 136 20
    $screen = Rounded 48 60 160 112 10
    $stand = Rounded 88 204 80 12 6
    $stream = New-Object IO.MemoryStream
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.ScaleTransform(($size / 256.0), ($size / 256.0))
        $graphics.FillPath($dark, $tile)
        $graphics.FillPath($teal, $monitor)
        $graphics.FillPath($shade, $screen)
        $graphics.SetClip($screen)
        $graphics.FillPolygon($light, [Drawing.PointF[]]@(
            [Drawing.PointF]::new(48,60), [Drawing.PointF]::new(154,60),
            [Drawing.PointF]::new(102,172), [Drawing.PointF]::new(48,172)))
        $graphics.ResetClip()
        $graphics.FillRectangle($teal, 122, 184, 12, 24)
        $graphics.FillPath($teal, $stand)
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $bytes = $stream.ToArray()
        $frames += [pscustomobject]@{ Size = $size; Bytes = $bytes }
        if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $destination 'shade.png'), $bytes) }
    }
    finally {
        $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
        $dark.Dispose(); $teal.Dispose(); $shade.Dispose(); $light.Dispose()
        $tile.Dispose(); $monitor.Dispose(); $screen.Dispose(); $stand.Dispose()
    }
}
$file = [IO.File]::Create((Join-Path $destination 'shade.ico'))
$writer = New-Object IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
}
finally { $writer.Dispose(); $file.Dispose() }
Write-Output 'Generated original Shade SVG, PNG and seven-size Windows ICO.'
