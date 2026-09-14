$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dateStamp = Get-Date -Format 'yyyyMMdd'
$packageName = 'LittleTools-Source-' + $dateStamp
$stageRoot = Join-Path $repoRoot '.source-package-stage'
$packageRoot = Join-Path $stageRoot $packageName
$zipPath = Join-Path $repoRoot ($packageName + '.zip')

$resolvedRepo = [IO.Path]::GetFullPath($repoRoot)
$resolvedStage = [IO.Path]::GetFullPath($stageRoot)
if ([IO.Directory]::GetParent($resolvedStage).FullName -ne $resolvedRepo -or
    [IO.Path]::GetFileName($resolvedStage) -ne '.source-package-stage') {
    throw 'Source package staging directory validation failed.'
}

if (Test-Path -LiteralPath $resolvedStage) {
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

$rootFiles = @(
    '.gitignore',
    'README.md',
    'LINUX_PORTING.md',
    'build-all.ps1',
    'start-all.ps1'
)
$sourceDirectories = @(
    'AIUsageMonitor',
    'Common',
    'CrossPlatform',
    'LittleTools',
    'LittleToolsStartup',
    'packaging',
    'StockMonitor',
    'tests',
    'TodoNotes',
    'TranslateApp'
)

foreach ($relativePath in $rootFiles) {
    $sourcePath = Join-Path $resolvedRepo $relativePath
    if (Test-Path -LiteralPath $sourcePath) {
        Copy-Item -LiteralPath $sourcePath -Destination $packageRoot
    }
}

foreach ($directoryName in $sourceDirectories) {
    $sourceDirectory = Join-Path $resolvedRepo $directoryName
    $destinationDirectory = Join-Path $packageRoot $directoryName
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null

    Get-ChildItem -LiteralPath $sourceDirectory -Recurse -File | Where-Object {
        $relative = $_.FullName.Substring($sourceDirectory.Length + 1)
        $segments = $relative -split '[\\/]'
        $segments -notcontains 'bin' -and
        $segments -notcontains 'obj' -and
        $segments -notcontains 'artifacts' -and
        $segments -notcontains '.package-stage' -and
        $_.Name -ne 'appsettings.json' -and
        $_.Extension -notin @('.exe', '.dll', '.pdb', '.zip')
    } | ForEach-Object {
        $relative = $_.FullName.Substring($sourceDirectory.Length + 1)
        $destinationPath = Join-Path $destinationDirectory $relative
        $destinationParent = Split-Path -Parent $destinationPath
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destinationPath
    }
}

$manifestPath = Join-Path $packageRoot 'SHA256SUMS.txt'
$hashLines = Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
    Where-Object FullName -ne $manifestPath |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($packageRoot.Length + 1).Replace(
            [IO.Path]::DirectorySeparatorChar,
            [char]'/'
        )
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
$hashLines | Set-Content -LiteralPath $manifestPath -Encoding ASCII

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

# Build the ZIP explicitly so entry names always use the forward slashes required
# by the ZIP specification. Compress-Archive writes backslashes on Windows,
# which some Linux extraction tools treat as literal filename characters.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $packageRoot -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($stageRoot.Length + 1).Replace(
            [IO.Path]::DirectorySeparatorChar,
            [char]'/'
        )
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $_.FullName,
            $relative,
            [IO.Compression.CompressionLevel]::Optimal
        ) | Out-Null
    }
}
finally {
    $archive.Dispose()
}
Remove-Item -LiteralPath $resolvedStage -Recurse -Force

Write-Output $zipPath
