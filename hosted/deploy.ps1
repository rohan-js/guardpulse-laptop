# GuardPulse Hosted Console — Cloudflare Pages deploy script
# Requires:  npm install -g wrangler  (or  npx wrangler)
# Usage:     .\deploy.ps1
# First run: wrangler login  (opens a browser window)

$ErrorActionPreference = "Stop"

$Wrangler = "npx.cmd"
if (Get-Command "wrangler" -ErrorAction SilentlyContinue) {
    $Wrangler = "wrangler"
}
Write-Host "Deploying to Cloudflare Pages..." -ForegroundColor Cyan
& $Wrangler pages deploy --project-name guardpulse-console
Write-Host "Done. The URL printed above is your hosted console." -ForegroundColor Green