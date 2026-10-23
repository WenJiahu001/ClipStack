$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$projectRoot = Split-Path $PSScriptRoot -Parent
$iconDirectory = Join-Path $projectRoot 'src\ClipStack.App\Assets'
New-Item -ItemType Directory -Path $iconDirectory -Force | Out-Null
$iconPath = Join-Path $iconDirectory 'clipstack.ico'
$sizes = @(16, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $inset = $size * 0.04
    $edge = $size - 2 * $inset
    $radius = $size * 0.38
    $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddArc($inset, $inset, $radius, $radius, 180, 90)
    $shape.AddArc($inset + $edge - $radius, $inset, $radius, $radius, 270, 90)
    $shape.AddArc($inset + $edge - $radius, $inset + $edge - $radius, $radius, $radius, 0, 90)
    $shape.AddArc($inset, $inset + $edge - $radius, $radius, $radius, 90, 90)
    $shape.CloseFigure()
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(169, 237, 197))
    $graphics.FillPath($background, $shape)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(22, 47, 34), $size * 0.105)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawArc($pen, [single]($size * 0.29), [single]($size * 0.25), [single]($size * 0.43), [single]($size * 0.50), [single]48, [single]264)
    $buffer = [System.IO.MemoryStream]::new()
    $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,$buffer.ToArray()
    $buffer.Dispose(); $pen.Dispose(); $background.Dispose(); $shape.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$stream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length); $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
}
finally { $writer.Dispose(); $stream.Dispose() }
Write-Output $iconPath
