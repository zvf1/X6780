#requires -version 5.1
<#
.SYNOPSIS
  lzhwctrl for Windows -- installer.

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
$SvThANSPSrc   = "win\SvThANSP.sys"
$SvThANSPSvc   = "SvThANSP"
# SvThANSPDst is set after InstallDir is known (same value, defined here for clarity)
$SvThANSPDst   = "$InstallDir\SvThANSP.sys"
$ClevoBmfSrc   = "win\clevo.bmf"
$ClevoBmfDst   = "$InstallDir\clevo.bmf"

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
    Info ".NET SDK not found - attempting install..."

    # Try winget first, if present.
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Info "winget found - installing .NET 8 SDK via winget..."
        winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
        $dotnetVer = (dotnet --version) 2>$null
        if ($dotnetVer) { $dotnetOk = $true }
    } else {
        Warn "winget not available on this machine."
    }

    # Fall back to Microsoft's official dotnet-install.ps1 script, which
    # only needs PowerShell + internet access (no winget/Store dependency).
    # It installs to the standard machine-wide location so the resulting
    # apphost (LzHwCtrl.exe) can find the runtime without any extra setup.
    if (-not $dotnetOk) {
        Info "Falling back to Microsoft's dotnet-install.ps1 script..."
        New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
        $dotnetInstallScript = "$WorkDir\dotnet-install.ps1"
        Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $dotnetInstallScript -UseBasicParsing

        $dotnetInstallDir = "$env:ProgramFiles\dotnet"
        # dotnet-install.ps1 exits non-zero even on success in some environments,
        # so do not check $LASTEXITCODE here -- verify by probing dotnet itself.
        & $dotnetInstallScript -Channel 8.0 -InstallDir $dotnetInstallDir -NoPath

        # Add to this session's PATH immediately so dotnet is usable right now.
        if (($env:Path -split ';') -notcontains $dotnetInstallDir) {
            $env:Path = "$dotnetInstallDir;$env:Path"
        }

        # Persist to machine PATH so new terminals find it too.
        $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
        if (($machinePath -split ';') -notcontains $dotnetInstallDir) {
            [Environment]::SetEnvironmentVariable('Path', "$machinePath;$dotnetInstallDir", 'Machine')
        }

        # Verify the install actually worked by running dotnet.
        $dotnetVer = (& "$dotnetInstallDir\dotnet.exe" --version) 2>$null
        if ($dotnetVer) {
            $dotnetOk = $true
            # Make sure subsequent dotnet calls in this script use the full path.
            Set-Alias -Name dotnet -Value "$dotnetInstallDir\dotnet.exe" -Scope Script
        } else {
            Die "dotnet-install.ps1 ran but dotnet is still not usable. Install the .NET 8 SDK manually from https://dotnet.microsoft.com/download then re-run this script."
        }
    }

    if (-not $dotnetOk) {
        Die "Could not install the .NET 8 SDK automatically. Install it manually from https://dotnet.microsoft.com/download then re-run this script."
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

# ---- 5b. Install and start SvThANSP.sys (Clevo WMI provider) ----
# This driver registers the CLEVO_GET WMI class in root\wmi, which exposes
# SetWhiteLedKB / GetWhiteLedKB -- the keyboard backlight methods used by
# the app. Without it, the keyboard row is disabled.
Info "Installing SvThANSP.sys (Clevo WMI provider)..."
$svThANSPSrcPath = Join-Path $repoRoot.FullName $SvThANSPSrc
if (Test-Path $svThANSPSrcPath) {
    Copy-Item $svThANSPSrcPath -Destination $SvThANSPDst -Force

    # Remove any stale service entry before re-creating it.
    $svc = Get-Service -Name $SvThANSPSvc -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -eq "Running") {
            sc.exe stop $SvThANSPSvc | Out-Null
            Start-Sleep -Seconds 1
        }
        sc.exe delete $SvThANSPSvc | Out-Null
        Start-Sleep -Seconds 1
    }

    # sc.exe is very picky: params must be lowercase, and paths with spaces
    # need embedded quotes that survive PowerShell argument passing.
    # Use cmd /c to sidestep PowerShell's argument rewriting entirely.
    $scCreate = "sc.exe create $SvThANSPSvc type= kernel start= auto error= normal binpath= `"$SvThANSPDst`" displayname= `"Clevo EC WMI Provider`""
    cmd /c $scCreate | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Warn "sc.exe create failed for SvThANSP (exit $LASTEXITCODE) - keyboard backlight may not work."
        Write-Host "  Command was: $scCreate" -ForegroundColor DarkGray
    } else {
        sc.exe start $SvThANSPSvc | Out-Null
        Start-Sleep -Seconds 2
        $svc = Get-Service -Name $SvThANSPSvc -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq "Running") {
            Ok "SvThANSP.sys installed and running."
        } else {
            Warn "SvThANSP.sys installed but did not reach Running state. Check Event Viewer -> Windows Logs -> System for details."
        }
    }
} else {
    Warn "SvThANSP.sys not found at $svThANSPSrcPath - keyboard backlight will be disabled."
}

