@echo off
set "suite=%~dp0"
if exist "%~dp0App\LittleTools\bin\LittleTools.exe" set "suite=%~dp0App\"
if not exist "%suite%LittleTools\bin\LittleTools.exe" (
    echo Little Tools is not built. Run build-all.ps1 first.
    pause
    exit /b 1
)
start "" "%suite%LittleTools\bin\LittleTools.exe" %*
