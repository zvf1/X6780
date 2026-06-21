#requires -version 5.1
<#
.SYNOPSIS
  lzhwctrl for Windows — uninstaller.

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

# ---- 2. Remove scheduled task ----
Info "Removing scheduled task '$TaskName'..."
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Ok "Scheduled task removed."
} else {
    Warn "Scheduled task not found (safe to ignore)."
}

# ---- 3. Remove install directory ----
Info "Removing $InstallDir ..."
if (Test-Path $InstallDir) {
    Remove-Item -Recurse -Force $InstallDir
    Ok "Removed $InstallDir."
} else {
    Warn "$InstallDir not found (safe to ignore)."
}

# ---- Done ----
Write-Host ""
Write-Host "Uninstall complete." -ForegroundColor Green
Write-Host ""
Write-Host "  Note: this does not unload the inpoutx64 kernel driver service,"
Write-Host "  since it's shared infrastructure other apps might also use."
Write-Host "  If you want it gone too: sc.exe stop inpoutx64; sc.exe delete inpoutx64"
Write-Host ""
Write-Host "  To reinstall:"
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
