$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$outputDir = Join-Path $root '.artifacts\todo-templates'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$referenceDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.1'
$output = Join-Path $outputDir 'TodoTemplateSmoke.exe'
& $compiler /nologo /target:exe /platform:x64 /main:TodoTemplateSmoke "/out:$output" `
    "/reference:$referenceDir\PresentationCore.dll" "/reference:$referenceDir\PresentationFramework.dll" `
    "/reference:$referenceDir\WindowsBase.dll" "/reference:$referenceDir\System.Xaml.dll" `
    (Join-Path $root 'Common\AtomicFile.cs') (Join-Path $root 'Common\DesktopScreens.cs') `
    (Join-Path $root 'TodoNotes\Program.cs') (Join-Path $root 'TodoNotes\FocusTracking.cs') `
    (Join-Path $root 'tests\TodoTemplateSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Todo template smoke compilation failed.' }
& $output $outputDir
if ($LASTEXITCODE -ne 0) { throw 'Todo template smoke tests failed.' }
