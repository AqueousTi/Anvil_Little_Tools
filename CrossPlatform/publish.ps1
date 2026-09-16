param([ValidateSet('win-x64', 'linux-x64')][string[]]$Runtime = @('win-x64', 'linux-x64'))
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$localDotnet = Join-Path (Split-Path -Parent $root) '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$project = Join-Path $root 'LittleTools.Assistant\LittleTools.Assistant.csproj'
$artifacts = Join-Path $root 'artifacts'

foreach ($targetRuntime in $Runtime) {
    $destination = Join-Path $artifacts $targetRuntime
    & $dotnet publish $project -c Release -r $targetRuntime --self-contained true -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $targetRuntime." }
    Get-ChildItem -LiteralPath $destination -Filter '*.pdb' -File | Remove-Item -Force
}

Write-Output $artifacts
