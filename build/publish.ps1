<#
.SYNOPSIS
    把 StreamPilot 打包为自包含发布产物，输出到 StreamPilot_publish。

.DESCRIPTION
    流程：SDK 检查 → 测试 → dotnet publish（单文件自包含）→ 复制 Web/ 与文档 → 生成 VERSION.txt
          → 压缩为 StreamPilot-windows-v<版本>.zip。
    源码目录不落任何产物；所有产物只写入 -OutputDirectory 指定的目录。

.PARAMETER OutputDirectory
    产物目录，默认 D:\文件\实用软件\b站插件\StreamPilot_publish；
    发行版目录会自动创建在其下（目录名已是发行版名时直接使用）。

.PARAMETER SkipTests
    跳过测试（不建议；仅在明确知道风险时使用）。

.PARAMETER SkipArchive
    只生成发行版目录，不压缩为 zip。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build/publish.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File build/publish.ps1 -OutputDirectory D:\temp\out
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'D:\文件\实用软件\b站插件\StreamPilot_publish',
    [switch]$SkipTests,
    [switch]$SkipArchive
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\StreamPilot.App\StreamPilot.App.csproj'

Write-Host '==== 1/6 检查 .NET 10 SDK ====' -ForegroundColor Cyan
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

# 发行版命名：产品名与版本号均取自 App 工程，避免脚本与工程各写一份版本号。
# 用 MSBuild 求值而不是手工解析 XML：工程文件是 UTF-8，在 Windows PowerShell 5.1 下
# 若按 ANSI 读取会破坏中文内容，导致 XML 解析失败。
function Get-ProjectProperty {
    param([string]$ProjectPath, [string]$PropertyName)
    $value = & dotnet msbuild $ProjectPath -getProperty:$PropertyName -nologo 2>$null
    if ($LASTEXITCODE -ne 0 -or $null -eq $value) { return '' }
    return ([string]$value).Trim()
}

$productName = Get-ProjectProperty -ProjectPath $project -PropertyName 'Product'
if ([string]::IsNullOrWhiteSpace($productName)) { $productName = 'StreamPilot' }
$releaseVersion = Get-ProjectProperty -ProjectPath $project -PropertyName 'Version'
if ([string]::IsNullOrWhiteSpace($releaseVersion)) { $releaseVersion = '0.0.0' }
$releaseName = "{0}-windows-v{1}" -f $productName, $releaseVersion

# 若 -OutputDirectory 本身已经是发行版目录，就直接使用；否则在其下建立发行版目录。
if ((Split-Path -Leaf $OutputDirectory) -ne $releaseName) {
    $OutputDirectory = Join-Path $OutputDirectory $releaseName
}

$executableName = "$releaseName.exe"
Write-Host "发行版标识: $releaseName（可执行文件 $executableName）" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host '==== 2/6 运行测试 ====' -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test.ps1')
    if ($LASTEXITCODE -ne 0) {
        Write-Host '测试未通过，已中止发布。' -ForegroundColor Red
        exit 1
    }
}
else {
    Write-Host '==== 2/6 已按要求跳过测试 ====' -ForegroundColor Yellow
}

Write-Host '==== 3/6 发布单文件自包含产物 ====' -ForegroundColor Cyan
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

Write-Host '==== 4/6 组装 Web 资源与文档 ====' -ForegroundColor Cyan
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

Write-Host '==== 5/6 生成 VERSION.txt ====' -ForegroundColor Cyan
$builtExe = Join-Path $OutputDirectory 'StreamPilot.exe'
if (-not (Test-Path $builtExe)) {
    Write-Host "未找到发布产物 $builtExe，发布失败。" -ForegroundColor Red
    exit 1
}

# 发行版文件名与发行版目录同名，便于用户在多版本共存时区分。
$exe = Join-Path $OutputDirectory $executableName
if ($executableName -ne 'StreamPilot.exe') {
    Move-Item -LiteralPath $builtExe -Destination $exe -Force
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
$versionLines += "product=$productName"
$versionLines += "release=$releaseName"
$versionLines += "version=$releaseVersion"
$versionLines += "executable=$executableName"
$versionLines += "buildTime=$((Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz'))"
$versionLines += "gitDescribe=$gitDescribe"
$versionLines += "target=win-x64 / net10.0-windows / self-contained single-file"
$versionLines += "exeSha256=$exeHash"
$versionLines += "webAssets=" + ((Get-ChildItem -Path $webTarget -File | ForEach-Object { "$($_.Name) ($($_.Length) bytes)" }) -join ', ')
$versionLines | Set-Content -Path (Join-Path $OutputDirectory 'VERSION.txt') -Encoding utf8

$totalBytes = (Get-ChildItem -Path $OutputDirectory -Recurse -File | Measure-Object -Property Length -Sum).Sum

$archivePath = $null
$archiveHashPath = $null
if (-not $SkipArchive) {
    Write-Host '==== 6/6 压缩为发行版 zip ====' -ForegroundColor Cyan

    # zip 与发行版目录同级同名：解压后得到 StreamPilot-windows-v0.1.0\ 目录，
    # 目录内的 Web\ 相对路径与虚拟主机映射保持一致，不会多套一层壳。
    #
    # 压缩包的 SHA256 只记录在包外的 <zip>.sha256 中：
    # 把 zip 的哈希写进 zip 内部属于自引用（写入后哈希必然改变），无法自洽。
    $archivePath = Join-Path (Split-Path -Parent $OutputDirectory) "$releaseName.zip"
    $archiveHashPath = "$archivePath.sha256"

    if (Test-Path $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
    Compress-Archive -Path $OutputDirectory -DestinationPath $archivePath -CompressionLevel Optimal -Force
    if (-not (Test-Path $archivePath)) {
        Write-Host "压缩失败：未生成 $archivePath" -ForegroundColor Red
        exit 1
    }

    $archiveBytes = (Get-Item -LiteralPath $archivePath).Length
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash

    Add-Content -Path (Join-Path $OutputDirectory 'VERSION.txt') -Encoding utf8 -Value "archive=$releaseName.zip"
    Set-Content -Path $archiveHashPath -Encoding utf8 -Value ("{0}  {1}" -f $archiveHash, "$releaseName.zip")

    Write-Host ("zip 大小：{0:N1} MiB" -f ($archiveBytes / 1MB))
    Write-Host "zip SHA256：$archiveHash"
    Write-Host "校验文件：$archiveHashPath"
}
else {
    Write-Host '==== 6/6 已按要求跳过压缩 ====' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '发布完成。' -ForegroundColor Green
Write-Host "发行版目录：$OutputDirectory"
Write-Host "可执行文件：$executableName"
Write-Host ("目录总大小：{0:N1} MiB" -f ($totalBytes / 1MB))
Write-Host "$executableName SHA256：$exeHash"
if ($archivePath) { Write-Host "发行版压缩包：$archivePath" }
Write-Host ''
Write-Host '提示：tools\ 目录需要用户自行放入 mpv.exe（见 tools\README.md），仓库中不包含任何二进制。' -ForegroundColor Yellow
exit 0
