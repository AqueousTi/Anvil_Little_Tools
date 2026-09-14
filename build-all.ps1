$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'AIUsageMonitor\build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'AIUsageMonitor build failed.' }

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'TranslateApp\build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'TranslateApp build failed.' }

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'TodoNotes\build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'TodoNotes build failed.' }

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'StockMonitor\build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'StockMonitor build failed.' }

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'LittleTools\build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'LittleTools Manager build failed.' }

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'LittleToolsStartup\build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'LittleTools Startup build failed.' }

Write-Output 'All Little Tools builds completed.'
