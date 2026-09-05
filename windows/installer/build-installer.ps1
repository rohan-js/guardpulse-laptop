# GuardPulse Laptop â€” One-Click Installer Builder
# Publishes both exes as ReadyToRun, merges them, generates the ICO,
# and invokes ISCC.exe to produce GuardPulseLaptopSetup-<version>.exe
# Usage: .\build.ps1 [-Configuration Release]

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# Wire dotnet (not on system PATH)
$DotNet = "C:\Users\rohan\AppData\Local\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $DotNet)) { throw "dotnet not found at $DotNet" }

$Root = Split-Path (Split-Path $PSScriptRoot)
$WinDir = Join-Path $Root "windows"
$InstallerDir = Join-Path $WinDir "installer"
$PublishDir = Join-Path $InstallerDir "publish"
$OutputDir = Join-Path $InstallerDir "Output"
$Issc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
$LogoPng = Join-Path $Root "docs\assets\guardpulse-logo.png"
$LogoIco = Join-Path $WinDir "assets\guardpulse-laptop.ico"

Write-Host "=== GuardPulse Laptop Installer Build ===" -ForegroundColor Cyan

# --- 1. Publish both projects ---
Write-Host "`n[1/5] Publishing GuardPulse.Agent.Service..." -ForegroundColor Yellow
& $DotNet publish (Join-Path $WinDir "src\GuardPulse.Agent.Service\GuardPulse.Agent.Service.csproj") `
    -c $Configuration -r win-x64 --self-contained `
    -p:PublishReadyToRun=true --nologo -v q `
    -o (Join-Path $WinDir "publish\service") 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Service publish failed ($LASTEXITCODE)" }
Write-Host "  OK" -ForegroundColor Green

Write-Host "[2/5] Publishing GuardPulse.Agent.Session..." -ForegroundColor Yellow
& $DotNet publish (Join-Path $WinDir "src\GuardPulse.Agent.Session\GuardPulse.Agent.Session.csproj") `
    -c $Configuration -r win-x64 --self-contained `
    -p:PublishReadyToRun=true --nologo -v q `
    -o (Join-Path $WinDir "publish\session") 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Session publish failed ($LASTEXITCODE)" }
Write-Host "  OK" -ForegroundColor Green

# --- 2. Merge into installer/publish ---
Write-Host "[3/5] Merging publish payloads..." -ForegroundColor Yellow

if (-not (Test-Path (Join-Path $WinDir "publish\service\GuardPulse.Agent.Service.exe"))) {
    throw "Service exe not found in publish/service"
}
if (-not (Test-Path (Join-Path $WinDir "publish\session\GuardPulse.Agent.Session.exe"))) {
    throw "Session exe not found in publish/session"
}

Remove-Item -Path $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
Copy-Item -Path (Join-Path $WinDir "publish\service\*") -Destination $PublishDir -Recurse -Force
Copy-Item -Path (Join-Path $WinDir "publish\session\*") -Destination $PublishDir -Recurse -Force

# Copy content-blocklists
$BlSrc = Join-Path $WinDir "content-blocklists"
$BlDst = Join-Path $PublishDir "content-blocklists"
if (Test-Path $BlSrc) {
    New-Item -ItemType Directory -Force -Path $BlDst | Out-Null
    Copy-Item -Path "$BlSrc\*" -Destination $BlDst -Force
}
else {
    throw "content-blocklists not found at $BlSrc - the installer would ship without content filtering"
}

# Copy + pack the Site Guard extension (force-installed into Chrome/Edge/Brave).
$ExtSrc = Join-Path $WinDir "extension"
$ExtDst = Join-Path $PublishDir "extension"
if (-not (Test-Path (Join-Path $ExtSrc "manifest.json"))) {
    throw "extension manifest not found at $ExtSrc - the installer would ship without site blocking"
}
New-Item -ItemType Directory -Force -Path $ExtDst | Out-Null
Copy-Item -Path "$ExtSrc\*" -Destination $ExtDst -Force -Exclude "*.crx", "*.pem"

