$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$suiteRoot = Split-Path -Parent $projectDir
$launcherPath = Join-Path $projectDir 'bin\LittleToolsStartup.exe'
$suitePath = Join-Path $suiteRoot 'LittleTools\bin\LittleTools.exe'
$taskName = 'Little Tools Deferred Start'

if (-not (Test-Path -LiteralPath $launcherPath)) { throw "Startup launcher is missing: $launcherPath" }
if (-not (Test-Path -LiteralPath $suitePath)) { throw "Little Tools is missing: $suitePath" }

$action = New-ScheduledTaskAction -Execute $launcherPath -Argument '--deferred'
$settings = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -Principal $principal -Description 'Starts Little Tools after a short delay so Windows startup remains lightweight.' -Force | Out-Null

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $runKey -Force | Out-Null
Set-ItemProperty -Path $runKey -Name 'Little Tools' -Value ('"' + $launcherPath + '"')
Remove-ItemProperty -Path $runKey -Name 'LittleTools' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $runKey -Name 'LittleToolsTranslate' -ErrorAction SilentlyContinue

$startupApproved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
if (Test-Path -LiteralPath $startupApproved) {
    Remove-ItemProperty -Path $startupApproved -Name 'Little Tools' -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $startupApproved -Name 'LittleTools' -ErrorAction SilentlyContinue
}

Write-Output "TASK=$taskName"
Write-Output "RUN=$launcherPath"
