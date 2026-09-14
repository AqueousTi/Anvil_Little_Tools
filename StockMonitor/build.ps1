$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $projectDir 'bin'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$referenceDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.1'
$suiteRoot = Split-Path -Parent $projectDir

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$outputPath = Join-Path $outputDir 'StockMonitor.exe'
$compilerArgs = @(
    '/nologo'
    '/target:winexe'
    '/optimize+'
    '/platform:x64'
    '/main:LittleTools.StockMonitor.Program'
    "/out:$outputPath"
    "/reference:$referenceDir\PresentationCore.dll"
    "/reference:$referenceDir\PresentationFramework.dll"
    "/reference:$referenceDir\WindowsBase.dll"
    "/reference:$referenceDir\System.Xaml.dll"
    "/reference:$referenceDir\System.Net.Http.dll"
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    (Join-Path $suiteRoot 'Common\AtomicFile.cs')
    (Join-Path $projectDir 'StockData.cs')
    (Join-Path $projectDir 'StockWindow.cs')
    (Join-Path $projectDir 'Program.cs')
)

& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }
Write-Output $outputPath
