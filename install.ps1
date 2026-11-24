#requires -Version 5.1
<#
.SYNOPSIS
    Installs (or uninstalls) the Sqlzilla app for the current user.
.EXAMPLE
    & ([scriptblock]::Create((irm "https://raw.githubusercontent.com/judgewest2000/sqlzilla/main/install.ps1")))
.EXAMPLE
    & ([scriptblock]::Create((irm "https://raw.githubusercontent.com/judgewest2000/sqlzilla/main/install.ps1"))) -Uninstall
#>
[CmdletBinding()]
param(
    [string]$Repo = 'judgewest2000/sqlzilla',
    [string]$Version = 'latest',
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Sqlzilla'),
    [switch]$AssociateSqlFiles,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$appName = 'Sqlzilla'
$exeName = 'Sqlzilla.exe'
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$appName.lnk"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appName"
$classes = 'HKCU:\Software\Classes'
$progId = "$appName.sql"

function Send-AssocChanged {
    Add-Type -Namespace Sqlzilla -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);'
    [Sqlzilla.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)   # SHCNE_ASSOCCHANGED
}

# Always lists Sqlzilla under "Open with" for .sql; -AssociateSqlFiles also makes it the default.
function Register-SqlFiles([string]$exePath, [bool]$makeDefault) {
    # New-Item -Force on an existing key wipes its values (e.g. other apps' OpenWithProgids), so only create missing keys.
    function Set-Value([string]$key, [string]$name, [string]$value) {
        if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
        New-ItemProperty -Path $key -Name $name -Value $value -PropertyType String -Force | Out-Null
    }
    $open = "`"$exePath`" `"%1`""
    Set-Value "$classes\$progId" '(default)' 'SQL Script'
    Set-Value "$classes\$progId\DefaultIcon" '(default)' "`"$exePath`",0"
    Set-Value "$classes\$progId\shell\open\command" '(default)' $open
    Set-Value "$classes\.sql\OpenWithProgids" $progId ''
    if ($makeDefault) { Set-Value "$classes\.sql" '(default)' $progId }
    Set-Value "$classes\Applications\$exeName" 'FriendlyAppName' $appName
    Set-Value "$classes\Applications\$exeName\SupportedTypes" '.sql' ''
    Set-Value "$classes\Applications\$exeName\shell\open\command" '(default)' $open
    Send-AssocChanged
}

function Unregister-SqlFiles {
    foreach ($key in "$classes\$progId", "$classes\Applications\$exeName") {
        if (Test-Path $key) { Remove-Item $key -Recurse -Force }
    }
    Remove-ItemProperty -Path "$classes\.sql\OpenWithProgids" -Name $progId -ErrorAction SilentlyContinue
    if ((Get-ItemProperty -Path "$classes\.sql" -Name '(default)' -ErrorAction SilentlyContinue).'(default)' -eq $progId) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(($classes -replace '^HKCU:\\', '') + '\.sql', $true)
        try { $key.DeleteValue('', $false) } finally { $key.Dispose() }
    }
    Send-AssocChanged
}

function Stop-App {
    $exe = Join-Path $InstallDir $exeName
    Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exeName)) -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exe } |
        Stop-Process -Force
}

if ($Uninstall) {
    Stop-App
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    if (Test-Path $startMenu) { Remove-Item $startMenu -Force }
    if (Test-Path $uninstallKey) { Remove-Item $uninstallKey -Recurse -Force }
    Unregister-SqlFiles
    Write-Host "$appName uninstalled." -ForegroundColor Green
    return
}

$arch = switch ($env:PROCESSOR_ARCHITECTURE) {
    'AMD64' { 'x64' }
    'ARM64' { 'arm64' }
    'x86'   { if ($env:PROCESSOR_ARCHITEW6432 -eq 'AMD64') { 'x64' } elseif ($env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } else { 'x86' } }
    default { throw "Unsupported architecture: $env:PROCESSOR_ARCHITECTURE" }
}
$assetName = "$appName-win-$arch.zip"

$api = if ($Version -eq 'latest') {
    "https://api.github.com/repos/$Repo/releases/latest"
} else {
    "https://api.github.com/repos/$Repo/releases/tags/$Version"
}

Write-Host "Looking up $Version release of $Repo..." -ForegroundColor Cyan
$release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = "$appName-installer" }
$asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
if (-not $asset) { throw "Release $($release.tag_name) has no asset named $assetName." }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("$appName-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp $assetName
    Write-Host "Downloading $assetName ($([math]::Round($asset.size / 1MB)) MB)..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing

    $staging = Join-Path $tmp 'extracted'
    Expand-Archive -Path $zip -DestinationPath $staging -Force
    if (-not (Test-Path (Join-Path $staging $exeName))) { throw "$exeName not found in $assetName." }

    Stop-App
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    New-Item -ItemType Directory -Path (Split-Path $InstallDir) -Force | Out-Null
    Move-Item -Path $staging -Destination $InstallDir
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

$exePath = Join-Path $InstallDir $exeName
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($startMenu)
$lnk.TargetPath = $exePath
$lnk.WorkingDirectory = $InstallDir
$lnk.IconLocation = "$exePath,0"
$lnk.Save()

$ver = $release.tag_name.TrimStart('v')
New-Item -Path $uninstallKey -Force | Out-Null
$uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command `"& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/$Repo/main/install.ps1'))) -Uninstall -InstallDir '$InstallDir'`""
@{
    DisplayName     = $appName
    DisplayVersion  = $ver
    Publisher       = $Repo.Split('/')[0]
    InstallLocation = $InstallDir
    DisplayIcon     = $exePath
    UninstallString = $uninstallCmd
    NoModify        = 1
    NoRepair        = 1
}.GetEnumerator() | ForEach-Object {
    $type = if ($_.Value -is [int]) { 'DWord' } else { 'String' }
    New-ItemProperty -Path $uninstallKey -Name $_.Key -Value $_.Value -PropertyType $type -Force | Out-Null
}

Register-SqlFiles $exePath $AssociateSqlFiles.IsPresent

Write-Host "$appName $ver installed to $InstallDir" -ForegroundColor Green
