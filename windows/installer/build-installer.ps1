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

# --- 0. Firebase config coherence guard ---
# The installer pre-fills from firebase-local.iss; the Android side reads
# firebase.local.properties. Both must hold the SAME project triple, and the
# URL must belong to the project — a mismatch (US key + SG URL) produced an
# agent that signed in fine and failed every cloud write silently.
Write-Host "[0/5] Validating Firebase config coherence..." -ForegroundColor Yellow
$FirebaseLocalIss = Join-Path $InstallerDir "firebase-local.iss"
$FirebaseProps = Join-Path $Root "firebase.local.properties"
if (-not (Test-Path $FirebaseLocalIss)) { throw "firebase-local.iss not found - the installer would ship a placeholder API key" }
$issKey = (Select-String -Path $FirebaseLocalIss -Pattern 'FirebaseApiKey\s+"([^"]+)"' | Select-Object -First 1).Matches.Groups[1].Value
if ([string]::IsNullOrWhiteSpace($issKey)) { throw "FirebaseApiKey not found in firebase-local.iss" }
if ($issKey -match 'REPLACE_WITH|__') { throw "firebase-local.iss still holds a placeholder API key" }
$propsKey = $null; $propsProject = $null; $propsUrl = $null
foreach ($line in (Get-Content $FirebaseProps)) {
    if ($line -match '^\s*firebase\.apiKey\s*=\s*(.+?)\s*$') { $propsKey = $Matches[1] }
    if ($line -match '^\s*firebase\.projectId\s*=\s*(.+?)\s*$') { $propsProject = $Matches[1] }
    if ($line -match '^\s*firebase\.databaseUrl\s*=\s*(.+?)\s*$') { $propsUrl = $Matches[1] }
}
if ((Test-Path $FirebaseProps) -and $propsKey -and ($propsKey -ne $issKey)) {
    throw "Firebase API key mismatch: firebase-local.iss ($($issKey.Substring(0,12))...) vs firebase.local.properties ($($propsKey.Substring(0,12))...). Keep the two files in sync."
}
# The .iss also hardcodes the wizard prefill for projectId/databaseUrl - read them back from installer.iss and check the URL contains the project.
$issProject = (Select-String -Path (Join-Path $InstallerDir "installer.iss") -Pattern "FirebasePage.Values\[1\] := '([^']+)'").Matches.Groups[1].Value
$issUrl = (Select-String -Path (Join-Path $InstallerDir "installer.iss") -Pattern "FirebasePage.Values\[2\] := '([^']+)'").Matches.Groups[1].Value
if ($issUrl -notlike "*$issProject*") { throw "installer.iss wizard prefill is incoherent: databaseUrl does not contain project '$issProject'" }
if ($propsUrl -and $propsProject -and ($propsUrl -notlike "*$propsProject*")) { throw "firebase.local.properties is incoherent: databaseUrl does not contain project '$propsProject'" }
Write-Host "  OK (project $issProject)" -ForegroundColor Green

# --- 1. Publish both projects (clean: dotnet publish accumulates, so a
#        removed feature would keep shipping from stale intermediates) ---
Write-Host "`n[1/5] Publishing GuardPulse.Agent.Service..." -ForegroundColor Yellow
Remove-Item -Path (Join-Path $WinDir "publish\service") -Recurse -Force -ErrorAction SilentlyContinue
& $DotNet publish (Join-Path $WinDir "src\GuardPulse.Agent.Service\GuardPulse.Agent.Service.csproj") `
    -c $Configuration -r win-x64 --self-contained `
    -p:PublishReadyToRun=true --nologo -v q `
    -o (Join-Path $WinDir "publish\service") 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Service publish failed ($LASTEXITCODE)" }
Write-Host "  OK" -ForegroundColor Green

Write-Host "[2/5] Publishing GuardPulse.Agent.Session..." -ForegroundColor Yellow
Remove-Item -Path (Join-Path $WinDir "publish\session") -Recurse -Force -ErrorAction SilentlyContinue
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
# PDBs ship source paths on disk for no benefit in a de-branded installer.
Get-ChildItem $PublishDir -Filter "*.pdb" -Recurse | Remove-Item -Force

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

# Version single-source: gradle.properties (guardpulse.versionName/versionCode) wins.
# ISCC /D overrides the #define defaults in installer.iss.
$VersionName = $null
$VersionCode = $null
foreach ($line in (Get-Content (Join-Path $Root "gradle.properties"))) {
    if ($line -match '^\s*guardpulse\.versionName\s*=\s*(.+?)\s*$') { $VersionName = $Matches[1] }
    if ($line -match '^\s*guardpulse\.versionCode\s*=\s*(.+?)\s*$') { $VersionCode = $Matches[1] }
}
if ([string]::IsNullOrWhiteSpace($VersionName)) { throw "guardpulse.versionName not found in gradle.properties" }
if ([string]::IsNullOrWhiteSpace($VersionCode)) { throw "guardpulse.versionCode not found in gradle.properties" }

& $Issc /O"$OutputDir" /DAppVersion="$VersionName" /DAppVersionCode="$VersionCode" "$IssFile"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

$ExePath = Join-Path $OutputDir "DeviceServiceSetup-$VersionName.exe"
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

