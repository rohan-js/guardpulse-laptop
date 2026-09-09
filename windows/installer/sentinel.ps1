# GuardPulse sentinel — self-repair for the protection stack.
# Runs as SYSTEM via the GuardPulseSentinel scheduled task (boot + logon + every 30 min).
# Checks service registration/start state, Run key, SafeBoot keys, browser policy keys,
# and the hosts block; repairs what it can and records every intervention to
# sentinel-events.jsonl (the service reads it and pushes tamper events to the phone).
# App binaries cannot be resurrected if deleted — that is recorded (binariesMissing)
# so the parent's phone shows the red alert.
#requires -RunAsAdministrator

param(
    # -Register: (re)create the GuardPulseSentinel scheduled task and exit.
    # Used by the service's mutual watchdog when the task was deleted.
    [switch]$Register
)

$ErrorActionPreference = "Continue"

$SentinelDir = "C:\Windows\System32\GuardPulse"
$TaskName = "GuardPulseSentinel"

if ($Register) {
    $action = New-ScheduledTaskAction -Execute "powershell.exe" `
        -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$SentinelDir\sentinel.ps1`""
    $t1 = New-ScheduledTaskTrigger -AtStartup
    $t2 = New-ScheduledTaskTrigger -AtLogOn
    $t3 = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(30) `
        -RepetitionInterval (New-TimeSpan -Minutes 30) -RepetitionDuration (New-TimeSpan -Days 3650)
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
    Register-ScheduledTask -TaskName $TaskName -Action $action `
        -Trigger @($t1, $t2, $t3) -Principal $principal -Settings $settings -Force | Out-Null
    exit 0
}

$ServiceName = "GuardPulseDeviceService"
$AppDir = "C:\Program Files\Device Service"
$StateDir = Join-Path $env:ProgramData "GuardPulse\Laptop"
$EventsFile = Join-Path $StateDir "sentinel-events.jsonl"
$SentinelDir = "C:\Windows\System32\GuardPulse"
$TaskName = "GuardPulseSentinel"

function Write-Event {
    param([string]$Type, [string]$Message)
    $entry = @{
        type    = $Type
        message = $Message
        atMs    = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    } | ConvertTo-Json -Compress
    try {
        Add-Content -Path $EventsFile -Value $entry -Encoding UTF8 -ErrorAction SilentlyContinue
    } catch { }
}

# Record: service registered?
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    if (Test-Path (Join-Path $AppDir "GuardPulse.Agent.Service.exe")) {
        & sc.exe create $ServiceName binPath= "`"$AppDir\GuardPulse.Agent.Service.exe`"" start= auto DisplayName= "Device Service" | Out-Null
        & sc.exe description $ServiceName "Device background service." | Out-Null
        & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
        & sc.exe sdset $ServiceName "D:(A;;GA;;;SY)(A;;GA;;;BA)" | Out-Null
        Write-Event "sentinelRepaired" "The GuardPulse service had been deleted; the sentinel re-created it."
    } else {
        Write-Event "binariesMissing" "GuardPulse service is gone AND its program files are missing — protection was removed."
    }
} else {
    # Disabled or not auto?
    $startType = (& sc.exe qc $ServiceName | Select-String "START_TYPE").ToString()
    if ($startType -match "DISABLED") {
        & sc.exe config $ServiceName start= auto | Out-Null
        Write-Event "serviceWasDisabled" "The GuardPulse service had been disabled; the sentinel re-enabled it."
    }
    if ($svc.Status -ne "Running") {
        & sc.exe start $ServiceName | Out-Null
        Write-Event "sentinelRepaired" "The GuardPulse service was stopped; the sentinel restarted it."
    }
}

# Run key (logon fallback for the session agent)
$runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
if ((Test-Path (Join-Path $AppDir "GuardPulse.Agent.Session.exe")) -and
    -not (Get-ItemProperty -Path $runKey -Name "DeviceServiceAgent" -ErrorAction SilentlyContinue)) {
    New-ItemProperty -Path $runKey -Name "DeviceServiceAgent" `
        -Value "`"$AppDir\GuardPulse.Agent.Session.exe`"" -PropertyType String -Force | Out-Null
    Write-Event "sentinelRepaired" "The logon start entry had been removed; the sentinel restored it."
}

# SafeBoot keys (protection survives Safe Mode)
foreach ($hive in @("HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal",
                    "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network")) {
    $k = Join-Path $hive $ServiceName
    if (-not (Test-Path $k) -and (Test-Path (Join-Path $AppDir "GuardPulse.Agent.Service.exe"))) {
        New-Item -Path $k -Force | Out-Null
        Set-ItemProperty -Path $k -Name "(default)" -Value "Service"
        Write-Event "sentinelRepaired" "The Safe Mode start entry had been removed; the sentinel restored it."
    }
}

# Browser policy keys (site blocking) + hosts marker — only while the app exists to own them
if (Test-Path (Join-Path $AppDir "GuardPulse.Agent.Service.exe")) {
    $blockActive = $false
    $cachePath = Join-Path $StateDir "policy-cache.json"
    if (Test-Path $cachePath) {
        try {
            $cache = Get-Content $cachePath -Raw | ConvertFrom-Json
            # Any active blocking state means the browser policies should exist.
            $blockActive = ($cache.blockedApps.Count -gt 0) -or ($cache.dailyBlockedApps.Count -gt 0) -or
                           ($cache.sessionBlockedApps.Count -gt 0) -or ($cache.allowlistEnabled) -or ($cache.deviceLocked)
        } catch { }
    }

    if ($blockActive) {
        foreach ($b in @("HKLM:\SOFTWARE\Policies\Google\Chrome",
                         "HKLM:\SOFTWARE\Policies\Microsoft\Edge",
                         "HKLM:\SOFTWARE\Policies\BraveSoftware\Brave")) {
            if (-not (Test-Path (Join-Path $b "URLBlocklist"))) {
                # The service rewrites the full blocklist on its next control apply;
                # the sentinel only notes the tamper so the parent knows.
                Write-Event "browserPolicyRemoved" "The browser site-block policy had been deleted while blocking was active; protection will re-apply it."
                break
            }
        }
    }
}

# Sentinel task self-recreate (if someone deleted the task but left this script)
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $task) {
    try {
        $action = New-ScheduledTaskAction -Execute "powershell.exe" `
            -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$SentinelDir\sentinel.ps1`""
        $triggerBoot = New-ScheduledTaskTrigger -AtStartup
        $triggerLogon = New-ScheduledTaskTrigger -AtLogOn
        $triggerRepeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(30) `
            -RepetitionInterval (New-TimeSpan -Minutes 30) -RepetitionDuration (New-TimeSpan -Days 3650)
        $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
        Register-ScheduledTask -TaskName $TaskName -Action $action `
            -Trigger @($triggerBoot, $triggerLogon, $triggerRepeat) `
            -Principal $principal -Settings $settings -Force | Out-Null
        Write-Event "sentinelRepaired" "The sentinel task itself had been deleted; it re-registered itself."
    } catch {
        Write-Event "sentinelRepaired" ("Sentinel self-recreate failed: " + $_.Exception.Message)
    }
}
