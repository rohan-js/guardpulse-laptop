$ErrorActionPreference = "Continue"
$log = "C:\Users\rohan\AppData\Local\Temp\guardpulse_full_uninstall.log"
"=== GuardPulse full uninstall $(Get-Date -Format o) ===" | Out-File $log -Encoding utf8
function Log($m) { $m | Tee-Object -FilePath $log -Append | Write-Host }
Log "User: $env:USERNAME  Elevated: $(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"

# 1. Kill agents first (unblock UI)
Log "[1] Killing agent processes..."
Get-Process -Name "GuardPulse.Agent.Session","GuardPulse.Agent.Service" -ErrorAction SilentlyContinue | ForEach-Object { Log "  Killing $($_.ProcessName) PID $($_.Id)"; Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep 2

# 2. Run Inno uninstaller. Since 0.2.6 it lives in a random per-install folder;
#    discover its path from the hidden ARP registry entry, with legacy fallbacks.
$arpKeys = @(
  "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7C4E8091-A3B2-4D5F-8E6A-C5D2F0A91B34}_is1",
  "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{7C4E8091-A3B2-4D5F-8E6A-C5D2F0A91B34}_is1"
)
$innoUninstaller = $null
foreach ($k in $arpKeys) {
  $v = (Get-ItemProperty -Path $k -Name UninstallString -ErrorAction SilentlyContinue).UninstallString
  if ($v) { $innoUninstaller = $v.Trim('"').Trim(); break }
}
if (-not $innoUninstaller -or -not (Test-Path $innoUninstaller)) {
  $innoUninstaller = "C:\ProgramData\GuardPulse\Laptop\sys\devdiag.exe"
}
if (-not (Test-Path $innoUninstaller)) {
  $innoUninstaller = "C:\Program Files (x86)\Device Service\unins000.exe"
}
if (Test-Path $innoUninstaller) {
  Log "[2] Running Inno uninstaller $innoUninstaller /SILENT ..."
  $p = Start-Process -FilePath $innoUninstaller -ArgumentList "/SILENT" -Wait -PassThru
  Log "  Inno exit: $($p.ExitCode)"
  Start-Sleep 3
} else { Log "[2] Inno uninstaller not found" }

# 3. Legacy SCM cleanup (C:\Program Files\Device Service)
Log "[3] SCM cleanup GuardPulseDeviceService ..."
& sc.exe stop GuardPulseDeviceService 2>&1 | ForEach-Object { Log "  sc stop: $_" }
Start-Sleep 1
& sc.exe delete GuardPulseDeviceService 2>&1 | ForEach-Object { Log "  sc delete: $_" }
Start-Sleep 1

# 4. Run key
Log "[4] Removing Run key DeviceServiceAgent ..."
Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" -Name "DeviceServiceAgent" -ErrorAction SilentlyContinue
if ($?) { Log "  Removed" } else { Log "  Not present or failed: $Error[0]" }

# 5. SafeBoot keys (both hives)
foreach ($hive in @("HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal","HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network")) {
  $k = Join-Path $hive "GuardPulseDeviceService"
  if (Test-Path $k) { Remove-Item -Path $k -Recurse -Force -ErrorAction SilentlyContinue; Log "  Removed $k" } else { Log "  $k not present" }
}
# Also WOW6432Node uninstall reg (Inno cleans it, but verify)
$innoReg = "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{7C4E8091-A3B2-4D5F-8E6A-C5D2F0A91B34}_is1"
if (Test-Path $innoReg) { Remove-Item -Path $innoReg -Recurse -Force -ErrorAction SilentlyContinue; Log "  Removed $innoReg" } else { Log "  $innoReg already gone" }

# 6. Filesystem
Log "[5] Removing install dirs..."
foreach ($dir in @("C:\Program Files\Device Service","C:\Program Files (x86)\Device Service")) {
  if (Test-Path $dir) {
    try { Remove-Item -Path $dir -Recurse -Force -ErrorAction Stop; Log "  Deleted $dir" } catch { Log "  FAILED $dir : $_" }
  } else { Log "  $dir already gone" }
}
# 7. State dir (full wipe)
Log "[6] Removing state dir C:\ProgramData\GuardPulse ..."
if (Test-Path "C:\ProgramData\GuardPulse") {
  try { Remove-Item -Path "C:\ProgramData\GuardPulse" -Recurse -Force -ErrorAction Stop; Log "  Deleted C:\ProgramData\GuardPulse" } catch { Log "  FAILED state: $_" }
} else { Log "  State already gone" }

# 8. Hosts cleanup (remove GuardPulse block if present - currently not present but safe)
Log "[7] Hosts file check..."
$hosts = "C:\Windows\System32\drivers\etc\hosts"
if (Test-Path $hosts) {
  $content = Get-Content $hosts -Raw -ErrorAction SilentlyContinue
  if ($content -match "BEGIN GUARDPULSE") { Log "  Found GUARDPULSE block - would clean (not implemented, manual)" } else { Log "  No GUARDPULSE hosts block (MS telemetry only)" }
}

# 9. Verify
Log "[8] Verification..."
& sc.exe query GuardPulseDeviceService 2>&1 | ForEach-Object { Log "  sc query: $_" }
Get-Service -Name GuardPulseDeviceService -ErrorAction SilentlyContinue | ForEach-Object { Log "  Get-Service: $($_.Name) $($_.Status)" }
if (-not (Get-Service -Name GuardPulseDeviceService -ErrorAction SilentlyContinue)) { Log "  Service GONE (expected)" }
Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" -Name "DeviceServiceAgent" -ErrorAction SilentlyContinue | ForEach-Object { Log "  Run key still present: $_" }
if (-not (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" -Name "DeviceServiceAgent" -ErrorAction SilentlyContinue)) { Log "  Run key GONE" }
Test-Path "C:\Program Files\Device Service" | ForEach-Object { Log "  C:\Program Files\Device Service exists: $_" }
Test-Path "C:\Program Files (x86)\Device Service" | ForEach-Object { Log "  C:\Program Files (x86)\Device Service exists: $_" }
Test-Path "C:\ProgramData\GuardPulse" | ForEach-Object { Log "  C:\ProgramData\GuardPulse exists: $_" }
Get-Process -Name "GuardPulse.Agent.Session","GuardPulse.Agent.Service" -ErrorAction SilentlyContinue | ForEach-Object { Log "  Still running: $($_.ProcessName) $($_.Id)" }
if (-not (Get-Process -Name "GuardPulse.Agent.Session","GuardPulse.Agent.Service" -ErrorAction SilentlyContinue)) { Log "  No GuardPulse processes running" }

Log "=== Done $(Get-Date -Format o) ==="
Get-Content $log -Raw | Write-Host
