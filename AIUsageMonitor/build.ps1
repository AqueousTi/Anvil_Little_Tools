$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $projectDir 'bin'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$referenceDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.1'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Windows .NET Framework C# compiler was not found.'
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

$outputPath = Join-Path $outputDir 'AIUsageMonitor.exe'
$sourcePath = Join-Path $projectDir 'Program.cs'
$compilerArgs = @(
    '/nologo'
    '/target:winexe'
    '/optimize+'
    '/platform:x64'
    "/out:$outputPath"
    "/reference:$referenceDir\PresentationCore.dll"
    "/reference:$referenceDir\PresentationFramework.dll"
    "/reference:$referenceDir\WindowsBase.dll"
    "/reference:$referenceDir\System.Xaml.dll"
    "/reference:$referenceDir\System.Net.Http.dll"
    (Join-Path (Split-Path -Parent $projectDir) 'Common\AtomicFile.cs')
    $sourcePath
)

& $compiler $compilerArgs

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

Write-Output $outputPath
