# Removes the Device Service, its Run key and (optionally) all local state.
# Mirrors windows/installer/installer.iss [UninstallDelete]/[UninstallRun] + usPostUninstall.
#requires -RunAsAdministrator

[CmdletBinding()]
param(
    [switch]$RemoveData
)

$ErrorActionPreference = "Continue"

$ServiceName = "GuardPulseDeviceService"
$RunKeyName = "DeviceServiceAgent"

function Wait-ServiceGone {
    param([string]$Name, [int]$TimeoutSec = 15)
    for ($i = 0; $i -lt ($TimeoutSec * 2); $i++) {
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if (-not $svc) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return ($null -eq (Get-Service -Name $Name -ErrorAction SilentlyContinue))
}

function Wait-ProcessGone {
    param([string[]]$Names, [int]$TimeoutSec = 15)
    for ($i = 0; $i -lt ($TimeoutSec * 2); $i++) {
        $procs = Get-Process -Name $Names -ErrorAction SilentlyContinue
        if (-not $procs) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return ($null -eq (Get-Process -Name $Names -ErrorAction SilentlyContinue))
}

$Existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($Existing) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    if (-not (Wait-ServiceGone -Name $ServiceName -TimeoutSec 15)) {
        Write-Warning "Service $ServiceName did not stop within 15s; proceeding anyway."
    }
    Get-Process -Name "GuardPulse.Agent.Session", "GuardPulse.Agent.Service" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Wait-ProcessGone -Names @("GuardPulse.Agent.Session", "GuardPulse.Agent.Service") -TimeoutSec 15 | Out-Null
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Service $ServiceName deleted."
    } else {
        Write-Warning "sc.exe delete failed ($LASTEXITCODE); it may already be marked for deletion."
    }
} else {
    Write-Host "Service $ServiceName is not installed."
}

# SafeBoot Minimal/Network keys (mirrors installer.iss [Registry] uninsdeletekey).
foreach ($SafeBootKey in "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal",
                         "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network") {
    Remove-Item -Path (Join-Path $SafeBootKey $ServiceName) -Recurse -Force -ErrorAction SilentlyContinue
}

# Local web-dashboard urlacl reservation removed in 0.2.13 (mirror installer.iss usPostUninstall).
& netsh.exe http delete urlacl url=http://127.0.0.1:37841/ | Out-Null

# Dashboard shortcuts from pre-0.2.13 installs (mirror installer.iss [UninstallDelete]).
foreach ($Shortcut in @(
    (Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "GuardPulse Dashboard.url"),
    (Join-Path ([Environment]::GetFolderPath("CommonPrograms")) "GuardPulse\Dashboard.url"),
    (Join-Path ([Environment]::GetFolderPath("Desktop")) "GuardPulse Dashboard.url")
)) {
    Remove-Item -Path $Shortcut -Force -ErrorAction SilentlyContinue
}

# Legacy install.ps1-era HKLM Run entry + per-user HKCU Run entry (mirror installer.iss).
Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" `
    -Name $RunKeyName -ErrorAction SilentlyContinue
Remove-ItemProperty -Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" `
    -Name $RunKeyName -ErrorAction SilentlyContinue

# Hidden installer dirs (mirror installer.iss HideUninstaller cleanup).
Remove-Item -Path (Join-Path $env:ProgramData "GuardPulse\Laptop\sys") -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveData) {
    # Scoped to ...\GuardPulse\Laptop only (never the whole GuardPulse tree).
    $StateRoot = Join-Path $env:ProgramData "GuardPulse\Laptop"
    if (Test-Path $StateRoot) {
        Remove-Item -Path $StateRoot -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Removed $StateRoot."
    }
}

Write-Host "Uninstall complete. Log off / log on to close any running agent."
