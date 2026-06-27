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
# LzHwCtrl.sys may still be locked by the kernel even after sc delete --
# Windows holds the file handle until the next SCM cycle. We use MoveFileEx
# with MOVEFILE_DELAY_UNTIL_REBOOT (flag 4) to schedule any locked files for
# deletion at the very start of the next boot, before services load.
# This is the same mechanism Windows Installer uses for locked-file cleanup.

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class BootDeleter {
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool MoveFileExW(string src, string dst, uint flags);
    public static bool ScheduleDelete(string path) {
        // flags=4 = MOVEFILE_DELAY_UNTIL_REBOOT; dst=null means delete
        return MoveFileExW(path, null, 4);
    }
    public static int LastError() {
        return System.Runtime.InteropServices.Marshal.GetLastWin32Error();
    }
}
"@

function Remove-OrSchedule([string]$Path) {
    Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $Path) {
        # File still locked -- use MoveFileEx to delete it at next boot startup,
        # before any services load. Pass the path as a verbatim \?\ extended path
        # to avoid any backslash interpretation issues (error 3 = path not found).
        $extPath = "\\?\" + $Path.TrimStart("\")
        $ok = [BootDeleter]::ScheduleDelete($extPath)
        if ($ok) {
            Write-Host "  Will be removed at next boot: $Path" -ForegroundColor DarkGray
        } else {
            $err = [BootDeleter]::LastError()
            Warn "Could not schedule deletion of $(Split-Path $Path -Leaf) (Win32 error $err)"
        }
    }
}

Info "Removing $InstallDir ..."
if (Test-Path -LiteralPath $InstallDir) {
    Start-Sleep -Milliseconds 500

    # Files first, longest path first (deepest), so parent dirs empty before we touch them
    Get-ChildItem -LiteralPath $InstallDir -Recurse -Force |
        Sort-Object { $_.FullName.Length } -Descending |
        Where-Object { -not $_.PSIsContainer } |
        ForEach-Object { Remove-OrSchedule $_.FullName }

    # Directories (also deepest first)
    Get-ChildItem -LiteralPath $InstallDir -Recurse -Force |
        Sort-Object { $_.FullName.Length } -Descending |
        Where-Object { $_.PSIsContainer } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }

    Remove-Item -LiteralPath $InstallDir -Force -Recurse -ErrorAction SilentlyContinue

    if (Test-Path -LiteralPath $InstallDir) {
        Ok "Removed $InstallDir (one locked file queued for deletion at next reboot -- folder will be gone after restart)."
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
Write-Host "  Note: this removes both the SvThANSP (keyboard backlight) and inpoutx64 (EC port I/O) driver services."
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
