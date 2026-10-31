param([string]$ManagerPath)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $ManagerPath) { $ManagerPath = Join-Path $root 'LittleTools\bin\LittleTools.exe' }
Add-Type -AssemblyName System.Web.Extensions
Add-Type -AssemblyName System.Windows.Forms
$assembly = [Reflection.Assembly]::LoadFrom($ManagerPath)
$settingsType = $assembly.GetType('LittleTools.Manager.ManagerSettings', $true)
$serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$settings = $serializer.Deserialize('{"MonitorEnabled":false,"TranslateEnabled":false,"TodoNotesEnabled":true}', $settingsType)
if ($settings.AutoClickIntervalMilliseconds -ne 100 -or $settings.MonitorEnabled -ne $false -or $settings.TranslateEnabled -ne $false) {
    throw 'Existing settings migration failed.'
}
$settings.AutoClickIntervalMilliseconds = 125
$testDir = Join-Path $root '.artifacts\auto-click-speed-test'
New-Item -ItemType Directory -Path $testDir -Force | Out-Null
$testConfig = Join-Path $testDir 'manager.json'
[IO.File]::WriteAllText($testConfig, $serializer.Serialize($settings))
$settings = $serializer.Deserialize([IO.File]::ReadAllText($testConfig), $settingsType)
if ($settings.AutoClickIntervalMilliseconds -ne 125 -or $settings.MonitorEnabled -ne $false -or $settings.TranslateEnabled -ne $false -or $settings.TodoNotesEnabled -ne $true) {
    throw 'Speed persistence changed other module settings.'
}
'SPEED_SETTINGS_LEGACY_AND_ROUNDTRIP_OK'

# Exercise menu updates without starting widgets or touching the user's configuration.
$hostType = $assembly.GetType('LittleTools.Manager.ManagerHost', $true)
$hostInstance = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($hostType)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$clickMenu = New-Object Windows.Forms.ToolStripMenuItem
$speedMenu = New-Object Windows.Forms.ToolStripMenuItem
foreach ($interval in @(500,200,100,50,20)) {
    $item = New-Object Windows.Forms.ToolStripMenuItem
    $item.Tag = $interval
    $speedMenu.DropDownItems.Add($item) | Out-Null
}
$hostType.GetField('settings',$flags).SetValue($hostInstance,$settings)
$hostType.GetField('autoClickItem',$flags).SetValue($hostInstance,$clickMenu)
$hostType.GetField('autoClickSpeedItem',$flags).SetValue($hostInstance,$speedMenu)
try {
    $refresh = $hostType.GetMethod('RefreshAutoClickSpeedMenu',$flags)
    $settings.AutoClickIntervalMilliseconds = 100
    $refresh.Invoke($hostInstance,@()) | Out-Null
    if (@($speedMenu.DropDownItems | Where-Object Checked).Count -ne 1 -or -not $speedMenu.DropDownItems[2].Checked) {
        throw 'Default menu selection failed.'
    }
    $settings.AutoClickIntervalMilliseconds = 20
    $refresh.Invoke($hostInstance,@()) | Out-Null
    if (@($speedMenu.DropDownItems | Where-Object Checked).Count -ne 1 -or -not $speedMenu.DropDownItems[4].Checked -or $speedMenu.Text -notmatch '20') {
        throw 'Menu speed selection failed.'
    }
    $settings.AutoClickIntervalMilliseconds = 125
    $refresh.Invoke($hostInstance,@()) | Out-Null
    if (@($speedMenu.DropDownItems | Where-Object Checked).Count -ne 0 -or $speedMenu.Text -notmatch '125' -or $clickMenu.ToolTipText -notmatch '8') {
        throw 'Custom speed menu description failed.'
    }
    'SPEED_MENU_SELECTION_AND_CUSTOM_LABEL_OK'
} finally { $clickMenu.Dispose(); $speedMenu.Dispose() }
