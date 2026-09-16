$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$manager = Join-Path $repoRoot 'LittleTools\bin\LittleTools.exe'
$assistant = Join-Path $repoRoot 'CrossPlatform\artifacts\win-x64\LittleTools.Assistant.exe'
$session = (Get-Process -Id $PID).SessionId

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}
function Get-SuiteProcess([string]$name) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session
}
function Wait-For([scriptblock]$condition, [string]$message) {
    $deadline = [datetime]::UtcNow.AddSeconds(15)
    do {
        if (& $condition) { return }
        Start-Sleep -Milliseconds 150
    } while ([datetime]::UtcNow -lt $deadline)
    throw $message
}
function Start-Client([string]$path, [string]$argument) {
    $client = Start-Process -FilePath $path -ArgumentList $argument -WindowStyle Hidden -PassThru
    try {
        Assert-True ($client.WaitForExit(15000)) 'Command client did not exit.'
        Assert-True ($client.ExitCode -eq 0) ('Command forwarding failed: ' + $argument)
    } finally { $client.Dispose() }
}

Assert-True (@(Get-SuiteProcess 'LittleTools').Count -eq 0) 'Exit the running suite before this integration test.'
Assert-True (@(Get-SuiteProcess 'LittleTools.Assistant').Count -eq 0) 'Exit the running assistant before this integration test.'
$assembly = [Reflection.Assembly]::LoadFrom($manager)
$send = $assembly.GetType('LittleTools.Common.CommandPipe', $true).GetMethod('Send')
function Send-Pipe([string]$name, [string]$command = 'ping') {
    $send.Invoke($null, @($name, $command, 1000))
}
function Stop-Suite {
    if (@(Get-SuiteProcess 'LittleTools').Count -gt 0) { Start-Client $manager '--exit' }
    elseif (@(Get-SuiteProcess 'LittleTools.Assistant').Count -gt 0) { Send-Pipe 'LittleTools.Assistant.Command.v1' 'Exit' | Out-Null }
    Wait-For { @(Get-SuiteProcess 'LittleTools').Count -eq 0 -and @(Get-SuiteProcess 'LittleTools.Assistant').Count -eq 0 } 'Suite exit left a process running.'
}

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class SuiteWindows {
    private delegate bool EnumProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    public static string[] Titles(int processId) {
        var titles = new List<string>();
        EnumWindows(delegate(IntPtr handle, IntPtr parameter) {
            uint owner; GetWindowThreadProcessId(handle, out owner);
            if (owner == processId && IsWindowVisible(handle)) {
                var text = new StringBuilder(512); GetWindowText(handle, text, text.Capacity);
                titles.Add(text.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return titles.ToArray();
    }
}
'@

try {
    Start-Client $assistant '--background'
    Wait-For { (Send-Pipe 'LittleTools.Manager.Command.v1') -gt 0 -and (Send-Pipe 'LittleTools.Assistant.Command.v1') -gt 0 } 'Assistant entry did not start the full suite.'
    $managerId = (Get-SuiteProcess 'LittleTools').Id
    $assistantId = (Get-SuiteProcess 'LittleTools.Assistant').Id
    $settings = Get-Content (Join-Path $env:LOCALAPPDATA 'LittleTools\manager.json') -Raw | ConvertFrom-Json
    if ($settings.MonitorEnabled) {
        Wait-For { [SuiteWindows]::Titles($managerId) -contains 'AI Usage Monitor' } 'Usage monitor did not appear.'
    }
    if ($settings.TodoNotesEnabled) {
        Wait-For { [SuiteWindows]::Titles($managerId) -contains 'Little Tools · Daily Todo' } 'Todo did not appear.'
    }
    Write-Output 'ASSISTANT_ENTRY_STARTS_ENABLED_WIDGETS_OK'

    Start-Client $assistant '--chat'
    Wait-For { [SuiteWindows]::Titles($assistantId) -contains 'Little Tools AI' } 'Chat command did not show the assistant.'
    Start-Client $manager '--translate'
    Start-Client $assistant '--background'
    Start-Sleep -Seconds 4
    Assert-True (@(Get-SuiteProcess 'LittleTools').Count -eq 1 -and (Get-SuiteProcess 'LittleTools').Id -eq $managerId) 'Duplicate manager instance.'
    Assert-True (@(Get-SuiteProcess 'LittleTools.Assistant').Count -eq 1 -and (Get-SuiteProcess 'LittleTools.Assistant').Id -eq $assistantId) 'Duplicate assistant instance or watchdog loop.'
    Stop-Suite
    Write-Output 'COMMAND_FORWARDING_SINGLE_INSTANCE_AND_SUITE_EXIT_OK'

    $standalone = Start-Process -FilePath $assistant -ArgumentList '--managed --background' -WindowStyle Hidden -PassThru
    $existingId = $standalone.Id
    $standalone.Dispose()
    Wait-For { (Send-Pipe 'LittleTools.Assistant.Command.v1') -eq $existingId } 'Assistant did not start for adoption test.'
    $hostProcess = Start-Process -FilePath $manager -ArgumentList '--background' -WindowStyle Hidden -PassThru
    $hostProcess.Dispose()
    Wait-For { (Send-Pipe 'LittleTools.Manager.Command.v1') -gt 0 } 'Manager did not start for adoption test.'
    Start-Sleep -Seconds 4
    Assert-True (@(Get-SuiteProcess 'LittleTools.Assistant').Count -eq 1 -and (Get-SuiteProcess 'LittleTools.Assistant').Id -eq $existingId) 'Manager did not adopt the existing assistant.'
    Send-Pipe 'LittleTools.Assistant.Command.v1' 'Exit' | Out-Null
    Wait-For { $current = @(Get-SuiteProcess 'LittleTools.Assistant'); $current.Count -eq 1 -and $current[0].Id -ne $existingId -and (Send-Pipe 'LittleTools.Assistant.Command.v1') -eq $current[0].Id } 'Watchdog did not recover the assistant.'
    Write-Output 'EXISTING_ASSISTANT_ADOPTION_AND_RECOVERY_OK'
} finally { Stop-Suite }

Write-Output 'SUITE_INTEGRATION_SMOKE_OK'
