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

$ExePath = Join-Path $OutputDir "DeviceServiceSetup-0.2.15.exe"
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

