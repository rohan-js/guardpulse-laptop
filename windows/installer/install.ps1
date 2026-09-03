# Installs the Device Service (Windows service + per-user agent fallback).
# Requires an elevated PowerShell.
#requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\Device Service",
    [Parameter(Mandatory = $true)]
    [string]$ApiKey,
    [Parameter(Mandatory = $true)]
    [string]$ProjectId,
    [string]$DatabaseUrl = "https://guardpulse-laptop-control-default-rtdb.firebaseio.com",
    [string]$SourceDir = ""
)

$ErrorActionPreference = "Stop"

$ServiceName = "GuardPulseDeviceService"
$ServiceDisplayName = "Device Service"
$RunKeyName = "DeviceServiceAgent"
$StateRoot = Join-Path $env:ProgramData "GuardPulse\Laptop"

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $SourceDir = Join-Path $PSScriptRoot "publish"
}

$ServiceExe = Join-Path $InstallDir "GuardPulse.Agent.Service.exe"
$AgentExe = Join-Path $InstallDir "GuardPulse.Agent.Session.exe"

if (-not (Test-Path (Join-Path $SourceDir "GuardPulse.Agent.Service.exe"))) {
    throw "Publish output not found in '$SourceDir'. Publish first (dotnet publish ... -o <folder>) and pass it with -SourceDir."
}
if (-not (Test-Path (Join-Path $SourceDir "GuardPulse.Agent.Session.exe"))) {
    throw "GuardPulse.Agent.Session.exe not found in '$SourceDir'. Both exes must come from the same publish folder."
}

# --- stop the existing stack FIRST: running exes are file-locked and would break the copy
$Existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($Existing) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}
Get-Process -Name "GuardPulse.Agent.Session", "GuardPulse.Agent.Service" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# --- copy binaries ------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $SourceDir "*") -Destination $InstallDir -Recurse -Force

# --- agent-config.json from template -----------------------------------------
$TemplatePath = Join-Path $PSScriptRoot "agent-config.template.json"
if (Test-Path $TemplatePath) {
    $ConfigJson = Get-Content $TemplatePath -Raw
    $ConfigJson = $ConfigJson.Replace("__API_KEY__", $ApiKey).Replace("__PROJECT_ID__", $ProjectId).Replace("__DATABASE_URL__", $DatabaseUrl)
} else {
    $ConfigJson = @{ apiKey = $ApiKey; projectId = $ProjectId; databaseUrl = $DatabaseUrl } | ConvertTo-Json
}
Set-Content -Path (Join-Path $InstallDir "agent-config.json") -Value $ConfigJson -Encoding UTF8

# --- state directory + ACL ------------------------------------------------------
# Users get read/traverse on the directory itself only (no OI/CI), so files the
# SYSTEM service creates start locked down; the service grants device.json and
# policy-cache.json Users:R explicitly at runtime. The installer also locks any
# state carried over from a previous install.
New-Item -ItemType Directory -Force -Path $StateRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $StateRoot "logs") | Out-Null
# *S-1-5-32-545 = BUILTIN\Users, *S-1-5-18 = SYSTEM, *S-1-5-32-544 = Administrators (locale-independent)
& icacls.exe $StateRoot /remove:g "*S-1-5-32-545" | Out-Null
& icacls.exe $StateRoot /grant "*S-1-5-32-545:(RX)" | Out-Null
if (Test-Path (Join-Path $StateRoot "device.json")) {
    & icacls.exe (Join-Path $StateRoot "device.json") /grant "*S-1-5-32-545:R" | Out-Null
}
$LockTargets = @(
    (Join-Path $StateRoot "secrets.bin"),
    (Join-Path $StateRoot "enforcement-state.json")
) + (Get-ChildItem -Path $StateRoot -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "usage-*.json" -or $_.Name -like "offsets-*.json" -or $_.Name -like "blocks-*.json" } |
    ForEach-Object { $_.FullName })
# One file per icacls call: the multi-file form fails with error 87.
foreach ($target in @($LockTargets | Where-Object { Test-Path $_ } | Select-Object -Unique)) {
    & icacls.exe $target /inheritance:r /grant:r "*S-1-5-18:(F)" "*S-1-5-32-544:(F)" | Out-Null
}
& icacls.exe (Join-Path $StateRoot "logs") /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)" | Out-Null

# --- service ------------------------------------------------------------------
& sc.exe create $ServiceName binPath= "`"$ServiceExe`"" start= auto DisplayName= "$ServiceDisplayName"
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed ($LASTEXITCODE)." }

& sc.exe description $ServiceName "Device background service."
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000
if ($LASTEXITCODE -ne 0) { throw "sc.exe failure failed ($LASTEXITCODE)." }

# SYSTEM/Administrators only: standard users cannot stop or reconfigure the service.
& sc.exe sdset $ServiceName "D:(A;;GA;;;SY)(A;;GA;;;BA)"
if ($LASTEXITCODE -ne 0) { throw "sc.exe sdset failed ($LASTEXITCODE)." }

# --- start even in Safe Mode: the protection must survive a Safe Mode reboot ---
foreach ($SafeBootKey in "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal",
                         "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network") {
    New-Item -Path (Join-Path $SafeBootKey $ServiceName) -Force | Out-Null
    Set-ItemProperty -Path (Join-Path $SafeBootKey $ServiceName) -Name "(default)" -Value "Service" -ErrorAction SilentlyContinue
}

Start-Service -Name $ServiceName

# --- per-user fallback start at logon -----------------------------------------
New-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" `
    -Name $RunKeyName -Value "`"$AgentExe`"" -PropertyType String -Force | Out-Null

Get-Service -Name $ServiceName | Format-Table Name, DisplayName, Status, StartType

Write-Host ""
Write-Host "Install complete."
Write-Host "Reminder: log off and back on (or reboot) once so the per-user agent starts for each user."
Write-Host "The service also launches the agent automatically for currently active sessions."
