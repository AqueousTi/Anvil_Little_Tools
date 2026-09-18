$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assetDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'assets'
$sizes = @(16, 20, 24, 32, 48, 256)
$frames = @()

$sourcePath = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'design\littletools-logo-wrench-v1.png'
$logo = [System.Drawing.Image]::FromFile($sourcePath)

foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::White)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($logo, 0, 0, $size, $size)
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

$logo.Dispose()
Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $assetDir 'little-tools-preview.png') -Force
$assistantAssets = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'CrossPlatform\LittleTools.Assistant\Assets'
Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $assistantAssets 'little-tools.png') -Force
Copy-Item -LiteralPath $iconPath -Destination (Join-Path $assistantAssets 'little-tools.ico') -Force
Write-Output $iconPath
