# lzhwctrl for Windows 10 x64

A C# WinForms port of `lzhwctrl.py`, using the two dependencies you already
staged: `inpoutx64.dll` for raw EC port I/O (replaces `/dev/port`) and
LibreHardwareMonitorLib for CPU/GPU temps (replaces the hwmon lookups).

## What ported directly

- **EC fan control** (`EcPort.cs`) -- same protocol constants as Linux/your
  original Windows script: ports 0x62/0x66, `FAN_SET_CMD = 0x99`,
  `EC_AUTO_DUTY = 0xCC`, `EC_RELEASE_CMD = 0x98`. Only the I/O mechanism
  changed (`Inp32`/`Out32` instead of `/dev/port` reads/writes).
- **Fan curve & control loop** (`ControlLoop.cs`) -- identical thresholds
  to `FAN_CURVE` in the Python script.
- **Temps** (`Sensors.cs`) -- LibreHardwareMonitorLib instead of sysfs;
  picks the CPU "Package" sensor and a GPU "Core" sensor the same way the
  Linux version picked `coretemp`'s package input and `nouveau`'s temp1.

## What needed a different approach

- **No sudo/helper split.** Windows doesn't have a sudoers equivalent for
  "this one script, this one flag, no password." The whole app just runs
  elevated (UAC prompt via `app.manifest`'s `requireAdministrator`).
- **CPU freq cap** (`CpuFreq.cs`) -- there's no `cpupower`-style kHz cap on
  Windows. The closest analog is the `PROCTHROTTLEMAX` power setting (max
  processor state, as a % of nominal max), set via `powercfg`. I left the
  UI buttons as percentages instead of the kHz values your Python buttons
  used -- map them to whatever feels right for your i7-6700HQ's turbo
  range.
- **Keyboard backlight** (`Keyboard.cs`) -- **now implemented**, not a stub
  anymore. Turns out this board's backlight isn't an EC port write at all --
  `tuxedo-drivers`' `clevo_leds.h`/`clevo_wmi.c` showed it's a standard ACPI
  WMI control-method call (GUID `ABBC0F6D-8EA1-11D1-00A0-C90629100000`,
  method `0x27` to set / `0x3D` to get brightness). Windows' own
  `wmiacpi.sys` mapper reads the exact same firmware table and exposes a
  WMI class for it under `root\WMI` -- so this uses plain `System.Management`,
  no driver, no admin rights needed for this one piece.

  The class/method *names* Windows generates are BIOS-specific and weren't
  in the driver source, so `Keyboard.cs` finds them at runtime by matching
  the `guid` qualifier rather than hardcoding a guess. Run
  `discover-clevo-wmi.ps1` first (no admin needed) to see what your BIOS
  actually calls things, and sanity-check it lines up with what
  `Keyboard.cs` finds.

  One thing I genuinely can't verify without the hardware: whether this
  board's white backlight is the 3-step variant (off/half/full,
  `CLEVO_KBD_BRIGHTNESS_WHITE_MAX = 0x02`) or the 6-step variant used on
  some <=7th-gen boards (`..._MAX_5 = 0x05`). I assumed 6-step since your
  i7-6700HQ is 6th gen, matching the existing 0-5 button row 1:1. If only
  3 distinct brightness levels actually show up, halve/round the level
  before calling `SetLevel`.

## Build

Requires .NET 8 SDK and Windows (this won't compile cross-platform since
it's WinForms + COM-ish P/Invoke against a Windows-only DLL).

```
cd win/LzHwCtrl
dotnet restore
dotnet build -c Release -r win-x64
```

Then put `inpoutx64.dll` next to `LzHwCtrl.exe` in the build output
(`bin\Release\net8.0-windows\win-x64\`), or uncomment the `<None Include=...>`
block in `LzHwCtrl.csproj` to have the build copy it automatically.

Same for LibreHardwareMonitorLib: either let the NuGet package reference
in the `.csproj` pull it in automatically, or manually copy
`LibreHardwareMonitorLib.dll` from `../LibreHardwareMonitor/` into the
project folder if you'd rather use your already-downloaded copy than
fetch from NuGet.

## Run

Launch `LzHwCtrl.exe` -- it'll prompt for admin via UAC (required for the
raw port I/O), then sit in the system tray. First launch loads/starts the
inpoutx64 kernel driver; if `EcPort.EnsureDriverLoaded()` throws, confirm
you accepted the UAC prompt and that `inpoutx64.dll` actually sits next
to the exe.

## Caveats / things to verify on real hardware

- I don't have this laptop to test against, so the EC read/write timing
  (`WaitReady`'s busy-loop) and the LibreHardwareMonitor sensor name
  matching (`"Package"` / `"Core"`) should be checked against what
  actually shows up in LibreHardwareMonitor's own GUI for your specific
  i7-6700HQ + GTX 980M combo -- sensor label text can vary by
  LibreHardwareMonitorLib version.
- Closing to tray vs. fully exiting matters: only the tray "Exit" item
  calls `ControlLoop.Stop()` (which resets fans to EC auto control), same
  intent as your Python script's `reset_to_ec_control()` on shutdown.
  Killing the process via Task Manager will skip that, same risk as on
  Linux.
