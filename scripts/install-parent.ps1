param(
    [string]$ApkPath = "",
    [string]$AdbPath = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ApkPath)) {
    $ApkPath = Join-Path $repo "parent\build\outputs\apk\debug\parent-debug.apk"
}

if ([string]::IsNullOrWhiteSpace($AdbPath)) {
    $AdbPath = Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
}

if (!(Test-Path $ApkPath)) {
    throw "Parent APK not found: $ApkPath. Run scripts\build.ps1 first."
}

if (!(Test-Path $AdbPath)) {
    throw "adb.exe not found: $AdbPath"
}

& $AdbPath devices
& $AdbPath install -r $ApkPath
if ($LASTEXITCODE -ne 0) {
    throw "Parent APK install failed."
}

Write-Host "Parent app installed."
