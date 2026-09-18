$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class HotkeyProbe {
[DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(IntPtr c,string t);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern void keybd_event(byte v,byte s,uint f,UIntPtr e);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
$root=Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$manager=Join-Path $root 'LittleTools\bin\LittleTools.exe'
$running=@(Get-Process -Name LittleTools,LittleTools.Assistant -ErrorAction SilentlyContinue)
$p=Start-Process $manager -ArgumentList '--exit' -WindowStyle Hidden -PassThru
if (-not $p.WaitForExit(10000)) { throw 'Exit timeout' }
foreach($instance in $running) { if (-not $instance.WaitForExit(10000)) { throw 'Suite still running' } }
Start-Process $manager -ArgumentList '--background' -WindowStyle Hidden
Start-Sleep -Seconds 3
$h=[HotkeyProbe]::FindWindow([IntPtr]::Zero,'Little Tools AI')
if ([HotkeyProbe]::IsWindowVisible($h)) { throw 'Background startup showed translation' }
if (Get-Process LittleTools.Assistant -ErrorAction SilentlyContinue) { throw 'Background startup eagerly launched the assistant.' }
'BACKGROUND_START_SINGLE_PROCESS_OK'
'BACKGROUND_START_HIDDEN_OK'
$f=New-Object Windows.Forms.Form
$f.Text='Little Tools shortcut test'; $f.Width=240; $f.Height=100
try {
$f.Show()
foreach($modifier in @(0x10,0x11,0x10)) {
if ($modifier -eq 0x11) {
    $assembly=[Reflection.Assembly]::LoadFrom($manager)
    $send=$assembly.GetType('LittleTools.Common.CommandPipe',$true).GetMethod('Send')
    $oldAssistant=Get-Process LittleTools.Assistant -ErrorAction Stop
    [void]$send.Invoke($null,@('LittleTools.Assistant.Command.v1','Exit',1000))
    if (-not $oldAssistant.WaitForExit(10000)) { throw 'Assistant did not exit for hotkey recovery test.' }
    Start-Sleep -Seconds 4
    if (Get-Process LittleTools.Assistant -ErrorAction SilentlyContinue) { throw 'Unused assistant was restarted.' }
}
[void][HotkeyProbe]::SetForegroundWindow($f.Handle)
[Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 250
[HotkeyProbe]::keybd_event([byte]$modifier,0,0,[UIntPtr]::Zero)
[HotkeyProbe]::keybd_event(8,0,0,[UIntPtr]::Zero)
[HotkeyProbe]::keybd_event(8,0,2,[UIntPtr]::Zero)
[HotkeyProbe]::keybd_event([byte]$modifier,0,2,[UIntPtr]::Zero)
$timer=[Diagnostics.Stopwatch]::StartNew(); do { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100; $h=[HotkeyProbe]::FindWindow([IntPtr]::Zero,'Little Tools AI') } while (-not [HotkeyProbe]::IsWindowVisible($h) -and $timer.ElapsedMilliseconds -lt 5000); "WAKE_MS=$($timer.ElapsedMilliseconds)"
$h=[HotkeyProbe]::FindWindow([IntPtr]::Zero,'Little Tools AI')
if (-not [HotkeyProbe]::IsWindowVisible($h)) { throw "Shortcut failed: modifier $modifier" }
"SHORTCUT_MODIFIER_$($modifier)_OK"
[HotkeyProbe]::keybd_event(27,0,0,[UIntPtr]::Zero)
[HotkeyProbe]::keybd_event(27,0,2,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 250
if ([HotkeyProbe]::IsWindowVisible($h)) { throw 'Escape did not hide between hotkey attempts.' }
}
[HotkeyProbe]::keybd_event(27,0,0,[UIntPtr]::Zero)
[HotkeyProbe]::keybd_event(27,0,2,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 200
if ([HotkeyProbe]::IsWindowVisible($h)) { throw 'Escape did not hide' }
'ESCAPE_HIDE_OK'
} finally { $f.Close(); $f.Dispose() }
'BACKGROUND_AND_HOTKEYS_OK'
