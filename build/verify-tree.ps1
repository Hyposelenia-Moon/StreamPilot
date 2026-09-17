<#
.SYNOPSIS
    Static self-check for StreamPilot: quality red lines, layering, forbidden files.

.DESCRIPTION
    Guards the CLAUDE.md red lines without needing a compiler or network.
    Any violation returns a non-zero exit code, so this can gate CI.

.PARAMETER VerifyHashes
    Also print SHA256 of the third-party JS assets in Web/ (for release records).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build/verify-tree.ps1
#>
[CmdletBinding()]
param(
    [switch]$VerifyHashes
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

function Get-Files {
    param([string]$RelativePath, [string[]]$Patterns)
    $base = Join-Path $root $RelativePath
    if (-not (Test-Path $base)) { return @() }
    $result = @()
    foreach ($pattern in $Patterns) {
        $result += Get-ChildItem -Path $base -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue
    }
    return $result
}

function Test-PatternAbsent {
    param(
        [string]$Name,
        [object[]]$Files,
        [string]$Pattern
    )
    $hits = @()
    foreach ($file in $Files) {
        $matches = Select-String -Path $file.FullName -Pattern $Pattern -ErrorAction SilentlyContinue
        foreach ($m in $matches) {
            $relative = $file.FullName.Substring($root.Length + 1)
            $hits += "$relative`:$($m.LineNumber): $($m.Line.Trim())"
        }
    }
    if ($hits.Count -gt 0) {
        $failures.Add("[$Name] forbidden pattern /$Pattern/:`n    " + ($hits -join "`n    "))
    }
}

$csFiles = Get-Files -RelativePath 'src' -Patterns @('*.cs')
$allCsFiles = $csFiles + (Get-Files -RelativePath 'tests' -Patterns @('*.cs'))
$coreFiles = Get-Files -RelativePath 'src\StreamPilot.Core' -Patterns @('*.cs')
$parserFiles = Get-Files -RelativePath 'src\StreamPilot.Parsers' -Patterns @('*.cs')
$recordingFiles = Get-Files -RelativePath 'src\StreamPilot.Recording' -Patterns @('*.cs')
$bridgeFiles = Get-Files -RelativePath 'src\StreamPilot.Bridge' -Patterns @('*.cs')

Write-Host '== 1. Forbidden debug / TODO markers ==' -ForegroundColor Cyan
Test-PatternAbsent -Name 'TODO/FIXME/HACK' -Files $allCsFiles -Pattern '\b(TODO|FIXME|HACK)\b'

Write-Host '== 2. Forbidden debug output ==' -ForegroundColor Cyan
Test-PatternAbsent -Name 'Console output' -Files $csFiles -Pattern 'Console\.(WriteLine|Write)\s*\('

Write-Host '== 3. Layering ==' -ForegroundColor Cyan
# Comments that merely mention WebView2 are fine (the player page is a UI concern);
# only real UI type usage inside Core is a violation.
Test-PatternAbsent -Name 'Core must not reference UI' -Files $coreFiles -Pattern '(System\.Windows|PresentationFramework|PresentationCore|Microsoft\.Web\.WebView2|WinForms|Process\.Start|HttpListener)'
Test-PatternAbsent -Name 'Parsers must not reference App' -Files $parserFiles -Pattern 'StreamPilot\.App'
Test-PatternAbsent -Name 'Recording must not reference App/Bridge' -Files $recordingFiles -Pattern '(StreamPilot\.App|StreamPilot\.Bridge)'
Test-PatternAbsent -Name 'Bridge must not reference App' -Files $bridgeFiles -Pattern 'StreamPilot\.App'
Test-PatternAbsent -Name 'Recording must not transcode' -Files $recordingFiles -Pattern '(ffmpeg|Process\.Start|libx264|libavcodec)'

Write-Host '== 4. Bridge must listen on loopback only ==' -ForegroundColor Cyan
Test-PatternAbsent -Name 'Wildcard listener forbidden' -Files $bridgeFiles -Pattern 'http://(\+|\*|0\.0\.0\.0)'

Write-Host '== 5. No official binaries or private files in the repo ==' -ForegroundColor Cyan
$forbidden = @('*.exe', '*.dll', '*.pdb', 'huya-cookie.txt', 'mpv-config.txt', '.env', '*.cookie')
foreach ($pattern in $forbidden) {
    $found = Get-ChildItem -Path $root -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git)\\' }
    if ($found) {
        $relative = ($found | ForEach-Object { $_.FullName.Substring($root.Length + 1) }) -join ', '
        $failures.Add("[forbidden binary/private file] $pattern : $relative")
    }
}

Write-Host '== 6. Code hygiene (long method / deep nesting heuristic) ==' -ForegroundColor Cyan
$maxMethodLines = 300
# Build the regex from char codes so the script stays ASCII-only and parser-safe.
$openParen = [char]40
$openBrace = [string][char]123
$closeBrace = [string][char]125
$declarationPattern = '^' + [char]92 + 's*(public|private|protected|internal)[^;]*' + [char]92 + $openParen

foreach ($file in $allCsFiles) {
    $lines = Get-Content -Path $file.FullName
    $depth = 0
    $methodStart = 0
    $maxDepth = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($depth -eq 0 -and $line -match $declarationPattern) {
            $methodStart = $i
        }
        $openCount = ([regex]::Matches($line, $openBrace)).Count
        $closeCount = ([regex]::Matches($line, $closeBrace)).Count
        $depth += $openCount - $closeCount
        if ($depth -gt $maxDepth) { $maxDepth = $depth }
        if ($depth -le 0 -and $methodStart -gt 0) {
            $length = $i - $methodStart
            if ($length -gt $maxMethodLines) {
                $relative = $file.FullName.Substring($root.Length + 1)
                $failures.Add("[method too long] $relative`:$($methodStart + 1) ~$length lines (max $maxMethodLines)")
            }
            $methodStart = 0
        }
    }
    if ($maxDepth -gt 10) {
        $relative = $file.FullName.Substring($root.Length + 1)
        $warnings.Add("[deep nesting] $relative max depth $maxDepth")
    }
}

Write-Host '== 7. Required files and docs ==' -ForegroundColor Cyan
$requiredFiles = @(
    'README.md',
    'LICENSE',
    'CLAUDE.md',
    'THIRD-PARTY-NOTICES.md',
    'docs\adr\0001-技术栈选型.md',
    'docs\adr\0002-架构分层.md',
    'docs\adr\0003-解析器实现.md',
    'docs\adr\0004-录制实现.md',
    'docs\adr\0005-桥接服务与打包发布.md',
    'Web\player.html',
    'Web\player-core.js',
    'Web\mpegts.js',
    'Web\hls.js',
    'tools\README.md'
)
foreach ($relative in $requiredFiles) {
    if (-not (Test-Path (Join-Path $root $relative))) {
        $failures.Add("[missing required file] $relative")
    }
}

if ($VerifyHashes) {
    Write-Host '== 8. Third-party JS asset hashes ==' -ForegroundColor Cyan
    foreach ($name in @('mpegts.js', 'hls.js')) {
        $path = Join-Path $root "Web\$name"
        if (Test-Path $path) {
            $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash
            Write-Host ("    Web/{0}  SHA256 = {1}" -f $name, $hash)
        }
    }
}

Write-Host ''
if ($warnings.Count -gt 0) {
    Write-Host 'WARNINGS:' -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
}

if ($failures.Count -gt 0) {
    Write-Host 'SELF-CHECK FAILED:' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'Static self-check passed: no red-line violations found.' -ForegroundColor Green
exit 0
