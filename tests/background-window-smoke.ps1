$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
public static class BackgroundWindowProbe {
    private delegate bool Callback(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int length);
    public static string[] Check(int pid) {
        var results = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            uint owner; GetWindowThreadProcessId(h, out owner);
            if (owner != pid || !IsWindowVisible(h)) return true;
            var title = new StringBuilder(512); GetWindowText(h, title, title.Capacity);
            int style = GetWindowLong(h, -20);
            if ((style & 0x80) == 0 || (style & 0x40000) != 0)
                throw new InvalidOperationException("Window is not excluded from task switching: " + title);
            results.Add(title.ToString());
            return true;
        }, IntPtr.Zero);
        return results.ToArray();
    }
}
'@
$manager = Get-Process LittleTools -ErrorAction Stop
$windows = @([BackgroundWindowProbe]::Check($manager.Id))
if ($windows.Count -eq 0) { throw 'No visible widgets were checked.' }
$windows | ForEach-Object { "TOOL_WINDOW_OK=$_" }
$run = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').'Little Tools'
if ($run -notmatch 'LittleTools\.exe" --startup$') { throw "Unexpected startup command: $run" }
if (Get-Process LittleToolsStartup -ErrorAction SilentlyContinue) { throw 'Startup launcher is still running.' }
'DIRECT_STARTUP_AND_BACKGROUND_WINDOWS_OK'
