param([Parameter(Mandatory = $true)][string]$ZipPath)
$ErrorActionPreference = 'Stop'

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$zip = [IO.Path]::GetFullPath($ZipPath)
Assert-True (Test-Path -LiteralPath $zip) 'Package ZIP is missing.'
$testRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.package-verify'))
Assert-True ([IO.Directory]::GetParent($testRoot).FullName -eq [IO.Path]::GetFullPath($repoRoot)) 'Verification path is unsafe.'
if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }

try {
    Expand-Archive -LiteralPath $zip -DestinationPath $testRoot
    $packageRoots = @(Get-ChildItem -LiteralPath $testRoot -Directory)
    Assert-True ($packageRoots.Count -eq 1) 'ZIP must contain exactly one package root.'
    $packageRoot = $packageRoots[0].FullName

    if (Test-Path -LiteralPath (Join-Path $packageRoot 'App')) {
        foreach ($required in @('Start.cmd', 'App\LittleTools\bin\LittleTools.exe', 'App\LittleToolsStartup\bin\LittleToolsStartup.exe')) {
            Assert-True (Test-Path -LiteralPath (Join-Path $packageRoot $required)) ('Unified suite file is missing: ' + $required)
        }
        $installedAssistant = Join-Path $packageRoot 'App\Assistant\LittleTools.Assistant.exe'
        $portableAssistant = Join-Path $packageRoot 'App\LittleTools.Assistant.exe'
        Assert-True ((Test-Path -LiteralPath $installedAssistant) -or (Test-Path -LiteralPath $portableAssistant)) 'AI Assistant is missing.'
    }

    Get-ChildItem -LiteralPath $packageRoot -File -Filter '*.ps1' | ForEach-Object {
        $tokens = $null; $parseErrors = $null
        [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
        Assert-True ($parseErrors.Count -eq 0) ('Invalid packaged script: ' + $_.Name)
    }

    $privateNames = 'appsettings.json','data.json','snapshot.json','usage-history.json','manager.json'
    $privateFiles = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object { $privateNames -contains $_.Name })
    Assert-True ($privateFiles.Count -eq 0) 'Package contains private state.'

    $manifestPath = Join-Path $packageRoot 'SHA256SUMS.txt'
    Assert-True (Test-Path -LiteralPath $manifestPath) 'SHA256 manifest is missing.'
    foreach ($line in Get-Content -LiteralPath $manifestPath) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line -split '  ', 2
        Assert-True ($parts.Count -eq 2) 'Malformed SHA256 manifest line.'
        $filePath = Join-Path $packageRoot $parts[1]
        Assert-True (Test-Path -LiteralPath $filePath) ('Manifest file is missing: ' + $parts[1])
        Assert-True ((Get-FileHash -Algorithm SHA256 -LiteralPath $filePath).Hash -eq $parts[0]) ('Hash mismatch: ' + $parts[1])
    }

    Write-Output 'PACKAGE_VERIFY_OK'
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
