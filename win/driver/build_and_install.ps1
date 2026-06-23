#requires -version 5.1
<#
.SYNOPSIS
  Builds, test-signs, and installs the Clevo keyboard backlight driver.

.DESCRIPTION
  1. Enables Test Signing mode (requires reboot once, then stays enabled).
  2. Creates a self-signed test certificate if one doesn't already exist.
  3. Builds clevo_kb.sys using MSBuild + WDK.
  4. Signs the driver with the test certificate.
  5. Installs the driver using pnputil.

  Run from an elevated PowerShell prompt inside the driver source directory.
  A reboot is required the first time Test Signing is enabled.

.NOTES
  The desktop watermark ("Test Mode") is normal and expected.
  To remove it later: bcdedit /set testsigning off  (+ reboot)
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Info { param($m) Write-Host "  [*] $m" -ForegroundColor Cyan }
function Ok   { param($m) Write-Host "  [OK] $m" -ForegroundColor Green }
function Die  { param($m) Write-Host "  [X] $m" -ForegroundColor Red; exit 1 }

# ── Must be elevated ─────────────────────────────────────────────────────────
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal   = New-Object Security.Principal.WindowsPrincipal($currentUser)
$isAdmin     = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Die "Run this script as Administrator." }

$ScriptDir = Split-Path $MyInvocation.MyCommand.Path

# ── 1. Enable Test Signing ───────────────────────────────────────────────────
Info "Checking Test Signing status..."
$bcdOut = bcdedit /enum "{current}" 2>&1 | Out-String
if ($bcdOut -notmatch "testsigning\s+Yes") {
    Info "Enabling Test Signing (requires one reboot)..."
    bcdedit /set testsigning on | Out-Null
    Write-Host ""
    Write-Host "  *** Test Signing has been enabled. ***" -ForegroundColor Yellow
    Write-Host "  *** Please REBOOT and then re-run this script. ***" -ForegroundColor Yellow
    Write-Host ""
    exit 0
}
Ok "Test Signing is already enabled."

# ── 2. Locate WDK / MSBuild tools ───────────────────────────────────────────
Info "Locating build tools..."

# Find MSBuild (VS 2022)
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { Die "vswhere.exe not found. Is Visual Studio installed?" }

$vsPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vsPath) { Die "Could not locate Visual Studio installation." }

$msbuild = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) { Die "MSBuild.exe not found at: $msbuild" }
Ok "MSBuild: $msbuild"

# Find signtool (ships with WDK / Windows SDK)
$sdkBin = "C:\Program Files (x86)\Windows Kits\10\bin"
$signtool = Get-ChildItem -Path $sdkBin -Recurse -Filter "signtool.exe" |
    Where-Object { $_.FullName -match "x64" } |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) { Die "signtool.exe not found under $sdkBin" }
Ok "signtool: $signtool"

# Find inf2cat (needed to create a .cat file for the INF)
$inf2cat = Get-ChildItem -Path $sdkBin -Recurse -Filter "inf2cat.exe" |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $inf2cat) { Die "inf2cat.exe not found under $sdkBin" }
Ok "inf2cat: $inf2cat"

# ── 3. Create test certificate ───────────────────────────────────────────────
$certSubject = "CN=ClevoKbTestCert"
$certStore   = "Cert:\LocalMachine\Root"
$certStoreMy = "Cert:\LocalMachine\My"

$cert = Get-ChildItem $certStoreMy -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $certSubject } |
    Select-Object -First 1

if (-not $cert) {
    Info "Creating self-signed test certificate..."
    $cert = New-SelfSignedCertificate `
        -Subject $certSubject `
        -CertStoreLocation $certStoreMy `
        -Type CodeSigningCert `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(10)
    Ok "Certificate created: $($cert.Thumbprint)"

    # Also install in Trusted Root and Trusted Publishers so the driver loads
    $rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        "Root", "LocalMachine")
    $rootStore.Open("ReadWrite")
    $rootStore.Add($cert)
    $rootStore.Close()

    $tpStore = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        "TrustedPublisher", "LocalMachine")
    $tpStore.Open("ReadWrite")
    $tpStore.Add($cert)
    $tpStore.Close()

    Ok "Certificate installed in Root + TrustedPublisher stores."
} else {
    Ok "Using existing certificate: $($cert.Thumbprint)"
}

# ── 4. Build ─────────────────────────────────────────────────────────────────
Info "Building clevo_kb.sys..."
$slnPath = Join-Path $ScriptDir "clevo_kb.sln"
& $msbuild $slnPath /p:Configuration=Release /p:Platform=x64 /v:minimal
if ($LASTEXITCODE -ne 0) { Die "MSBuild failed." }
Ok "Build succeeded."

$sysPath = Join-Path $ScriptDir "bin\x64\Release\clevo_kb.sys"
$infPath = Join-Path $ScriptDir "clevo_kb.inf"
if (-not (Test-Path $sysPath)) { Die "clevo_kb.sys not found at $sysPath" }

# ── 5. Generate .cat and sign ────────────────────────────────────────────────
Info "Generating catalog file..."
$infDir = Join-Path $ScriptDir "bin\x64\Release"
# Copy INF next to the sys for inf2cat
Copy-Item $infPath $infDir -Force

& $inf2cat /driver:"$infDir" /os:10_x64 2>&1 | Out-Null
$catPath = Join-Path $infDir "clevo_kb.cat"
if (-not (Test-Path $catPath)) { Die "inf2cat failed to produce clevo_kb.cat" }
Ok "Catalog created."

Info "Signing clevo_kb.sys and clevo_kb.cat..."
$thumb = $cert.Thumbprint
& $signtool sign /sha1 $thumb /fd SHA256 /t http://timestamp.digicert.com "$sysPath" 2>&1
& $signtool sign /sha1 $thumb /fd SHA256 /t http://timestamp.digicert.com "$catPath" 2>&1
Ok "Signing complete."

# ── 6. Install via pnputil ───────────────────────────────────────────────────
Info "Installing driver package..."
$result = pnputil /add-driver "$infDir\clevo_kb.inf" /install 2>&1 | Out-String
Write-Host $result

if ($LASTEXITCODE -ne 0) {
    # pnputil may return 3010 (reboot required) which is also success
    if ($LASTEXITCODE -ne 3010) {
        Die "pnputil failed (exit $LASTEXITCODE). Output above."
    }
}
Ok "Driver installed."

# ── 7. Start the driver service ──────────────────────────────────────────────
Info "Starting driver service..."
try {
    Start-Service "clevo_kb" -ErrorAction Stop
    Ok "Service started."
} catch {
    # Device may not be enumerated yet — that's fine, pnputil handles it
    Warn "Could not start service directly (may start automatically when ACPI enumerates CLV0001): $_"
}

Write-Host ""
Write-Host "  Driver installed successfully." -ForegroundColor Green
Write-Host "  Now run the lzhwctrl installer to pick up the new Keyboard.cs." -ForegroundColor Green
Write-Host ""
