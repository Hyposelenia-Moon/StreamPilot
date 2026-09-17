<#
.SYNOPSIS
    Run all StreamPilot tests: C# unit tests, player-core JS tests, static self-check.

.DESCRIPTION
    Any failing stage returns a non-zero exit code. Missing toolchains are reported
    explicitly instead of being silently skipped.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build/test.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipStatic,
    [switch]$SkipWeb
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failed = $false

function Write-Section {
    param([string]$Title)
    Write-Host ''
    Write-Host "==== $Title ====" -ForegroundColor Cyan
}

function Get-DotnetSdkPath {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { return $null }

    $sdks = & dotnet --list-sdks 2>$null
    if (-not $sdks) { return $null }

    $hasTen = $false
    foreach ($line in $sdks) {
        if ($line -match '^10\.') { $hasTen = $true }
    }

    if (-not $hasTen) {
        Write-Host 'dotnet found but no .NET 10 SDK. Installed SDKs:' -ForegroundColor Yellow
        $sdks | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
        Write-Host 'Install: https://dotnet.microsoft.com/download/dotnet/10.0' -ForegroundColor Yellow
        return $null
    }

    return $dotnet.Source
}

Write-Section '1/3 C# unit tests'
$dotnetPath = Get-DotnetSdkPath
if ($null -eq $dotnetPath) {
    Write-Host 'SKIPPED: .NET 10 SDK is not available.' -ForegroundColor Yellow
    $failed = $true
}
else {
    & $dotnetPath run --project (Join-Path $root 'tests\StreamPilot.Tests\StreamPilot.Tests.csproj') -c Debug
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'C# unit tests FAILED.' -ForegroundColor Red
        $failed = $true
    }
    else {
        Write-Host 'C# unit tests passed.' -ForegroundColor Green
    }
}

if (-not $SkipWeb) {
    Write-Section '2/3 Player-core JS tests (node --test)'
    $node = Get-Command node -ErrorAction SilentlyContinue
    if (-not $node) {
        Write-Host 'SKIPPED: node is not available.' -ForegroundColor Yellow
        $failed = $true
    }
    else {
        $testFile = Join-Path $root 'tests\web\player-core.test.js'
        & $node.Source --test $testFile
        if ($LASTEXITCODE -ne 0) {
            Write-Host 'Player-core tests FAILED.' -ForegroundColor Red
            $failed = $true
        }
        else {
            Write-Host 'Player-core tests passed.' -ForegroundColor Green
        }
    }
}

if (-not $SkipStatic) {
    Write-Section '3/3 Static self-check'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-tree.ps1')
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Static self-check FAILED.' -ForegroundColor Red
        $failed = $true
    }
}

Write-Section 'Result'
if ($failed) {
    Write-Host 'Some stages failed.' -ForegroundColor Red
    exit 1
}

Write-Host 'All stages passed.' -ForegroundColor Green
exit 0
