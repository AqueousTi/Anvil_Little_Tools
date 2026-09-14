$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

function Assert-True([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root 'AIUsageMonitor\bin\AIUsageMonitor.exe'))
$settingsStore = $assembly.GetType('AIUsageMonitor.ProviderSettingsStore', $true)
$static = [Reflection.BindingFlags]'Static,Public,NonPublic'
$encrypted = $settingsStore.GetMethod('Protect', $static).Invoke($null, @('smoke-secret'))
$resolved = $settingsStore.GetMethod('ResolveKey', $static).Invoke($null, @('Manual', '', $encrypted))
Assert-True ($resolved -eq 'smoke-secret') 'DPAPI provider key round-trip failed.'
Assert-True ($encrypted -notmatch 'smoke-secret') 'Provider key was not encrypted.'

$glmProvider = $assembly.GetType('AIUsageMonitor.GlmProvider', $true)
$glmJson = '{"code":200,"data":{"level":"pro","limits":[{"type":"TOKENS_LIMIT","unit":3,"number":5,"usage":1000000,"currentValue":245000,"percentage":24.5,"nextResetTime":1893456000000},{"type":"TOKENS_LIMIT","unit":6,"number":1,"usage":7000000,"currentValue":840000,"percentage":12,"nextResetTime":1893888000000},{"type":"TIME_LIMIT","unit":5,"number":1,"usage":1000,"currentValue":30,"percentage":3,"nextResetTime":1896048000000}]}}'
$glmUsage = $glmProvider.GetMethod('Parse', $static).Invoke($null, @($glmJson))
$glmUsageType = $glmUsage.GetType()
Assert-True ($glmUsageType.GetField('Level').GetValue($glmUsage) -eq 'pro') 'GLM plan level was not parsed.'
$fiveHour = $glmUsageType.GetField('FiveHour').GetValue($glmUsage)
$weekly = $glmUsageType.GetField('Weekly').GetValue($glmUsage)
$monthly = $glmUsageType.GetField('Monthly').GetValue($glmUsage)
$rateType = $fiveHour.GetType()
Assert-True ([bool]$glmUsageType.GetField('QuotaRead').GetValue($glmUsage)) 'GLM quota response was not marked readable.'
Assert-True ([Math]::Abs([double]$rateType.GetField('UsedPercent').GetValue($fiveHour) - 24.5) -lt 0.001) 'GLM 5h quota was not parsed.'
Assert-True ([double]$rateType.GetField('UsedPercent').GetValue($weekly) -eq 12) 'GLM weekly quota was not parsed.'
Assert-True ([double]$rateType.GetField('UsedPercent').GetValue($monthly) -eq 3) 'GLM monthly MCP quota was not parsed.'
Assert-True ($null -ne $rateType.GetField('ResetsAt').GetValue($fiveHour)) 'GLM quota reset time was not parsed.'

$httpType = $assembly.GetType('AIUsageMonitor.GlmProvider+HttpResult', $true)
$httpResult = [Activator]::CreateInstance($httpType, $true)
$httpType.GetField('StatusCode').SetValue($httpResult, 200)
$httpType.GetField('Body').SetValue($httpResult, '{"code":200,"success":true,"data":{"balance":66.5,"availableBalance":66.5,"totalSpendAmount":3.5}}')
$reportWallet = $glmProvider.GetMethod('ParseReportWallet', $static).Invoke($null, @($httpResult))
$walletType = $reportWallet.GetType()
$yen = [string][char]0x00A5
Assert-True ([double]$walletType.GetField('Balance').GetValue($reportWallet) -eq 66.5) 'GLM report balance was not parsed.'
Assert-True ([double]$walletType.GetField('TotalSpend').GetValue($reportWallet) -eq 3.5) 'GLM report spend was not parsed.'
Assert-True ($walletType.GetField('CurrencySymbol').GetValue($reportWallet) -eq $yen) 'GLM report currency fallback is wrong.'

