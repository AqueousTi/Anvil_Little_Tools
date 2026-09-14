$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$localDotnet = Join-Path (Split-Path -Parent $root) '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }

& $dotnet restore (Join-Path $root 'LittleTools.CrossPlatform.slnx')
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
& $dotnet build (Join-Path $root 'LittleTools.CrossPlatform.slnx') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& $dotnet run --project (Join-Path $root 'LittleTools.Assistant.Tests\LittleTools.Assistant.Tests.csproj') -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Protocol tests failed.' }

Write-Output (Join-Path $root 'LittleTools.Assistant\bin\Release\net10.0\LittleTools.Assistant.exe')
