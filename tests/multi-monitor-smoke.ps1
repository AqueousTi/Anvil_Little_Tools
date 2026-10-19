param([string]$AssemblyPath)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $arguments = @('-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', $MyInvocation.MyCommand.Path)
    if ($AssemblyPath) { $arguments += @('-AssemblyPath', $AssemblyPath) }
    & powershell.exe @arguments
    exit $LASTEXITCODE
}
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Windows.Forms
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $AssemblyPath) { $AssemblyPath = Join-Path $root 'LittleTools\bin\LittleTools.exe' }
$assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
$instance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$static = [Reflection.BindingFlags]'Static,Public,NonPublic'
$screensType = $assembly.GetType('LittleTools.Common.DesktopScreens', $true)
$workArea = $screensType.GetMethod('GetWorkingArea', $static)
$onScreen = $screensType.GetMethod('IsOnScreen', $static)
$testRoot = Join-Path $root ('.artifacts\multi-monitor-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

function Assert-Near([double]$actual, [double]$expected, [string]$message) {
    if ([math]::Abs($actual - $expected) -gt 1) { throw "$message Actual=$actual Expected=$expected" }
}
function Invoke-Window($window, [string]$method) {
    $window.GetType().GetMethod($method, $instance).Invoke($window, @()) | Out-Null
}
function New-IsolatedWindow([string]$windowName, [string]$storeName, [string[]]$pathFields) {
    $storeType = $assembly.GetType($storeName, $true)
    $store = [Activator]::CreateInstance($storeType)
    foreach ($field in $pathFields) {
        $storeType.GetField($field, $instance).SetValue($store, (Join-Path $testRoot ($storeType.Name + '-' + $field + '.json')))
    }
    $type = $assembly.GetType($windowName, $true)
    $constructor = $type.GetConstructor($instance, $null, [type[]]@($storeType), $null)
    return $constructor.Invoke(@($store))
}
function Test-Widget($window, [string]$detailsField, [string]$detailsTypeName, [int]$strip) {
    $type = $window.GetType()
    # Create the rendering target without showing a window or starting network refreshes.
    $handle = (New-Object System.Windows.Interop.WindowInteropHelper($window)).EnsureHandle()
    $transform = [System.Windows.Interop.HwndSource]::FromHwnd($handle).CompositionTarget.TransformFromDevice
    foreach ($screen in [System.Windows.Forms.Screen]::AllScreens) {
        $area = $screen.WorkingArea
        $start = $transform.Transform([System.Windows.Point]::new($area.Left, $area.Top))
        $end = $transform.Transform([System.Windows.Point]::new($area.Right, $area.Bottom))
        $window.SetEdgeHideEnabled($false)
        $window.Left = $start.X + 80
        $window.Top = $start.Y + 80
        Invoke-Window $window 'SnapOrHideAtEdge'
        Assert-Near $window.Left ($start.X + 80) ($type.Name + ' jumped to a different monitor.')
        Assert-Near $window.Top ($start.Y + 80) ($type.Name + ' changed its vertical position.')
        $work = $workArea.Invoke($null, @($window))
        Assert-Near $work.Left $start.X 'Monitor working area has incorrect DPI or negative coordinates.'
        Assert-Near $work.Bottom $end.Y 'Monitor working area does not respect the taskbar.'
        if (-not $onScreen.Invoke($null, @($window, $window.Left, $window.Top, $window.Width, $window.Height))) {
            throw 'A visible secondary-monitor position was rejected.'
        }
        if ($detailsField) {
            $detailsType = if ($detailsTypeName) { $assembly.GetType($detailsTypeName, $true) } else { [System.Windows.Window] }
            $details = [Activator]::CreateInstance($detailsType)
            $details.Width = 470; $details.Height = 650
            try {
                $type.GetField($detailsField, $instance).SetValue($window, $details)
                Invoke-Window $window 'PositionDetails'
                if ($details.Left -lt $start.X - 1 -or $details.Left + $details.Width -gt $end.X + 1 -or
                    $details.Top -lt $start.Y - 1 -or $details.Top + $details.Height -gt $end.Y + 1) {
                    throw ($type.Name + ' details escaped the owner monitor.')
                }
            } finally {
                $type.GetField($detailsField, $instance).SetValue($window, $null)
                $details.Close()
            }
        } else {
            $window.Width = 430; $window.Height = 530
            $window.Left = $end.X - 330; $window.Top = $end.Y - 100
            Invoke-Window $window 'KeepOnScreen'
            Assert-Near $window.Left ($end.X - 430) 'Expanded todo escaped its monitor.'
            Assert-Near $window.Top ($end.Y - 530) 'Expanded todo escaped its work area.'
            $window.Width = 316; $window.Height = 92
        }
        foreach ($edge in @(-1, 1)) {
            $window.SetEdgeHideEnabled($false)
            $window.Left = if ($edge -lt 0) { $start.X + 1 } else { $end.X - $window.Width - 1 }
            $window.Top = $start.Y + 80
            $window.SetEdgeHideEnabled($true)
            Invoke-Window $window 'SnapOrHideAtEdge'
            $hiddenLeft = if ($edge -lt 0) { $start.X - $window.Width + $strip } else { $end.X - $strip }
            Assert-Near $window.Left $hiddenLeft 'Edge hiding used the wrong monitor.'
            # On a shared edge most of the hidden window lies on the neighboring monitor.
            Invoke-Window $window 'HideToEdge'
            Assert-Near $window.Left $hiddenLeft 'Repeated edge hiding switched monitors.'
            Invoke-Window $window 'RevealFromEdge'
            $revealed = if ($edge -lt 0) { $start.X } else { $end.X - $window.Width }
            Assert-Near $window.Left $revealed 'Edge reveal used the wrong monitor.'
        }
        $window.SetEdgeHideEnabled($false)
        $window.Left = $start.X + 80; $window.Top = $start.Y + 80
        Write-Output ('MONITOR_OK=' + $type.Name + ' ' + $screen.DeviceName)
    }
}

$windows = @()
try {
    $todo = New-IsolatedWindow 'LittleTools.DailyTodo.DailyTodoWindow' 'LittleTools.DailyTodo.TodoStore' @('dataPath', 'backupPath')
    $windows += $todo
    $snapshot = [Activator]::CreateInstance($assembly.GetType('AIUsageMonitor.UsageSnapshot', $true))
    $usage = [Activator]::CreateInstance($assembly.GetType('AIUsageMonitor.MonitorWindow', $true), @($snapshot))
    $windows += $usage
    $stock = New-IsolatedWindow 'LittleTools.StockMonitor.StockWindow' 'LittleTools.StockMonitor.StockStore' @('path')
    $windows += $stock
    Test-Widget $todo '' '' 10
    Test-Widget $usage 'details' 'AIUsageMonitor.DetailsWindow' 9
    Test-Widget $stock 'detailsWindow' '' 9
    $restored = New-IsolatedWindow 'LittleTools.StockMonitor.StockWindow' 'LittleTools.StockMonitor.StockStore' @('path')
    $windows += $restored
    Assert-Near $restored.Left $stock.Left 'Stock position on the secondary monitor was not restored.'
    Assert-Near $restored.Top $stock.Top 'Stock vertical position was not restored.'

    $bounds = $screensType.GetMethod('ToDeviceBounds', $static).Invoke($null, @(
        [double]-1000, [double]-200, [double]316, [double]92, [System.Windows.Media.Matrix]::new(1.5, 0, 0, 1.5, 0, 0)))
    Assert-Near $bounds.Left -1500 'Negative coordinates were lost at 150% DPI.'
    Assert-Near $bounds.Width 474 'Widget dimensions were not converted to device pixels.'
    if ($onScreen.Invoke($null, @($usage, [double]::NaN, [double]0, [double]316, [double]92))) {
        throw 'Invalid saved coordinates were accepted.'
    }
    'MULTI_MONITOR_SMOKE_OK'
} finally {
    foreach ($window in $windows) {
        if ($window.GetType().Name -eq 'StockWindow') { $window.ClosePermanently() } else { $window.Close() }
    }
    # Keep isolated test files under .artifacts; no personal settings are touched.
}
