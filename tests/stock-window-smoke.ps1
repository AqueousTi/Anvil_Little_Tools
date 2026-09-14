$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $repoRoot 'StockMonitor\bin\StockMonitor.exe'))
$type = $assembly.GetType('LittleTools.StockMonitor.StockWindow', $true)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$window = [Activator]::CreateInstance($type)

try {
    Assert-True ($window.Width -eq 316 -and $window.Height -eq 92) 'Stock compact window must be 316 x 92.'
    $shell = $type.GetField('shell', $flags).GetValue($window)
    Assert-True ($shell.Background.Color.A -eq 62) 'Stock compact transparency does not match usage monitor.'

    $window.Show()
    $type.GetMethod('ToggleDetails', $flags).Invoke($window, @())
    $details = $type.GetField('detailsWindow', $flags).GetValue($window)
    Assert-True ($null -ne $details -and $details.IsVisible) 'Stock details did not open.'
    Assert-True ($details.Width -eq 470 -and $details.Height -eq 650) 'Stock details size is incorrect.'
    Assert-True (-not $details.ShowInTaskbar) 'Stock details should not create a taskbar item.'

    $outlinedType = $assembly.GetType('LittleTools.StockMonitor.OutlinedValueText', $true)
    $outlined = [Activator]::CreateInstance($outlinedType)
    $premiumStyle = $type.GetMethod('ApplyPremiumStyle', [Reflection.BindingFlags]'Static,NonPublic')
    $premiumStyle.Invoke($null, @($outlined, [Nullable[double]]1.99)) | Out-Null
    Assert-True ($outlined.OutlineEnabled) 'Low premium outline was not enabled.'
    $premiumStyle.Invoke($null, @($outlined, [Nullable[double]]2.00)) | Out-Null
    Assert-True (-not $outlined.OutlineEnabled -and $outlined.Foreground.Color.R -eq 165) 'Normal premium should use gray text.'

    $changeBrush = $type.GetMethod('ChangeBrush', [Reflection.BindingFlags]'Static,NonPublic')
    $up = $changeBrush.Invoke($null, @(1.0)).Color
    $down = $changeBrush.Invoke($null, @(-1.0)).Color
    Assert-True ($up.R -gt $up.G -and $down.G -gt $down.R) 'Stock price colors must be red-up and green-down.'

    $type.GetMethod('ToggleDetails', $flags).Invoke($window, @())
    Assert-True ($null -eq $type.GetField('detailsWindow', $flags).GetValue($window)) 'Stock details did not close.'
}
finally {
    $type.GetMethod('ClosePermanently', [Reflection.BindingFlags]'Instance,Public').Invoke($window, @())
}

Write-Output 'STOCK_WINDOW_SMOKE_OK'
