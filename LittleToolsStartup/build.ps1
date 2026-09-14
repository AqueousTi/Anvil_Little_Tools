$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $projectDir 'bin'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$iconPath = Join-Path (Split-Path -Parent $projectDir) 'LittleTools\assets\little-tools.ico'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Windows .NET Framework C# compiler was not found.'
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$outputPath = Join-Path $outputDir 'LittleToolsStartup.exe'
$compilerArgs = @(
    '/nologo'
    '/target:winexe'
    '/optimize+'
    '/platform:x64'
    "/out:$outputPath"
    "/win32icon:$iconPath"
    (Join-Path $projectDir 'Program.cs')
)

& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}
Write-Output $outputPath
