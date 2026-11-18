#requires -Version 5.1
<#
.SYNOPSIS
    Publishes Sqlzilla and builds Setup.exe (Inno Setup) plus the zip used by install.ps1.
.EXAMPLE
    .\build-installer.ps1 -Platform x64 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64', 'ARM64')]
    [string]$Platform = 'x64',
    [Parameter(Mandatory)]
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$arch = $Platform.ToLowerInvariant()

& (Join-Path $root 'publish.ps1') -Runtime "win-$arch" -Version $Version -Zip -SkipTests:$SkipTests

$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup 6 not found. Install with: winget install JRSoftware.InnoSetup' }

& $iscc "/DArch=$arch" "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'Sqlzilla.iss')
if ($LASTEXITCODE) { throw 'iscc failed' }

Write-Host 'Upload these to the GitHub release:' -ForegroundColor Green
Get-ChildItem (Join-Path $root 'publish') -Filter "Sqlzilla-*$arch*" -File | ForEach-Object { Write-Host "  $($_.FullName)" }
