# X6780

Fan speed, CPU frequency, and keyboard backlight control for the Clevo P65/P67RGRERA (i7-6700HQ, GTX 980M).

---

## Install

**Linux Mint / Ubuntu-based**
```bash
curl -fsSL https://raw.githubusercontent.com/zvf1/X6780/main/mint/mintinstall.sh | bash
```

**Arch / EndeavourOS**
```bash
curl -fsSL https://raw.githubusercontent.com/zvf1/X6780/main/arch/eosinstall.sh | bash
```

**Windows 10 x64**
```powershell
irm https://raw.githubusercontent.com/zvf1/X6780/main/win/install.ps1 | iex
```

## Uninstall

**Linux Mint / Ubuntu-based**
```bash
curl -fsSL https://raw.githubusercontent.com/zvf1/X6780/main/mint/uninstall.sh | bash
```

**Arch / EndeavourOS**
```bash
curl -fsSL https://raw.githubusercontent.com/zvf1/X6780/main/arch/uninstall.sh | bash
```

**Windows 10 x64**
```powershell
irm https://raw.githubusercontent.com/zvf1/X6780/main/win/uninstall.ps1 | iex
```
---

## Features

- **Fan control** — independent CPU and GPU fan speed buttons (AUTO / 38% / 50% / 56% / 69% / 82% / 100%), with a temperature-driven auto curve tuned for this laptop's EC duty values
- **CPU frequency cap** — limit max CPU frequency on demand (AUTO / 1.8 / 2.0 / 2.2 / 2.4 / 2.6 / 2.8 GHz) via cpupower
- **Keyboard backlight** — brightness levels Off / 1–5
- **Persistent settings** — all selections are saved to `~/.config/lzhwctrl/state.json` and restored automatically on next login
- **System tray** — runs as a tray icon; click to open the control window
- **No root GUI** — privilege is scoped to a single sudoers rule for the EC helper only; the GUI itself runs as your normal user
- **Kernel module auto-rebuild** — tuxedo-drivers rebuilds automatically after kernel upgrades (pacman hook on Arch, postinst hook on Mint/Ubuntu)

---

## How it works

`lzhwctrl.py` is a single Python file that plays two roles. In normal mode it runs as your user, managing the GUI, tray icon, temperature polling, and fan curve logic. When it needs to write to the embedded controller (fan speeds) or the keyboard backlight, it calls itself via `sudo -n` in a tightly scoped helper mode (`--ec-helper`), which performs the raw EC I/O and exits immediately. No persistent root process.

Temperatures are read from sysfs (`coretemp` for the CPU package, `nouveau` for the GPU). Fan duties are written directly to the EC via `/dev/port` at the same `0x62`/`0x66` ports used by the original Windows tooling.
