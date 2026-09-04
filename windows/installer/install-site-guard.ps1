# Installs the GuardPulse Site Guard extension into Chromium browsers as an UNPACKED
# extension. This is a ONE-TIME manual step per browser: Chromium only allows
# policy-force-installed extensions from the Chrome Web Store, so the folder is
# loaded via the extensions page instead.

Write-Host ""
Write-Host "=== GuardPulse Site Guard - browser extension install ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "For EACH browser you use (Brave / Chrome / Edge):"
Write-Host ""
Write-Host "  1. Open the extensions page:   " -NoNewline
Write-Host "brave://extensions   (or chrome://extensions / edge://extensions)" -ForegroundColor Yellow
Write-Host "  2. Turn ON  'Developer mode'  (toggle, top-right)."
Write-Host "  3. Click  'Load unpacked'."
Write-Host "  4. Select this folder when prompted:"
Write-Host ""
Write-Host "     $PSScriptRoot\..\extension" -ForegroundColor Green
Write-Host ""
Write-Host "  5. Done. The extension persists across browser restarts."
Write-Host ""

$ext = Join-Path $PSScriptRoot "..\extension"
if (Test-Path (Join-Path $ext "manifest.json")) {
    Write-Host "Opening the extension folder in Explorer..."
    Start-Process explorer.exe $ext
    Start-Process "brave://extensions"
} else {
    Write-Host "Extension folder not found next to this script." -ForegroundColor Red
}
