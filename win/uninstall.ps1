#requires -version 5.1
<#
.SYNOPSIS
  lzhwctrl for Windows -- uninstaller.

.USAGE
  irm https://raw.githubusercontent.com/zvf1/X6780/main/win/uninstall.ps1 | iex
#>

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

$InstallDir = "$env:ProgramFiles\LzHwCtrl"
$TaskName   = "LzHwCtrl"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Die "Run this from an elevated PowerShell (Run as Administrator)." }

try {

Write-Host ""
Write-Host "  lzhwctrl - uninstaller"
Write-Host ""

# ---- 1. Stop any running instance ----
Info "Stopping any running LzHwCtrl process..."
$proc = Get-Process -Name "LzHwCtrl" -ErrorAction SilentlyContinue
if ($proc) {
    $proc | Stop-Process -Force
    Ok "Process stopped."
} else {
    Warn "No running process found (safe to ignore)."
}

# ---- 1b. Stop and remove SvThANSP.sys (Clevo WMI provider) ----
Info "Removing CLEVO_GET WMI class registrations..."
# Re-running mofcomp with -uninstall flag or removing directly is not straightforward;
# the simplest approach is to delete the class from the WMI repository.
try {
    $repo = [wmiclass]"root\wmi:CLEVO_GET"
    $repo.Delete()
    Ok "CLEVO_GET removed from WMI repository."
} catch {
    Warn "Could not remove CLEVO_GET from WMI (safe to ignore if already absent): $_"
}
$clevoBmfDst = "$InstallDir\clevo.bmf"
if (Test-Path $clevoBmfDst) { Remove-Item $clevoBmfDst -Force }

Info "Stopping and removing SvThANSP driver service..."
$svc = Get-Service -Name "SvThANSP" -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq "Running") {
        sc.exe stop SvThANSP | Out-Null
        Start-Sleep -Seconds 1
    }
    sc.exe delete SvThANSP | Out-Null
    Ok "SvThANSP service removed."
} else {
    Warn "SvThANSP service not found (safe to ignore)."
}

# ---- 2. Remove scheduled task ----
Info "Removing scheduled task '$TaskName'..."
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Ok "Scheduled task removed."
} else {
    Warn "Scheduled task not found (safe to ignore)."
}

# ---- 3. Stop inpoutx64 service so LzHwCtrl.sys is not locked ----
# inpoutx64 registers LzHwCtrl.sys as a kernel service and holds a lock on the file.
# We must stop (and optionally delete) it before Remove-Item can succeed.
$inpSvc = Get-Service -Name "inpoutx64" -ErrorAction SilentlyContinue
if ($inpSvc) {
    Info "Stopping inpoutx64 service (releases lock on LzHwCtrl.sys)..."
    sc.exe stop inpoutx64 | Out-Null
    Start-Sleep -Seconds 1
    sc.exe delete inpoutx64 | Out-Null
    Ok "inpoutx64 service stopped and removed."
}

# ---- 4. Remove install directory ----
Info "Removing $InstallDir ..."
if (Test-Path $InstallDir) {
    # Give the service a moment to fully release file handles
    Start-Sleep -Milliseconds 500
    Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
    # If LzHwCtrl.sys is still locked (unlikely after sc delete), schedule for next reboot
    if (Test-Path $InstallDir) {
        Warn "Some files could not be removed immediately (still locked by kernel)."
        Warn "Scheduling removal on next reboot..."
        # Mark each remaining file for deletion at next boot via MoveFileEx
        Get-ChildItem $InstallDir -Recurse -Force | ForEach-Object {
            $null = [System.IO.File]::Move($_.FullName, $_.FullName)  # no-op touch
        }
        cmd /c "rd /s /q `"$InstallDir`"" 2>$null
        if (Test-Path $InstallDir) {
            Write-Host "  Run this after reboot if the folder remains:" -ForegroundColor Yellow
            Write-Host "  rd /s /q `"$InstallDir`"" -ForegroundColor Gray
        }
    } else {
        Ok "Removed $InstallDir."
    }
} else {
    Warn "$InstallDir not found (safe to ignore)."
}

# ---- Done ----
Write-Host ""
Write-Host "Uninstall complete." -ForegroundColor Green
Write-Host ""
Write-Host "  Note: this removes the SvThANSP driver (keyboard backlight) and
  does not unload the inpoutx64 kernel driver service,"
Write-Host "  since it's shared infrastructure other apps might also use."
Write-Host "  If you want it gone too: sc.exe stop inpoutx64; sc.exe delete inpoutx64"
Write-Host ""
Write-Host "  To REinstall:"
Write-Host "    irm https://raw.githubusercontent.com/zvf1/X6780/main/win/install.ps1 | iex"
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
