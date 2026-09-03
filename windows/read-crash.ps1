$f = Get-ChildItem 'C:\ProgramData\Microsoft\Windows\WER\ReportArchive' -Filter 'AppCrash_GuardPulse*' |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ("REPORT: " + $f.FullName)
Get-ChildItem $f.FullName | ForEach-Object { Write-Host ("  file: " + $_.Name) }
$w = Join-Path $f.FullName 'Report.wer'
if (Test-Path $w) {
  Write-Host "=== Report.wer (key lines) ==="
  Get-Content $w | Select-String -Pattern 'EventType|FaultingModule|Exception|ModuleName|AppPath|Application|Parameter' | Select-Object -First 30
}
$m = Join-Path $f.FullName 'WERInternalMetadata.xml'
if (Test-Path $m) {
  Write-Host "=== WERInternalMetadata ==="
  Get-Content $m | Select-String -Pattern 'guardpulse|Exception|crash|Stack|Module' | Select-Object -First 30
}
