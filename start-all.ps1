$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$manager = Join-Path $root 'LittleTools\bin\LittleTools.exe'

if (-not (Get-Process -Name 'LittleTools' -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath $manager -WindowStyle Hidden
}

Write-Output 'Little Tools are running.'
