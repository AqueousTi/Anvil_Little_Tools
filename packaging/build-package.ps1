$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$packageName = 'LittleTools-Portable-' + (Get-Date -Format 'yyyyMMdd')
$stageRoot = Join-Path $repoRoot '.package-stage'
$packageRoot = Join-Path $stageRoot $packageName
$zipPath = Join-Path $repoRoot ($packageName + '.zip')

$stageRoot = [IO.Path]::GetFullPath($stageRoot)
if ([IO.Directory]::GetParent($stageRoot).FullName -ne [IO.Path]::GetFullPath($repoRoot) -or [IO.Path]::GetFileName($stageRoot) -ne '.package-stage') {
    throw '打包临时目录校验失败。'
}
if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $packageRoot 'App\LittleTools\bin') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageRoot 'App\LittleToolsStartup\bin') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageRoot 'App\Assistant') -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $repoRoot 'LittleTools\bin\LittleTools.exe') -Destination (Join-Path $packageRoot 'App\LittleTools\bin\LittleTools.exe')
Copy-Item -LiteralPath (Join-Path $repoRoot 'LittleToolsStartup\bin\LittleToolsStartup.exe') -Destination (Join-Path $packageRoot 'App\LittleToolsStartup\bin\LittleToolsStartup.exe')
$assistantSource = Join-Path $repoRoot 'CrossPlatform\artifacts\win-x64'
if (-not (Test-Path -LiteralPath (Join-Path $assistantSource 'LittleTools.Assistant.exe'))) {
    throw 'AI 助手尚未发布。请先运行 CrossPlatform\publish.ps1。'
}
Copy-Item -Path (Join-Path $assistantSource '*') -Destination (Join-Path $packageRoot 'App\Assistant') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\install.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\configure.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\uninstall.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\Install.cmd') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\Configure.cmd') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\Uninstall.cmd') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\README.txt') -Destination $packageRoot

# Windows PowerShell 5.1 needs a BOM to decode Chinese text reliably.
Get-ChildItem -LiteralPath $packageRoot -File | Where-Object Extension -in '.ps1','.txt' | ForEach-Object {
    $content = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
    Set-Content -LiteralPath $_.FullName -Value $content -Encoding UTF8
}

$hashLines = Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($packageRoot.Length + 1)
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
    "$hash  $relative"
}
$hashLines | Set-Content -LiteralPath (Join-Path $packageRoot 'SHA256SUMS.txt') -Encoding ASCII

if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
Remove-Item -LiteralPath $stageRoot -Recurse -Force
Write-Output $zipPath
