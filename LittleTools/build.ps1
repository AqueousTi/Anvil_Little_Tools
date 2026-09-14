$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $projectDir 'bin'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$referenceDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.1'
$suiteRoot = Split-Path -Parent $projectDir

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Windows .NET Framework C# compiler was not found.'
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$outputPath = Join-Path $outputDir 'LittleTools.exe'
$iconPath = Join-Path $projectDir 'assets\little-tools.ico'
$compilerArgs = @(
    '/nologo'
    '/target:winexe'
    '/optimize+'
    '/platform:x64'
    '/main:LittleTools.Manager.Program'
    "/out:$outputPath"
    "/win32icon:$iconPath"
    "/reference:$referenceDir\PresentationCore.dll"
    "/reference:$referenceDir\PresentationFramework.dll"
    "/reference:$referenceDir\WindowsBase.dll"
    "/reference:$referenceDir\System.Xaml.dll"
    "/reference:$referenceDir\System.Net.Http.dll"
    (Join-Path $suiteRoot 'Common\AtomicFile.cs')
    (Join-Path $projectDir 'Program.cs')
    (Join-Path $suiteRoot 'TodoNotes\Program.cs')
    (Join-Path $suiteRoot 'StockMonitor\StockData.cs')
    (Join-Path $suiteRoot 'StockMonitor\StockWindow.cs')
    (Join-Path $suiteRoot 'StockMonitor\Program.cs')
    (Join-Path $suiteRoot 'AIUsageMonitor\Program.cs')
)

& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}
Write-Output $outputPath
