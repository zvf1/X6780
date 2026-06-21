#requires -version 5.1
<#
.SYNOPSIS
  lzhwctrl for Windows — installer.

.DESCRIPTION
  Windows counterpart to arch/eosinstall.sh and mint/mintinstall.sh.
  Downloads the repo, builds the LzHwCtrl WinForms app with dotnet, stages
  inpoutx64.dll + LibreHardwareMonitorLib next to the exe, registers a
  Task Scheduler entry to autostart it elevated at logon (the Windows
  equivalent of the XDG autostart .desktop entry the Linux installers
  write), and launches it once to confirm everything works.

.USAGE
  Run elevated PowerShell, then:
    irm https://raw.githubusercontent.com/zvf1/X6780/main/win/install.ps1 | iex

  Or download + run locally:
    iwr https://raw.githubusercontent.com/zvf1/X6780/main/win/install.ps1 -OutFile install.ps1
    .\install.ps1
#>

# ---- Colour helpers (PowerShell equivalent of the bash info/warn/die/ok) ----
function Info { param($msg) Write-Host "[*] $msg" -ForegroundColor Green }
function Warn { param($msg) Write-Host "[!] $msg" -ForegroundColor Yellow }
function Ok   { param($msg) Write-Host "[OK] $msg" -ForegroundColor Green }
function Die  {
    param($msg)
    Write-Host "[X] $msg" -ForegroundColor Red
    Write-Host ""
    Write-Host "(press Enter to close this window)"
    Read-Host | Out-Null
    exit 1
}

# ---- Constants ----
$RepoZip    = "https://github.com/zvf1/X6780/archive/refs/heads/main.zip"
$InstallDir = "$env:ProgramFiles\LzHwCtrl"
$ExePath    = "$InstallDir\LzHwCtrl.exe"
$TaskName   = "LzHwCtrl"
$WorkDir    = "$env:TEMP\X6780-build"

# NOTE: this assumes the repo layout under win/ is:
#   win/*.cs, LzHwCtrl.csproj, app.manifest
#   win/inpoutx64.dll
#   win/LibreHardwareMonitor/*.dll
# If you've reorganised the repo since this script was written, adjust
# $ProjectSubdir / $DllSrc / $LhmSrc below to match.
$ProjectSubdir = "win"
$DllSrc        = "win\inpoutx64.dll"
$LhmSrc        = "win\LibreHardwareMonitor"

try {

# ---- Sanity checks ----
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Die "Run this from an elevated PowerShell (Run as Administrator). Raw EC port I/O and writing to Program Files both require it."
}

Write-Host ""
Write-Host "  lzhwctrl for Windows - installer"
Write-Host "  Install dir : $InstallDir"
Write-Host "  Build dir   : $WorkDir"
Write-Host ""

# ---- 1. Check for .NET SDK ----
$dotnetOk = $false
try {
    $dotnetVer = (dotnet --version) 2>$null
    if ($LASTEXITCODE -eq 0 -and $dotnetVer) { $dotnetOk = $true }
} catch {}

if (-not $dotnetOk) {
    Info ".NET SDK not found - attempting install via winget..."
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
        $dotnetVer = (dotnet --version) 2>$null
        if (-not $dotnetVer) {
            Die "winget install finished but 'dotnet' still isn't on PATH. Open a new terminal and re-run this script."
        }
    } else {
        Die "winget not available. Install the .NET 8 SDK manually from https://dotnet.microsoft.com/download then re-run this script."
    }
}
Ok ".NET SDK $((dotnet --version)) available."

# ---- 2. Fetch the repo ----
Info "Downloading repo from $RepoZip ..."
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$zipPath = "$WorkDir\X6780.zip"
Invoke-WebRequest -Uri $RepoZip -OutFile $zipPath -UseBasicParsing

Info "Extracting..."
$extractDir = "$WorkDir\extracted"
if (Test-Path $extractDir) { Remove-Item -Recurse -Force $extractDir }
Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

$repoRoot = Get-ChildItem -Path $extractDir -Directory | Select-Object -First 1
if (-not $repoRoot) { Die "Could not find extracted repo root under $extractDir." }

