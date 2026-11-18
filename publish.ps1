<#
.SYNOPSIS
    Builds a production (Release, self-contained) copy of Sqlzilla.

.EXAMPLE
    .\publish.ps1                      # x64 into .\publish\win-x64
    .\publish.ps1 -Runtime win-arm64 -Zip
    .\publish.ps1 -Version 1.2.0 -Zip  # stamps the app version (used by the update check)
#>
param(
    [ValidateSet('win-x64', 'win-arm64', 'win-x86')]
    [string]$Runtime = 'win-x64',
    [string]$Version,
    [switch]$Zip,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$output = Join-Path $PSScriptRoot "publish\$Runtime"
$platform = $Runtime.Substring(4)

if (-not $SkipTests) {
    dotnet test tests\Sqlzilla.Tests -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

if (Test-Path $output) { Remove-Item $output -Recurse -Force }

$versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }

dotnet publish src\Sqlzilla\Sqlzilla.csproj @versionArgs `
    -c Release `
    -r $Runtime `
    -p:Platform=$platform `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:DebugType=none `
    -o $output `
    -nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

if ($Zip) {
    $zipPath = Join-Path $PSScriptRoot "publish\Sqlzilla-$Runtime.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$output\*" -DestinationPath $zipPath
    Write-Host "Zipped to $zipPath"
}

Write-Host "Published to $output\Sqlzilla.exe" -ForegroundColor Green
