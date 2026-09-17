<#
.SYNOPSIS
    把 StreamPilot 打包为自包含发布产物，输出到 StreamPilot_publish。

.DESCRIPTION
    流程：SDK 检查 → 测试 → dotnet publish（单文件自包含）→ 复制 Web/ 与文档 → 生成 VERSION.txt。
    源码目录不落任何产物；所有产物只写入 -OutputDirectory 指定的目录。

.PARAMETER OutputDirectory
    产物目录，默认 D:\文件\实用软件\b站插件\StreamPilot_publish。

.PARAMETER SkipTests
    跳过测试（不建议；仅在明确知道风险时使用）。

.EXAMPLE
    pwsh -File build/publish.ps1
    pwsh -File build/publish.ps1 -OutputDirectory D:\temp\out
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'D:\文件\实用软件\b站插件\StreamPilot_publish',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\StreamPilot.App\StreamPilot.App.csproj'

Write-Host '==== 1/5 检查 .NET 10 SDK ====' -ForegroundColor Cyan
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
    Write-Host '未安装 .NET 10 SDK，无法发布。已安装：' -ForegroundColor Red
    $sdks | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host '请安装：https://dotnet.microsoft.com/download/dotnet/10.0' -ForegroundColor Red
    exit 1
}

if (-not $SkipTests) {
    Write-Host '==== 2/5 运行测试 ====' -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test.ps1')
    if ($LASTEXITCODE -ne 0) {
        Write-Host '测试未通过，已中止发布。' -ForegroundColor Red
        exit 1
    }
}
else {
    Write-Host '==== 2/5 已按要求跳过测试 ====' -ForegroundColor Yellow
}

Write-Host '==== 3/5 发布单文件自包含产物 ====' -ForegroundColor Cyan
if (Test-Path $OutputDirectory) {
    Write-Host "清理旧产物：$OutputDirectory"
    Get-ChildItem -Path $OutputDirectory -Force | Remove-Item -Recurse -Force
}
else {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

& $dotnet.Source publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $OutputDirectory

if ($LASTEXITCODE -ne 0) {
    Write-Host 'dotnet publish 失败。' -ForegroundColor Red
    exit 1
}

Write-Host '==== 4/5 组装 Web 资源与文档 ====' -ForegroundColor Cyan
$webSource = Join-Path $root 'Web'
$webTarget = Join-Path $OutputDirectory 'Web'
New-Item -ItemType Directory -Path $webTarget -Force | Out-Null
Copy-Item -Path (Join-Path $webSource '*') -Destination $webTarget -Recurse -Force

$docsTarget = Join-Path $OutputDirectory 'docs'
New-Item -ItemType Directory -Path $docsTarget -Force | Out-Null
Copy-Item -Path (Join-Path $root 'docs\*') -Destination $docsTarget -Recurse -Force
Copy-Item -Path (Join-Path $root 'README.md') -Destination $OutputDirectory -Force
Copy-Item -Path (Join-Path $root 'LICENSE') -Destination $OutputDirectory -Force
Copy-Item -Path (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $OutputDirectory -Force

$toolsTarget = Join-Path $OutputDirectory 'tools'
New-Item -ItemType Directory -Path $toolsTarget -Force | Out-Null
Copy-Item -Path (Join-Path $root 'tools\README.md') -Destination $toolsTarget -Force

Write-Host '==== 5/5 生成 VERSION.txt ====' -ForegroundColor Cyan
$exe = Join-Path $OutputDirectory 'StreamPilot.exe'
if (-not (Test-Path $exe)) {
    Write-Host "未找到发布产物 $exe，发布失败。" -ForegroundColor Red
    exit 1
}

$exeHash = (Get-FileHash -Path $exe -Algorithm SHA256).Hash
$gitDescribe = 'unknown'
Push-Location $root
try {
    $describe = & git describe --tags --always --dirty 2>$null
    if ($LASTEXITCODE -eq 0 -and $describe) { $gitDescribe = $describe }
}
finally {
    Pop-Location
}

$versionLines = @()
$versionLines += "product=StreamPilot"
$versionLines += "version=0.1.0"
$versionLines += "buildTime=$((Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz'))"
$versionLines += "gitDescribe=$gitDescribe"
$versionLines += "target=win-x64 / net10.0-windows / self-contained single-file"
$versionLines += "exeSha256=$exeHash"
$versionLines += "webAssets=" + ((Get-ChildItem -Path $webTarget -File | ForEach-Object { "$($_.Name) ($($_.Length) bytes)" }) -join ', ')
$versionLines | Set-Content -Path (Join-Path $OutputDirectory 'VERSION.txt') -Encoding utf8

$totalBytes = (Get-ChildItem -Path $OutputDirectory -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ''
Write-Host '发布完成。' -ForegroundColor Green
Write-Host "输出目录：$OutputDirectory"
Write-Host ("总大小：{0:N1} MiB" -f ($totalBytes / 1MB))
Write-Host "StreamPilot.exe SHA256：$exeHash"
Write-Host ''
Write-Host '提示：tools\ 目录需要用户自行放入 mpv.exe（见 tools\README.md），仓库中不包含任何二进制。' -ForegroundColor Yellow
exit 0