$httpType.GetField('Body').SetValue($httpResult, '{"data":{"total_balance":"100","available_balance":"64","currency":"CNY"}}')
$v4Wallet = $glmProvider.GetMethod('ParseV4Wallet', $static).Invoke($null, @($httpResult))
Assert-True ([double]$walletType.GetField('Balance').GetValue($v4Wallet) -eq 64) 'GLM v4 available balance was not parsed.'
Assert-True ([double]$walletType.GetField('WalletTotal').GetValue($v4Wallet) -eq 100) 'GLM v4 total balance was not parsed.'

$glmTrackerType = $assembly.GetType('AIUsageMonitor.GlmUsageTracker', $true)
$glmSampleType = $assembly.GetType('AIUsageMonitor.GlmSpendSample', $true)
$sampleListType = [Collections.Generic.List``1].MakeGenericType($glmSampleType)
$sampleList = [Activator]::CreateInstance($sampleListType)
$baselineSample = [Activator]::CreateInstance($glmSampleType)
$glmSampleType.GetField('Timestamp').SetValue($baselineSample, [datetime]'2026-09-01T23:50:00')
$glmSampleType.GetField('Balance').SetValue($baselineSample, [double]92)
$glmSampleType.GetField('TotalSpend').SetValue($baselineSample, [double]8)
$sampleList.Add($baselineSample)
$sampleTime = [datetime]'2026-09-02T09:00:00'
foreach ($value in @(10.0, 12.5, 12.0)) {
    $sample = [Activator]::CreateInstance($glmSampleType)
    $glmSampleType.GetField('Timestamp').SetValue($sample, $sampleTime)
    $glmSampleType.GetField('Balance').SetValue($sample, [double](100 - $value))
    $glmSampleType.GetField('TotalSpend').SetValue($sample, [double]$value)
    $sampleList.Add($sample)
    $sampleTime = $sampleTime.AddHours(1)
}
$rangeArgs = [object[]]::new(4)
$rangeArgs[0] = $sampleList; $rangeArgs[1] = [datetime]'2026-09-02'; $rangeArgs[2] = $null; $rangeArgs[3] = $null
$glmPoints = $glmTrackerType.GetMethod('BuildRange', $static).Invoke($null, $rangeArgs)
Assert-True ([Math]::Abs([double]$rangeArgs[2] - 4.5) -lt 0.001) 'GLM prior-period baseline was not included.'
Assert-True ($glmPoints.Count -eq 4 -and [double]$glmPoints[3].Value -eq 4.5) 'GLM spend trend points are wrong.'
Assert-True ([datetime]$rangeArgs[3] -eq [datetime]'2026-09-02') 'GLM tracking start should use the natural-day boundary.'

$dailyTrackerType = $assembly.GetType('AIUsageMonitor.DailyUsageTracker', $true)
$balanceSampleType = $assembly.GetType('AIUsageMonitor.BalanceSample', $true)
$balanceListType = [Collections.Generic.List``1].MakeGenericType($balanceSampleType)
$balanceList = [Activator]::CreateInstance($balanceListType)
foreach ($item in @(
    @([datetime]'2026-09-01T23:50:00', 100.0),
    @([datetime]'2026-09-02T09:00:00', 98.0),
    @([datetime]'2026-09-02T10:00:00', 97.0)
)) {
    $sample = [Activator]::CreateInstance($balanceSampleType)
    $balanceSampleType.GetField('Timestamp').SetValue($sample, $item[0])
    $balanceSampleType.GetField('Balance').SetValue($sample, [double]$item[1])
    $balanceList.Add($sample)
}
$deepSeekRangeArgs = [object[]]::new(4)
$deepSeekRangeArgs[0] = $balanceList; $deepSeekRangeArgs[1] = [datetime]'2026-09-02'; $deepSeekRangeArgs[2] = $null; $deepSeekRangeArgs[3] = $null
$deepSeekPoints = $dailyTrackerType.GetMethod('BuildRange', $static).Invoke($null, $deepSeekRangeArgs)
Assert-True ([Math]::Abs([double]$deepSeekRangeArgs[2] - 3) -lt 0.001) 'DeepSeek prior-period baseline was not included.'
Assert-True ($deepSeekPoints.Count -eq 3 -and [double]$deepSeekPoints[2].Value -eq 3) 'DeepSeek spend trend points are wrong.'
Assert-True ([datetime]$deepSeekRangeArgs[3] -eq [datetime]'2026-09-02') 'DeepSeek tracking start should use the natural-day boundary.'

