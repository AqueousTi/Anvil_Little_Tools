$ErrorActionPreference = 'Stop'

function Read-PlainSecret([string]$prompt) {
    $secure = Read-Host $prompt -AsSecureString
    if ($secure.Length -eq 0) { return $null }
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

Write-Host '留空表示不修改该项。' -ForegroundColor Cyan
$deepSeekKey = Read-PlainSecret 'DeepSeek API Key'
if ($deepSeekKey) {
    [Environment]::SetEnvironmentVariable('DEEPSEEK_API_KEY', $deepSeekKey.Trim(), 'User')
    Write-Host 'DeepSeek API Key 已保存到当前 Windows 用户环境变量。' -ForegroundColor Green
}

$glmKey = Read-PlainSecret 'GLM API Key'
if ($glmKey) {
    [Environment]::SetEnvironmentVariable('ZHIPUAI_API_KEY', $glmKey.Trim(), 'User')
    Write-Host 'GLM API Key 已保存到当前 Windows 用户环境变量。' -ForegroundColor Green
}

$managerPath = Join-Path $env:LOCALAPPDATA 'LittleTools\App\LittleTools\bin\LittleTools.exe'
if (Test-Path -LiteralPath $managerPath) {
    Get-Process -Name 'LittleTools','LittleTools.Assistant' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Process -FilePath $managerPath -WindowStyle Hidden
    Write-Host 'Little Tools 已重启并载入新配置。' -ForegroundColor Green
}
