<#
.SYNOPSIS
    开发期启动 StreamPilot（含 SDK 检查与桥接健康检查）。

.EXAMPLE
    pwsh -File build/run-dev.ps1
#>
[CmdletBinding()]
param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\StreamPilot.App\StreamPilot.App.csproj'

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host '未找到 dotnet。请安装 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0' -ForegroundColor Red
    exit 1
}

$sdks = & dotnet --list-sdks 2>$null
$hasTen = $false
foreach ($line in $sdks) {
    if ($line -match '^10\.') { $hasTen = $true }
}

if (-not $hasTen) {
    Write-Host '未安装 .NET 10 SDK。已安装：' -ForegroundColor Red
    $sdks | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host '请安装：https://dotnet.microsoft.com/download/dotnet/10.0' -ForegroundColor Red
    exit 1
}

if (-not (Test-Path (Join-Path $root 'Web\player.html'))) {
    Write-Host '警告：未找到 Web\player.html，播放页将无法加载。' -ForegroundColor Yellow
}

$arguments = @('run', '--project', $project, '-c', 'Debug')
if ($NoBuild) {
    $arguments += '--no-build'
}

Write-Host '启动 StreamPilot（关闭窗口即退出）…' -ForegroundColor Cyan
& $dotnet.Source @arguments
exit $LASTEXITCODE
