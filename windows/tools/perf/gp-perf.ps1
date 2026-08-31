# GuardPulse agent resource measurement harness.
# Samples the Service + Session processes over a window and reports deltas:
# CPU %, RAM, disk writes, network, handles/threads. Run while the machine is idle.
param([int]$Seconds = 60)

$names = 'GuardPulse.Agent.Service','GuardPulse.Agent.Session'

function Get-Snap {
    $wmi = @{}
    Get-CimInstance Win32_Process -Filter "Name LIKE 'GuardPulse%'" | ForEach-Object {
        $wmi[[string]$_.ProcessId] = [pscustomobject]@{
            Name = $_.Name
            Write = [uint64]$_.WriteTransferCount
            Read  = [uint64]$_.ReadTransferCount
        }
    }

    $rows = @()
    foreach ($p in (Get-Process | Where-Object { $names -contains $_.Name })) {
        $io = $wmi[[string]$p.Id]
        $rows += [pscustomobject]@{
            Name     = $p.Name
            Pid      = $p.Id
            WS       = [double]$p.WorkingSet64
            Private  = [double]$p.PrivateMemorySize64
            Handles  = $p.HandleCount
            Threads  = $p.Threads.Count
            CpuMs    = [double]$p.TotalProcessorTime.TotalMilliseconds
            Write    = if ($io) { [double]$io.Write } else { 0 }
            Read     = if ($io) { [double]$io.Read } else { 0 }
        }
    }

    $net = Get-NetAdapterStatistics | Where-Object { $_.ReceivedBytes -gt 0 -or $_.SentBytes -gt 0 } |
        Measure-Object -Property ReceivedBytes, SentBytes -Sum
    [pscustomobject]@{
        Rows = $rows
        NetRx = [double]($net | Where-Object Property -eq 'ReceivedBytes' | Select-Object -Expand Sum)
        NetTx = [double]($net | Where-Object Property -eq 'SentBytes' | Select-Object -Expand Sum)
        At = (Get-Date)
    }
}

$a = Get-Snap
Start-Sleep -Seconds $Seconds
$b = Get-Snap

$mins = $Seconds / 60.0
"GuardPulse agent resource report ($(Get-Date -Format 'yyyy-MM-dd HH:mm'), window ${Seconds}s)"
""
foreach ($rowB in $b.Rows) {
    $rowA = $a.Rows | Where-Object { $_.Name -eq $rowB.Name -and $_.Pid -eq $rowB.Pid }
    if (-not $rowA) { continue }
    $cpuPct = [math]::Round((($rowB.CpuMs - $rowA.CpuMs) / 1000.0) / $Seconds * 100, 2)
    $wKBmin = [math]::Round((($rowB.Write - $rowA.Write) / 1KB) / $mins, 1)
    $rKBmin = [math]::Round((($rowB.Read - $rowA.Read) / 1KB) / $mins, 1)
    "[{0}] pid {1}" -f $rowB.Name, $rowB.Pid
    "  RAM: working set {0:N1} MB, private {1:N1} MB, handles {2}, threads {3}" -f ($rowB.WS/1MB), ($rowB.Private/1MB), $rowB.Handles, $rowB.Threads
    "  CPU: {0} % of one core (window average)" -f $cpuPct
    "  Disk: writes {0} KB/min, reads {1} KB/min" -f $wKBmin, $rKBmin
    ""
}
$netRxMin = [math]::Round((($b.NetRx - $a.NetRx)/1KB)/$mins, 1)
$netTxMin = [math]::Round((($b.NetTx - $a.NetTx)/1KB)/$mins, 1)
"Network (ALL adapters, upper bound): received {0} KB/min, sent {1} KB/min" -f $netRxMin, $netTxMin
