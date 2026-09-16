$ErrorActionPreference = 'Stop'

$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceRoot = Join-Path $packageRoot 'App'
$installRoot = Join-Path $env:LOCALAPPDATA 'LittleTools\App'
$managerPath = Join-Path $installRoot 'LittleTools\bin\LittleTools.exe'
$launcherPath = Join-Path $installRoot 'LittleToolsStartup\bin\LittleToolsStartup.exe'
$taskName = 'Little Tools Deferred Start'

if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'LittleTools\bin\LittleTools.exe'))) {
    throw '安装包不完整：找不到 LittleTools.exe。'
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'LittleToolsStartup\bin\LittleToolsStartup.exe'))) {
    throw '安装包不完整：找不到 LittleToolsStartup.exe。'
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'Assistant\LittleTools.Assistant.exe'))) {
    throw '安装包不完整：找不到 LittleTools.Assistant.exe。'
}

$localToolsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'LittleTools'))
$installRoot = [IO.Path]::GetFullPath($installRoot)
if ([IO.Directory]::GetParent($installRoot).FullName -ne $localToolsRoot -or [IO.Path]::GetFileName($installRoot) -ne 'App') {
    throw '安装目录校验失败。'
}

Get-Process -Name 'LittleTools','LittleToolsStartup','LittleTools.Assistant' -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
foreach ($folderName in 'LittleTools','LittleToolsStartup','Assistant') {
    $oldFolder = [IO.Path]::GetFullPath((Join-Path $installRoot $folderName))
    if ([IO.Directory]::GetParent($oldFolder).FullName -ne $installRoot) { throw '旧程序目录校验失败。' }
    if (Test-Path -LiteralPath $oldFolder) { Remove-Item -LiteralPath $oldFolder -Recurse -Force }
}
Copy-Item -LiteralPath (Join-Path $sourceRoot 'LittleTools') -Destination $installRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'LittleToolsStartup') -Destination $installRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'Assistant') -Destination $installRoot -Recurse -Force

$runCommand = '"' + $launcherPath + '"'
try {
    $action = New-ScheduledTaskAction -Execute $launcherPath -Argument '--deferred'
    $settings = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
    $principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -Principal $principal -Description 'Starts Little Tools after a short delay so Windows startup remains lightweight.' -Force | Out-Null
}
catch {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    $runCommand = '"' + $launcherPath + '" --deferred'
    Write-Warning '公司策略不允许创建计划任务，已自动使用兼容的低占用延迟启动方式。'
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $runKey -Force | Out-Null
Set-ItemProperty -Path $runKey -Name 'Little Tools' -Value $runCommand
Remove-ItemProperty -Path $runKey -Name 'LittleTools' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $runKey -Name 'LittleToolsTranslate' -ErrorAction SilentlyContinue

Start-Process -FilePath $managerPath -WindowStyle Hidden
Write-Host ''
Write-Host 'Little Tools 安装完成并已启动。' -ForegroundColor Green
Write-Host "安装位置：$installRoot"
Write-Host '如需翻译、DeepSeek 或 GLM 监控，请运行「配置账号.cmd」。'