# Pack a CRX3 with a COMMITTED signing key (stable extension id across builds).
# The key must live OUTSIDE the extension dir (Chrome refuses a key inside it).
$PemPath = Join-Path $WinDir "extension-signing\guardpulse-block.pem"
$CrxPath = Join-Path $ExtDst "guardpulse-block.crx"
$Browser = @("C:\Program Files\Google\Chrome\Application\chrome.exe",
             "C:\Program Files\BraveSoftware\Brave-Browser\Applicationrave.exe",
             "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Browser) { $Browser = (Get-Command brave -ErrorAction SilentlyContinue).Source }
if (-not $Browser) { throw "no Chromium browser found to pack the Site Guard extension" }
if (-not (Test-Path $PemPath)) { throw "signing key missing at $PemPath - the extension id would change" }
# Chrome writes "<src>.crx" NEXT TO THE SOURCE DIR, so pack in place then move.
Write-Host "  Packing Site Guard extension with $Browser..." -ForegroundColor Yellow
$SrcCrx = "$ExtSrc.crx"
Remove-Item $SrcCrx, $CrxPath -Force -ErrorAction SilentlyContinue
& $Browser --pack-extension="$ExtSrc" --pack-extension-key="$PemPath" 2>$null | Out-Null
Start-Sleep -Seconds 3
if (-not (Test-Path $SrcCrx)) {
    throw "CRX was not produced at $SrcCrx - extension force-install will not work"
}
Copy-Item $SrcCrx $CrxPath -Force
Remove-Item $SrcCrx -Force -ErrorAction SilentlyContinue

# Extension version comes from the manifest (must match the CRX exactly).
$Manifest = Get-Content (Join-Path $ExtSrc "manifest.json") -Raw | ConvertFrom-Json
$ExtVersion = $Manifest.version

# Derive the extension id from the CRX3 header (field 2 = crx_id, 16 bytes).
$CrxBytes = [IO.File]::ReadAllBytes($CrxPath)
$HeaderLen = [BitConverter]::ToUInt32($CrxBytes, 8)
$Id = $null
for ($i = 12; $i -lt (12 + $HeaderLen - 17); $i++) {
    if ($CrxBytes[$i] -eq 0x12 -and $CrxBytes[$i + 1] -eq 0x10) {
        $Id = ""
        for ($j = $i + 2; $j -lt ($i + 18); $j++) {
            $Id += [char](97 + ($CrxBytes[$j] -shr 4))
            $Id += [char](97 + ($CrxBytes[$j] -band 0xF))
        }
        break
    }
}
if (-not $Id) { throw "could not derive Site Guard extension id from CRX" }

# Chromium requires the updatecheck to carry the CRX SHA-256 (hash_sha256):
# without it the updater rejects the response before downloading.
$Sha = [System.Security.Cryptography.SHA256]::Create()
$CrxHash = [Convert]::ToBase64String($Sha.ComputeHash([IO.File]::ReadAllBytes($CrxPath)))  # hash_sha256 expects BASE64

# updates.xml: Chromium's self-hosted update manifest (served by BlocklistServer).
$UpdatesXml = @"
<?xml version="1.0" encoding="UTF-8"?>
<gupdate xmlns="http://www.google.com/update2/response" protocol="2.0">
  <app appid="$Id">
    <updatecheck codebase="https://127.0.0.1:37846/extension.crx" version="$ExtVersion" hash_sha256="$CrxHash" status="ok" />
  </app>
</gupdate>
"@
Set-Content -Path (Join-Path $ExtDst "updates.xml") -Value $UpdatesXml -Encoding UTF8
Write-Host "  Site Guard extension packed (id $Id)" -ForegroundColor Green

Write-Host "  OK ($((Get-ChildItem $PublishDir -File).Count) files)" -ForegroundColor Green

# --- 3. Generate logo ICO if missing ---
Write-Host "[4/5] Generating setup icon..." -ForegroundColor Yellow
$SetupIcon = Join-Path $Root "docs\assets\guardpulse-logo.ico"
if (-not (Test-Path $SetupIcon)) {
    Add-Type -AssemblyName System.Drawing
    $srcImg = [System.Drawing.Image]::FromFile($LogoPng)
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $pngs = @()
    foreach ($sz in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($sz, $sz)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($srcImg, 0, 0, $sz, $sz)
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += @{ Size = $sz; Bytes = $ms.ToArray() }
        $bmp.Dispose(); $ms.Dispose()
    }
    $fs = [System.IO.File]::Create($SetupIcon)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
    $offset = 6 + 16 * $pngs.Count
    foreach ($p in $pngs) {
        $b = if ($p.Size -eq 256) { 0 } else { $p.Size }
        $bw.Write([Byte]$b); $bw.Write([Byte]$b)
        $bw.Write([Byte]0); $bw.Write([Byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$p.Bytes.Length); $bw.Write([UInt32]$offset)
        $offset += $p.Bytes.Length
    }
    foreach ($p in $pngs) { $bw.Write($p.Bytes) }
    $bw.Close(); $fs.Close(); $srcImg.Dispose()
    Write-Host "  Generated $SetupIcon" -ForegroundColor Green
} else {
    Write-Host "  Already exists" -ForegroundColor Green
}

# --- 4. Invoke ISCC ---
Write-Host "[5/5] Building installer with Inno Setup..." -ForegroundColor Yellow
$IssFile = Join-Path $InstallerDir "installer.iss"
if (-not (Test-Path $Issc)) { throw "ISCC.exe not found at $Issc" }

& $Issc /O"$OutputDir" "$IssFile"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

$ExePath = Join-Path $OutputDir "DeviceServiceSetup-0.2.26.exe"
if (Test-Path $ExePath) {
    Write-Host ""
    Write-Host "=== BUILD COMPLETE ===" -ForegroundColor Green
    Write-Host "  Output: $ExePath ($([math]::Round((Get-Item $ExePath).Length / 1MB, 1)) MB)" -ForegroundColor Cyan
} else {
    # Check for versioned name
    $found = Get-ChildItem $OutputDir -Filter "*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($found) {
        Write-Host ""
        Write-Host "=== BUILD COMPLETE ===" -ForegroundColor Green
        Write-Host "  Output: $($found.FullName) ($([math]::Round($found.Length / 1MB, 1)) MB)" -ForegroundColor Cyan
    } else {
        throw "No output exe found in $OutputDir"
    }
}