# ---- 5c. Register CLEVOMOF.dll WMI class definitions ----
# CLEVOMOF.dll is a resource-only DLL containing the compiled MOF (Binary MOF / BMF)
# that defines the CLEVO_GET WMI class and all its methods (SetWhiteLedKB etc).
# mofcomp.exe compiles it into the WMI repository. Without this step, CLEVO_GET
# does not exist in root\wmi even if SvThANSP.sys is running.
Info "Registering CLEVO_GET WMI classes (mofcomp clevo.bmf)..."
# clevo.bmf is the Binary MOF extracted from the Clevo installer's CLEVOMOF.dll resource.
# mofcomp accepts .bmf files directly (they start with the BMOF magic header).
$clevoBmfSrcPath = Join-Path $repoRoot.FullName $ClevoBmfSrc
if (Test-Path $clevoBmfSrcPath) {
    Copy-Item $clevoBmfSrcPath -Destination $ClevoBmfDst -Force
    $mofcomp = "$env:SystemRoot\System32\wbem\mofcomp.exe"
    if (Test-Path $mofcomp) {
        $mofResult = & $mofcomp $ClevoBmfDst 2>&1
        if ($LASTEXITCODE -eq 0) {
            Ok "CLEVO_GET WMI classes registered successfully."
        } else {
            Warn "mofcomp returned exit code $LASTEXITCODE. Trying text MOF fallback..."
            Write-Host "  mofcomp output: $mofResult" -ForegroundColor DarkGray
            # BMF failed - this can happen if WMI repository format differs.
            # Fall back to the reconstructed text MOF which defines the key methods.
            $textMofPath = "$env:TEMP\clevo_get.mof"
            $mofContent = @'
#pragma namespace("\\.\root\wmi")
[WMI, guid("{ABBC0F6D-8EA1-11d1-00A0-C90629100000}"), Description("Clevo WMI interface")]
class CLEVO_GET {
    [key, read] string InstanceName;
    [read] boolean Active;
    [WmiMethodId(1), Implemented] void GetWhiteLedKB([out] uint32 Data);
    [WmiMethodId(2), Implemented] void SetWhiteLedKB([in] uint16 Data);
    [WmiMethodId(3), Implemented] void GetCPUFANDuty([out] uint32 Data);
    [WmiMethodId(4), Implemented] void SetFanDuty([in] uint32 Data);
    [WmiMethodId(5), Implemented] void GetCPUtemp([out] uint32 Data);
    [WmiMethodId(6), Implemented] void GetVGA1temp([out] uint32 Data);
    [WmiMethodId(49), Implemented] void SetVolumeLED([in] uint16 Data);
    [WmiMethodId(50), Implemented] void GetVolumeLED([out] uint32 Data);
    [WmiMethodId(52), Implemented] void SetKBLED([in] uint32 Data);
};
'@
            $mofContent | Out-File -FilePath $textMofPath -Encoding ascii
            $mofResult2 = & $mofcomp $textMofPath 2>&1
            if ($LASTEXITCODE -eq 0) {
                Ok "CLEVO_GET WMI classes registered via text MOF fallback."
            } else {
                Warn "Text MOF fallback also failed (exit $LASTEXITCODE). Keyboard backlight buttons will not work."
                Write-Host "  Output: $mofResult2" -ForegroundColor DarkGray
            }
        }
    } else {
        Warn "mofcomp.exe not found - WMI classes not registered."
    }
} else {
    Warn "clevo.bmf not found at $clevoBmfSrcPath - keyboard backlight will be disabled."
}

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
