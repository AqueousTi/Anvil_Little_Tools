$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$manager = Join-Path $root 'LittleTools\bin\LittleTools.exe'

if (-not (Test-Path -LiteralPath $manager)) { throw 'Run build-all.ps1 first.' }
Start-Process -FilePath $manager -WindowStyle Hidden

Write-Output 'Little Tools are running.'
