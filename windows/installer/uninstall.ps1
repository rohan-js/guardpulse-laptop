# Removes the Device Service, its Run key and (optionally) all local state.
#requires -RunAsAdministrator

[CmdletBinding()]
param(
    [switch]$RemoveData
)

$ErrorActionPreference = "Continue"

$ServiceName = "GuardPulseDeviceService"
$RunKeyName = "DeviceServiceAgent"

$Existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($Existing) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Service $ServiceName deleted."
    } else {
        Write-Warning "sc.exe delete failed ($LASTEXITCODE); it may already be marked for deletion."
    }
} else {
    Write-Host "Service $ServiceName is not installed."
}

Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" `
    -Name $RunKeyName -ErrorAction SilentlyContinue

if ($RemoveData) {
    $StateRoot = Join-Path $env:ProgramData "GuardPulse"
    if (Test-Path $StateRoot) {
        Remove-Item -Path $StateRoot -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Removed $StateRoot."
    }
}

Write-Host "Uninstall complete. Log off / log on to close any running agent."
