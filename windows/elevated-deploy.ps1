# Elevated deploy: copy the rebuilt agent (publish\*) over the installed copy and
# restart the Device Service so the new parent-console build goes live.
# Run as Administrator. Mirrors the installer's runtime steps (stop -> copy -> start).
# Native tools (net/taskkill) write to stderr and return non-zero for states that
# are perfectly fine here (service already stopped, process already gone), so we
# deliberately do NOT use $ErrorActionPreference = 'Stop'; only a failed copy
# aborts the deploy.
$ErrorActionPreference = 'Continue'
$src = 'D:\UVM\PROJECTS\somthing1\somthgn1\guardpulse-laptop\windows\installer\publish'
$dest = 'C:\Program Files (x86)\Device Service'
$svc = 'GuardPulseDeviceService'
$marker = 'C:\Temp\gp_deploy_result.txt'

function Log($m) { "$m" | Tee-Object -FilePath $marker -Append }

Log "Stopping service $svc (if running) ..."
& net.exe stop $svc 2>&1 | Out-Null
Start-Sleep -Seconds 2

Log "Stopping any running Session UI ..."
& taskkill.exe /F /IM GuardPulse.Agent.Session.exe 2>&1 | Out-Null
& taskkill.exe /F /IM GuardPulse.Agent.Service.exe 2>&1 | Out-Null
Start-Sleep -Seconds 1

Log "Copying publish\* -> $dest ..."
if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
& robocopy.exe "$src" "$dest" /E /R:2 /W:2 /NFL /NDL /NP
if ($LASTEXITCODE -ge 8) {
    Log "FAIL: robocopy failed (exit $LASTEXITCODE)"
    exit 1
}

Log "Starting service $svc ..."
& net.exe start $svc 2>&1 | Out-Null
Start-Sleep -Seconds 3

# Make sure the per-logon UI (dashboard server) is up.
$sess = Get-Process -Name 'GuardPulse.Agent.Session' -ErrorAction SilentlyContinue
if (-not $sess) {
    Log "Launching Session UI ..."
    Start-Process -FilePath (Join-Path $dest 'GuardPulse.Agent.Session.exe')
}

Log "OK: deploy complete at $(Get-Date)"
exit 0
