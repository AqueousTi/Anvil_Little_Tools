$ErrorActionPreference = 'Stop'

$localToolsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'LittleTools'))
$installRoot = [IO.Path]::GetFullPath((Join-Path $localToolsRoot 'App'))
if ([IO.Directory]::GetParent($installRoot).FullName -ne $localToolsRoot -or [IO.Path]::GetFileName($installRoot) -ne 'App') {
    throw '卸载目录校验失败。'
}

Get-Process -Name 'LittleTools','LittleToolsStartup' -ErrorAction SilentlyContinue | Stop-Process -Force
Unregister-ScheduledTask -TaskName 'Little Tools Deferred Start' -Confirm:$false -ErrorAction SilentlyContinue
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $runKey -Name 'Little Tools' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $runKey -Name 'LittleTools' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $runKey -Name 'LittleToolsTranslate' -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force }

Write-Host 'Little Tools 程序和启动项已移除。待办、账号配置和使用记录仍然保留。' -ForegroundColor Green
