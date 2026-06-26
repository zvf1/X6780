# lzhwctrl for Windows

System tray utility for the Clevo P65/P67RGRERA (i7-6700HQ / GTX 980M) that
controls fan curves, keyboard backlight, and CPU frequency cap from a right-click
menu. Installs and uninstalls with a single command.

## Install

Run this in an elevated PowerShell (right-click -> Run as Administrator):

```powershell
irm https://raw.githubusercontent.com/zvf1/X6780/main/win/install.ps1 | iex
```

The installer:
1. Downloads the repo and builds the app with `dotnet publish`
2. Installs to `C:\Program Files\LzHwCtrl`
3. Installs and starts two kernel driver services:
   - `inpoutx64` -- raw EC port I/O (fan control, CPU freq)
   - `SvThANSP` -- Clevo WMI provider (keyboard backlight)
4. Registers the Clevo WMI class definitions (`mofcomp clevo.bmf`)
5. Registers a Task Scheduler task to autostart at logon (elevated)

## Uninstall

```powershell
irm https://raw.githubusercontent.com/zvf1/X6780/main/win/uninstall.ps1 | iex
```

Stops and removes both driver services, removes the WMI class registrations,
removes the scheduled task, and deletes the install directory. `LzHwCtrl.sys`
(the inpoutx64 driver file) is locked by Windows until the next reboot, so
if the install folder isn't fully removed immediately, it cleans up on reboot.

## What it controls

### Fan speed

Direct EC port I/O via `inpoutx64.dll` (ports `0x62`/`0x66`). A background
control loop reads CPU and GPU temps from LibreHardwareMonitorLib and drives
fan duty cycle against the same curve as the Linux version. Menu buttons set
a fixed duty % or return to EC auto control.

### Keyboard backlight

Six buttons: Off, 1, 2, 3, 4, 5 (brightness levels).

The P65/P67's white backlight is controlled by the `SetWhiteLedKB` WMI method
on the `CLEVO_GET` class in `root\wmi`. This class is provided by `SvThANSP.sys`
(a Clevo kernel driver from the original Control Center package) once its WMI
class schema is compiled into the WMI repository via `mofcomp clevo.bmf`.

The implementation (`Keyboard.cs`) uses `System.Management` to call
`SetWhiteLedKB(Data: UInt16)` directly -- no IOCTL, no custom driver code.

### CPU frequency cap

Sets the Windows `PROCTHROTTLEMAX` power setting (max processor state as a %
of nominal max) via `powercfg`. Buttons: Auto (100%), 60%, 70%, 80%, 90%.
There is no direct kHz cap equivalent on Windows, so percentages are used
instead of the kHz values the Linux version uses.

## How the keyboard backlight dependency works

Without `SvThANSP.sys` loaded and `clevo.bmf` compiled into WMI, the
`CLEVO_GET` class does not exist, which means backlight won't work.
The install script handles both. If you want to verify manually:

```powershell
# Should return an object with InstanceName, not an error
Get-WmiObject -Namespace "root\wmi" -Class "CLEVO_GET"

# Should turn the backlight off
$obj = Get-WmiObject -Namespace "root\wmi" -Class "CLEVO_GET"
$p = $obj.GetMethodParameters("SetWhiteLedKB")
$p["Data"] = [uint16]0
$obj.InvokeMethod("SetWhiteLedKB", $p, $null)
```

## Files in win/

| File | Purpose |
|------|---------|
| `*.cs` | C# source (WinForms app) |
| `LzHwCtrl.csproj` | .NET 8 project file |
| `app.manifest` | Requests UAC elevation at launch |
| `inpoutx64.dll` | Usermode shim for the inpoutx64 kernel driver (EC port I/O) |
| `SvThANSP.sys` | Clevo kernel driver -- provides CLEVO_GET WMI class |
| `clevo.bmf` | Binary MOF extracted from Clevo installer -- defines CLEVO_GET schema |
| `install.ps1` | One-command installer |
| `uninstall.ps1` | One-command uninstaller |

## Build manually

Requires .NET 8 SDK and Windows x64.

```powershell
cd win
dotnet publish -c Release -r win-x64 --self-contained false
```

Put `inpoutx64.dll` next to the published `LzHwCtrl.exe`, then install the
two driver services and run `mofcomp clevo.bmf` as described in `install.ps1`.

## Notes

- The app must run elevated (UAC) for EC port I/O and driver management.
  The scheduled task handles this at logon automatically.
- Closing via tray -> Exit resets fans to EC auto control. Killing the process
  via Task Manager skips that reset (same behaviour as the Linux version).
- LibreHardwareMonitorLib sensor label text can vary by version. If temps read
  as zero, check what labels appear in the LibreHardwareMonitor GUI for your
  i7-6700HQ + GTX 980M and adjust the sensor name matching in `Sensors.cs`.