$projectDir = Join-Path $repoRoot.FullName $ProjectSubdir
$dllSrcPath = Join-Path $repoRoot.FullName $DllSrc
$lhmSrcDir  = Join-Path $repoRoot.FullName $LhmSrc

if (-not (Test-Path $projectDir)) {
    Die "Expected project at $projectDir but it doesn't exist. The repo layout may have changed - check win/ and update `$ProjectSubdir at the top of this script."
}
Ok "Repo fetched, project found at $projectDir"

# ---- 3. Build ----
Info "Building LzHwCtrl (dotnet publish, this takes a moment)..."
$publishDir = "$WorkDir\publish"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

Push-Location $projectDir
try {
    dotnet publish -c Release -r win-x64 --self-contained false -o $publishDir
    if ($LASTEXITCODE -ne 0) { Die "dotnet publish failed - see output above." }
} finally {
    Pop-Location
}
Ok "Build complete."

# ---- 4. Stage dependencies (inpoutx64.dll, LibreHardwareMonitorLib) ----
Info "Staging inpoutx64.dll..."
if (Test-Path $dllSrcPath) {
    Copy-Item $dllSrcPath -Destination $publishDir -Force
    Ok "inpoutx64.dll staged."
} else {
    Warn "inpoutx64.dll not found at $dllSrcPath - the app will fail to start without it. Copy it into $InstallDir manually."
}

# LibreHardwareMonitorLib.dll is normally pulled in automatically by the
# NuGet PackageReference during dotnet publish above. Only copy manually
# from the repo's prebuilt copy if publish somehow didn't produce it
# (e.g. you're offline and NuGet restore failed).
if (-not (Test-Path "$publishDir\LibreHardwareMonitorLib.dll")) {
    Warn "LibreHardwareMonitorLib.dll missing from publish output - copying repo's prebuilt copy instead..."
    if (Test-Path $lhmSrcDir) {
        Copy-Item "$lhmSrcDir\*.dll" -Destination $publishDir -Force
        Ok "Copied LibreHardwareMonitor DLLs from repo."
    } else {
        Warn "Could not find $lhmSrcDir either - sensor reads will fail until LibreHardwareMonitorLib.dll is present in $InstallDir."
    }
}

# ---- 5. Install ----
Info "Installing to $InstallDir ..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item "$publishDir\*" -Destination $InstallDir -Recurse -Force
Ok "Installed to $InstallDir"

# ---- 6. Scheduled Task autostart (Windows equivalent of the XDG autostart
#         .desktop entry the Linux installers write) ----
# Runs at logon, elevated (RunLevel Highest), as the installing user.
Info "Registering autostart scheduled task '$TaskName' ..."
$action    = New-ScheduledTaskAction -Execute $ExePath
$trigger   = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -RunLevel Highest -LogonType Interactive
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null
Ok "Scheduled task registered - will launch elevated at next logon."

# ---- 7. Launch now to verify ----
Info "Launching $ExePath to verify the install (you'll get a UAC prompt - accept it)..."
try {
    Start-Process -FilePath $ExePath
    Start-Sleep -Seconds 2
    Ok "Launched. Check the system tray for the lzhwctrl icon."
} catch {
    Warn "Could not auto-launch: $($_.Exception.Message). Launch manually: $ExePath"
}

# ---- Done ----
Write-Host ""
Write-Host "Installation complete." -ForegroundColor Green
Write-Host ""
Write-Host "  Installed to : $InstallDir"
Write-Host "  Autostart    : Task Scheduler -> '$TaskName' (runs at logon, elevated)"
Write-Host ""
Write-Host "  To launch manually:"
Write-Host "    $ExePath"
Write-Host ""
Write-Host "  To uninstall:"
Write-Host "    irm https://raw.githubusercontent.com/zvf1/X6780/main/win/uninstall.ps1 | iex"
Write-Host ""

} catch {
    Write-Host ""
    Write-Host "[X] Unexpected error: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.InvocationInfo -and $_.InvocationInfo.PositionMessage) {
        Write-Host $_.InvocationInfo.PositionMessage -ForegroundColor DarkGray
    }
    Write-Host ""
    Write-Host "(press Enter to close this window)"
    Read-Host | Out-Null
    exit 1
}
