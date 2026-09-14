$ErrorActionPreference = 'Stop'

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$todoAssemblyPath = Join-Path $repoRoot 'TodoNotes\bin\TodoNotes.exe'
Assert-True (Test-Path -LiteralPath $todoAssemblyPath) 'TodoNotes.exe is missing.'

$assembly = [Reflection.Assembly]::LoadFrom($todoAssemblyPath)
$dataType = $assembly.GetType('LittleTools.DailyTodo.DailyTodoData', $true)
$ruleType = $assembly.GetType('LittleTools.DailyTodo.RecurringTodoRule', $true)
$engineType = $assembly.GetType('LittleTools.DailyTodo.RecurringTodoEngine', $true)
$itemType = $assembly.GetType('LittleTools.DailyTodo.DailyTodoItem', $true)
$data = [Activator]::CreateInstance($dataType)

function Add-Rule([string]$id, [string]$text, [string]$frequency, [int]$value) {
    $rule = [Activator]::CreateInstance($ruleType)
    $ruleType.GetField('Id').SetValue($rule, $id)
    $ruleType.GetField('Text').SetValue($rule, $text)
    $ruleType.GetField('Frequency').SetValue($rule, $frequency)
    $ruleType.GetField('ScheduleValue').SetValue($rule, $value)
    $ruleType.GetField('Enabled').SetValue($rule, $true)
    $ruleType.GetField('CreatedDate').SetValue($rule, '2026-08-01')
    $dataType.GetField('RecurringRules').GetValue($data).Add($rule)
}

Add-Rule 'daily' 'Daily task' 'Daily' 0
Add-Rule 'weekly' 'Saturday task' 'Weekly' 6
Add-Rule 'monthly' 'Month day task' 'Monthly' 29
$ensure = $engineType.GetMethod('Ensure')
$date = [datetime]'2026-08-29'
Assert-True ([bool]$ensure.Invoke($null, @($data, $date))) 'Recurring rules did not generate.'
Assert-True (-not [bool]$ensure.Invoke($null, @($data, $date))) 'Recurring rules generated duplicates.'
$days = $dataType.GetField('Days').GetValue($data)
$items = $days[0].GetType().GetField('Items').GetValue($days[0])
Assert-True ($items.Count -eq 3) 'Unexpected recurring item count.'

$dailyItem = $items | Where-Object { $itemType.GetField('RecurringRuleId').GetValue($_) -eq 'daily' } | Select-Object -First 1
$items.Remove($dailyItem) | Out-Null
$days[0].GetType().GetField('SuppressedRuleIds').GetValue($days[0]).Add('daily')
Assert-True (-not [bool]$ensure.Invoke($null, @($data, $date))) 'A suppressed rule regenerated.'

Add-Type -AssemblyName System.Web.Extensions
$serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$positionItem = [Activator]::CreateInstance($itemType)
$itemType.GetField('PreviousOpenIndex').SetValue($positionItem, 3)
$roundTrip = $serializer.Deserialize($serializer.Serialize($positionItem), $itemType)
Assert-True ($itemType.GetField('PreviousOpenIndex').GetValue($roundTrip) -eq 3) 'Priority position did not survive serialization.'

$focusType = $assembly.GetType('LittleTools.DailyTodo.FocusTimerData', $true)
$focus = [Activator]::CreateInstance($focusType)
$focusType.GetField('ItemId').SetValue($focus, 'focus-item')
$focusType.GetField('ItemText').SetValue($focus, 'Focused task')
$focusType.GetField('DurationMinutes').SetValue($focus, 25)
$focusType.GetField('EndsAtUtc').SetValue($focus, '2026-09-02T12:00:00.0000000Z')
$dataType.GetField('FocusTimer').SetValue($data, $focus)
$backlogItem = [Activator]::CreateInstance($itemType)
$itemType.GetField('Id').SetValue($backlogItem, 'backlog-item')
$itemType.GetField('Text').SetValue($backlogItem, 'Deferred task')
$itemType.GetField('BacklogSourceDate').SetValue($backlogItem, '2026-09-01')
$dataType.GetField('BacklogItems').GetValue($data).Add($backlogItem)
$dataRoundTrip = $serializer.Deserialize($serializer.Serialize($data), $dataType)
$focusRoundTrip = $dataType.GetField('FocusTimer').GetValue($dataRoundTrip)
Assert-True ($focusType.GetField('DurationMinutes').GetValue($focusRoundTrip) -eq 25) 'Focus timer did not survive serialization.'
Assert-True ($focusType.GetField('ItemId').GetValue($focusRoundTrip) -eq 'focus-item') 'Focus timer task did not survive serialization.'
$backlogRoundTrip = $dataType.GetField('BacklogItems').GetValue($dataRoundTrip)
Assert-True ($backlogRoundTrip.Count -eq 1) 'Backlog item did not survive serialization.'
Assert-True ($itemType.GetField('BacklogSourceDate').GetValue($backlogRoundTrip[0]) -eq '2026-09-01') 'Backlog source date did not survive serialization.'

$atomicType = $assembly.GetType('LittleTools.Common.AtomicFile', $true)
$write = $atomicType.GetMethods([Reflection.BindingFlags]'Static,Public,NonPublic') |
    Where-Object { $_.Name -eq 'WriteUtf8' -and $_.GetParameters().Count -eq 3 } | Select-Object -First 1
$testRoot = Join-Path $repoRoot '.smoke-test'
$testRoot = [IO.Path]::GetFullPath($testRoot)
if ([IO.Directory]::GetParent($testRoot).FullName -ne [IO.Path]::GetFullPath($repoRoot)) { throw 'Test path validation failed.' }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
try {
    $target = [string](Join-Path $testRoot 'state.json')
    $backup = [string](Join-Path $testRoot 'state.backup.json')
    $arguments = [object[]]::new(3); $arguments[0] = $target; $arguments[2] = $backup
    $arguments[1] = [string]'first'; $write.Invoke($null, $arguments) | Out-Null
    $arguments[1] = [string]'second'; $write.Invoke($null, $arguments) | Out-Null
    Assert-True ((Get-Content -LiteralPath $target -Raw) -eq 'second') 'Atomic target content is wrong.'
    Assert-True ((Get-Content -LiteralPath $backup -Raw) -eq 'first') 'Atomic backup content is wrong.'
    Assert-True (-not (Test-Path -LiteralPath ($target + '.tmp'))) 'Atomic write left a temporary file.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

$parseFailures = @()
Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Filter '*.ps1' | ForEach-Object {
    $parseTokens = $null; $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$parseTokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) {
        $messages = ($parseErrors | ForEach-Object { $_.Message }) -join ' | '
        $parseFailures += ($_.FullName + ': ' + $messages)
    }
}
Assert-True ($parseFailures.Count -eq 0) ('PowerShell parse failures: ' + ($parseFailures -join ', '))

Write-Output 'SMOKE_TESTS_OK'
