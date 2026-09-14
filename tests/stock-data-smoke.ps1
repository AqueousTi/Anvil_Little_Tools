$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$referenceDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.1'
$output = Join-Path $env:TEMP 'LittleTools.StockDataSmoke.exe'

& $compiler /nologo /target:exe /platform:x64 /main:StockDataSmoke "/out:$output" `
    "/reference:$referenceDir\System.Net.Http.dll" `
    (Join-Path $root 'Common\AtomicFile.cs') `
    (Join-Path $root 'StockMonitor\StockData.cs') `
    (Join-Path $root 'tests\StockDataSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Stock data smoke compilation failed.' }
try { & $output; if ($LASTEXITCODE -ne 0) { throw 'Stock data smoke failed.' } }
finally { Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue }
