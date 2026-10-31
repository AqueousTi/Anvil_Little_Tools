param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('LittleToolsPopupTest-' + [Guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$referenceDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.1'
$output = Join-Path $OutputDirectory 'AutoClickStatusPopupSmoke.exe'
& $compiler /nologo /target:exe /platform:x64 /main:AutoClickStatusPopupSmoke "/out:$output" `
    "/reference:$referenceDir\PresentationCore.dll" "/reference:$referenceDir\PresentationFramework.dll" `
    "/reference:$referenceDir\WindowsBase.dll" "/reference:$referenceDir\System.Xaml.dll" `
    (Join-Path $root 'LittleTools\AutoClickStatusPopup.cs') (Join-Path $root 'tests\AutoClickStatusPopupSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Popup smoke compilation failed.' }
& $output $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Popup smoke tests failed.' }
