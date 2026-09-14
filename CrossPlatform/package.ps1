$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $root
$dateStamp = Get-Date -Format 'yyyyMMdd'
$stageRoot = Join-Path $root '.package-stage'
$resolvedStage = [IO.Path]::GetFullPath($stageRoot)
if ([IO.Directory]::GetParent($resolvedStage).FullName -ne [IO.Path]::GetFullPath($root) -or
    [IO.Path]::GetFileName($resolvedStage) -ne '.package-stage') {
    throw 'Package staging directory validation failed.'
}
if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
New-Item -ItemType Directory -Path $resolvedStage -Force | Out-Null

function Add-Manifest([string]$packageRoot) {
    $manifestPath = Join-Path $packageRoot 'SHA256SUMS.txt'
    $lines = Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
        Where-Object FullName -ne $manifestPath |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($packageRoot.Length + 1).Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
            $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
            "$hash  $relative"
        }
    $lines | Set-Content -LiteralPath $manifestPath -Encoding ASCII
}

function New-StandardZip([string]$sourceRoot, [string]$zipPath) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | ForEach-Object {
            $relative = $_.FullName.Substring($resolvedStage.Length + 1).Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $_.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }
}

$windowsName = "LittleTools-Assistant-win-x64-$dateStamp"
$windowsRoot = Join-Path $resolvedStage $windowsName
New-Item -ItemType Directory -Path (Join-Path $windowsRoot 'App') -Force | Out-Null
Copy-Item -Path (Join-Path $root 'artifacts\win-x64\*') -Destination (Join-Path $windowsRoot 'App') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $windowsRoot
Add-Manifest $windowsRoot
$windowsZip = Join-Path $repoRoot ($windowsName + '.zip')
New-StandardZip $windowsRoot $windowsZip

$linuxName = "LittleTools-Assistant-linux-x64-$dateStamp"
$linuxRoot = Join-Path $resolvedStage $linuxName
New-Item -ItemType Directory -Path (Join-Path $linuxRoot 'App') -Force | Out-Null
Copy-Item -Path (Join-Path $root 'artifacts\linux-x64\*') -Destination (Join-Path $linuxRoot 'App') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $linuxRoot
Copy-Item -LiteralPath (Join-Path $root 'install-linux.sh') -Destination $linuxRoot
Copy-Item -LiteralPath (Join-Path $root 'little-tools-assistant.desktop.in') -Destination $linuxRoot
Add-Manifest $linuxRoot
$linuxArchive = Join-Path $repoRoot ($linuxName + '.tar.gz')
if (Test-Path -LiteralPath $linuxArchive) { Remove-Item -LiteralPath $linuxArchive -Force }
& tar.exe -czf $linuxArchive -C $resolvedStage $linuxName
if ($LASTEXITCODE -ne 0) { throw 'Linux tar.gz creation failed.' }

Remove-Item -LiteralPath $resolvedStage -Recurse -Force
Write-Output $windowsZip
Write-Output $linuxArchive