$snapshotType = $assembly.GetType('AIUsageMonitor.UsageSnapshot', $true)
$windowType = $assembly.GetType('AIUsageMonitor.MonitorWindow', $true)
$snapshot = [Activator]::CreateInstance($snapshotType)
$window = [Activator]::CreateInstance($windowType, @($snapshot))
$instance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
try {
    Assert-True ($window.Width -eq 316 -and $window.Height -eq 92) 'Usage compact window must remain 316 x 92.'
    Assert-True ($null -ne $windowType.GetField('glmText', $instance).GetValue($window)) 'GLM compact row is missing.'
    $snapshotType.GetField('CodexEnabled').SetValue($snapshot, $true)
    $snapshotType.GetField('DeepSeekEnabled').SetValue($snapshot, $false)
    $snapshotType.GetField('GlmEnabled').SetValue($snapshot, $false)
    $window.UpdateView($snapshot)
    $deepText = $windowType.GetField('deepSeekText', $instance).GetValue($window)
    $glmText = $windowType.GetField('glmText', $instance).GetValue($window)
    Assert-True ($deepText.Parent.Visibility.ToString() -eq 'Collapsed' -and $glmText.Parent.Visibility.ToString() -eq 'Collapsed') 'Disabled compact providers are still visible.'
    $snapshotType.GetField('GlmEnabled').SetValue($snapshot, $true)
    $snapshotType.GetField('GlmState').SetValue($snapshot, '正常')
    $snapshotType.GetField('GlmBalance').SetValue($snapshot, [double]66.5)
    $snapshotType.GetField('GlmCurrencySymbol').SetValue($snapshot, $yen)
    $snapshotType.GetField('TodayGlmSpend').SetValue($snapshot, [double]1.25)
    $snapshotType.GetField('GlmTrackingStart').SetValue($snapshot, [datetime]::Now)
    $snapshotType.GetField('GlmFiveHourRemaining').SetValue($snapshot, [double]75.5)
    $snapshotType.GetField('GlmWeeklyRemaining').SetValue($snapshot, [double]88)
    $window.UpdateView($snapshot)
    $compactChecks = @(
        [bool]($glmText.Text -match ([regex]::Escape($yen + '66.5'))),
        [bool]($glmText.Text.Contains('1.25')),
        [bool]($glmText.Text.Contains(([char]0x4ECA).ToString() + ([char]0x7EA6).ToString())),
        [bool]($glmText.Text -match '5h 76%'),
        [bool]($glmText.Text -match '周 88%')
    )
    Assert-True (-not ($compactChecks -contains $false)) ("GLM compact spend, balance, and quotas are not displayed: " + ($compactChecks -join ',') + ' / ' + $glmText.Text)
    $window.SetEdgeHideEnabled($false)
    $detailsType = $assembly.GetType('AIUsageMonitor.DetailsWindow', $true)
    $details = [Activator]::CreateInstance($detailsType)
    try {
        $details.UpdateView($snapshot)
        $todayGlm = $detailsType.GetField('todayGlm', $instance).GetValue($details)
        $trendTitle = $detailsType.GetField('trendTitle', $instance).GetValue($details)
        $glmTrend = $detailsType.GetField('glmTrend', $instance).GetValue($details)
        $deepSeekTrend = $detailsType.GetField('deepSeekTrend', $instance).GetValue($details)
        Assert-True ($todayGlm.Text -match ([regex]::Escape($yen + '1.25'))) 'GLM today spend is not displayed.'
        Assert-True ($trendTitle.Text -match 'GLM' -and $glmTrend.Visibility.ToString() -eq 'Visible' -and $deepSeekTrend.Visibility.ToString() -eq 'Collapsed') 'GLM trend was not selected when it is the only enabled provider.'
        $snapshotType.GetField('GlmEnabled').SetValue($snapshot, $false)
        $details.UpdateView($snapshot)
        Assert-True ($details.Height -lt 300) 'Disabled expanded provider sections were not collapsed.'
    }
    finally { $details.Close() }
}
finally { $window.Close() }

Write-Output 'USAGE_WINDOW_SMOKE_OK'
