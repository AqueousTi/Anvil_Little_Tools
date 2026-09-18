$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$manager = Join-Path $root 'LittleTools\bin\LittleTools.exe'
if (Get-Process LittleTools,LittleTools.Assistant -ErrorAction SilentlyContinue) { throw 'Exit the suite before this test.' }
function Send-Client([string]$command) {
    $client = Start-Process $manager -ArgumentList $command -WindowStyle Hidden -PassThru
    try {
        if (-not $client.WaitForExit(10000) -or $client.ExitCode -ne 0) { throw "Command failed: $command" }
    } finally { $client.Dispose() }
}
function Assert-NoAssistant {
    if (Get-Process LittleTools.Assistant -ErrorAction SilentlyContinue) { throw 'Startup created an assistant process.' }
}
$instance = $null
try {
    $instance = Start-Process $manager -ArgumentList '--startup' -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
    $instance.Refresh()
    Assert-NoAssistant
    if ($instance.HasExited) { throw 'Deferred startup exited prematurely.' }
    if (@($instance.Modules | Where-Object ModuleName -match 'PresentationFramework|PresentationCore|wpfgfx').Count) {
        throw 'WPF was loaded before the startup delay elapsed.'
    }
    $earlyCpu = $instance.TotalProcessorTime.TotalMilliseconds
    "DEFERRED_CPU_AT_3_SECONDS_MS=$earlyCpu"
    Send-Client '--background'
    if (@(Get-Process LittleTools).Count -ne 1) { throw 'Duplicate startup created a second manager.' }
    Start-Sleep -Seconds 31
    Assert-NoAssistant
    $instance.Refresh()
    if ($instance.HasExited -or (Get-Process LittleTools).Id -ne $instance.Id) { throw 'Startup did not retain the same process.' }
    if (-not @($instance.Modules | Where-Object ModuleName -match 'wpfgfx').Count) { throw 'Widgets did not initialize after the delay.' }
    'DEFERRED_STARTUP_SAME_PROCESS_NO_ASSISTANT_OK'
    Send-Client '--exit'
    if (-not $instance.WaitForExit(10000)) { throw 'Suite did not exit.' }
    $instance.Dispose()

    $instance = Start-Process $manager -ArgumentList '--startup' -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 1
    Send-Client '--chat'
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        $assistant = Get-Process LittleTools.Assistant -ErrorAction SilentlyContinue
        if ($assistant) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $assistant) { throw 'Explicit command did not bypass startup delay.' }
    'EXPLICIT_COMMAND_WAKES_DEFERRED_STARTUP_OK'
    Send-Client '--exit'
    if (-not $instance.WaitForExit(10000)) { throw 'Suite did not exit after wake.' }
    $instance.Dispose()

    $instance = Start-Process $manager -ArgumentList '--startup' -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 1
    Send-Client '--exit'
    if (-not $instance.WaitForExit(5000)) { throw 'Exit did not interrupt startup delay.' }
    'EXIT_INTERRUPTS_DEFERRED_STARTUP_OK'
} finally {
    if ($instance -and -not $instance.HasExited) { Send-Client '--exit'; [void]$instance.WaitForExit(10000) }
    if ($instance) { $instance.Dispose() }
}
