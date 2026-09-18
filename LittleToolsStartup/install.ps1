$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$suiteRoot = Split-Path -Parent $projectDir
$suitePath = Join-Path $suiteRoot 'LittleTools\bin\LittleTools.exe'
$taskName = 'Little Tools Deferred Start'

if (-not (Test-Path -LiteralPath $suitePath)) { throw "Little Tools is missing: $suitePath" }

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $runKey -Force | Out-Null
Set-ItemProperty -Path $runKey -Name 'Little Tools' -Value ('"' + $suitePath + '" --startup')
Remove-ItemProperty -Path $runKey -Name 'LittleTools' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $runKey -Name 'LittleToolsTranslate' -ErrorAction SilentlyContinue

$startupApproved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
if (Test-Path -LiteralPath $startupApproved) {
    Remove-ItemProperty -Path $startupApproved -Name 'Little Tools' -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $startupApproved -Name 'LittleTools' -ErrorAction SilentlyContinue
}

Write-Output "RUN=$suitePath --startup"
