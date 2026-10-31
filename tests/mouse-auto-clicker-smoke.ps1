param([switch]$StateOnly)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$outputDir = Join-Path $root '.artifacts\auto-clicker-test'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$output = Join-Path $outputDir 'MouseAutoClickerSmoke.exe'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:x64 /main:MouseAutoClickerSmoke "/out:$output" `
    (Join-Path $root 'LittleTools\MouseAutoClicker.cs') (Join-Path $root 'tests\MouseAutoClickerSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Auto clicker smoke compilation failed.' }
if ($StateOnly) { & $output --state-only } else { & $output }
if ($LASTEXITCODE -ne 0) { throw 'Auto clicker smoke tests failed.' }
