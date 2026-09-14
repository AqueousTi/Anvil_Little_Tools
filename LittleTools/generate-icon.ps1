$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assetDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'assets'
$sizes = @(16, 20, 24, 32, 48, 256)
$frames = @()

function Add-RoundedBubblePath([System.Drawing.Drawing2D.GraphicsPath]$path) {
    $path.StartFigure()
    $path.AddLine(10.2, 10.8, 19.8, 10.8)
    $path.AddBezier(19.8, 10.8, 21.7, 10.8, 23.2, 12.3, 23.2, 14.2)
    $path.AddLine(23.2, 14.2, 23.2, 17.7)
    $path.AddBezier(23.2, 17.7, 23.2, 19.6, 21.7, 21.1, 19.8, 21.1)
    $path.AddLine(19.8, 21.1, 16.2, 21.1)
    $path.AddLine(16.2, 21.1, 11.9, 24.0)
    $path.AddLine(11.9, 24.0, 11.9, 21.1)
    $path.AddLine(11.9, 21.1, 10.2, 21.1)
    $path.AddBezier(10.2, 21.1, 8.3, 21.1, 6.8, 19.6, 6.8, 17.7)
    $path.AddLine(6.8, 17.7, 6.8, 14.2)
    $path.AddBezier(6.8, 14.2, 6.8, 12.3, 8.3, 10.8, 10.2, 10.8)
    $path.CloseFigure()
}

foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scale = $size / 32.0
        $graphics.ScaleTransform($scale, $scale)
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 2.2)
        try {
            $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $graphics.DrawArc($pen, 3.6, 3.6, 24.8, 24.8, -68, 316)
            $bubble = New-Object System.Drawing.Drawing2D.GraphicsPath
            try {
                Add-RoundedBubblePath $bubble
                $graphics.DrawPath($pen, $bubble)
            } finally { $bubble.Dispose() }
        } finally { $pen.Dispose() }

        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames += [pscustomobject]@{ Size = $size; Data = $stream.ToArray() }
        [System.IO.File]::WriteAllBytes((Join-Path $assetDir ("little-tools-{0}.png" -f $size)), $stream.ToArray())
        $stream.Dispose()
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$iconPath = Join-Path $assetDir 'little-tools.ico'
$iconStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($iconStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Data.Length
    }
    foreach ($frame in $frames) { $writer.Write($frame.Data) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($iconPath, $iconStream.ToArray())
} finally {
    $writer.Dispose()
    $iconStream.Dispose()
}

$preview = New-Object System.Drawing.Bitmap(512, 512, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$previewGraphics = [System.Drawing.Graphics]::FromImage($preview)
try {
    $previewGraphics.Clear([System.Drawing.Color]::FromArgb(32, 32, 32))
    $source = [System.Drawing.Image]::FromFile((Join-Path $assetDir 'little-tools-256.png'))
    try { $previewGraphics.DrawImage($source, 128, 128, 256, 256) } finally { $source.Dispose() }
    $preview.Save((Join-Path $assetDir 'little-tools-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $previewGraphics.Dispose()
    $preview.Dispose()
}

Write-Output $iconPath
