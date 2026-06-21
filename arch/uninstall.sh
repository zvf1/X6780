#!/usr/bin/env bash
# uninstall.sh — Remove lzhwctrl fan/keyboard control (Arch / EndeavourOS)
# Usage: curl -fsSL https://raw.githubusercontent.com/zvf1/X6780/main/arch/uninstall.sh | bash
set -euo pipefail

# ---- Colour helpers ----
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
info()  { echo -e "${GREEN}[*]${NC} $*"; }
warn()  { echo -e "${YELLOW}[!]${NC} $*"; }
ok()    { echo -e "${GREEN}[✓]${NC} $*"; }

# ---- Detect the real user ----
REAL_USER="${SUDO_USER:-$USER}"
[[ -z "$REAL_USER" ]] && { echo "Could not determine the real username." >&2; exit 1; }
REAL_HOME=$(getent passwd "$REAL_USER" | cut -d: -f6)
[[ -z "$REAL_HOME" ]] && { echo "Could not determine home directory for $REAL_USER." >&2; exit 1; }

# ---- Sanity check ----
[[ "$(id -u)" -eq 0 ]] && { echo -e "${RED}[✗]${NC} Do not run as root. Run as your normal user; it will call sudo internally." >&2; exit 1; }

echo ""
echo "  lzhwctrl — uninstaller (Arch / EndeavourOS)"
echo "  User : $REAL_USER"
echo ""

# ---- 1. Kill any running instance ----
info "Stopping any running lzhwctrl instance..."
pkill -f lzhwctrl.py 2>/dev/null && ok "Process killed." || warn "No running process found (safe to ignore)."

# ---- 2. Unload kernel modules ----
info "Unloading tuxedo kernel modules..."
for mod in tuxedo_keyboard clevo_wmi clevo_acpi tuxedo_io; do
    if lsmod | grep -q "^$mod"; then
        sudo modprobe -r "$mod" && ok "Unloaded $mod." || warn "Could not unload $mod — may still be in use."
    fi
done

# ---- 3. Remove install directory (script + icon) ----
info "Removing /opt/clevo-fancontrol..."
if [[ -d /opt/clevo-fancontrol ]]; then
    sudo rm -rf /opt/clevo-fancontrol
    ok "Removed /opt/clevo-fancontrol."
else
    warn "/opt/clevo-fancontrol not found (safe to ignore)."
fi

# ---- 4. Remove sudoers rule ----
info "Removing sudoers rule..."
if [[ -f /etc/sudoers.d/clevo-fancontrol ]]; then
    sudo rm -f /etc/sudoers.d/clevo-fancontrol
    ok "Removed /etc/sudoers.d/clevo-fancontrol."
else
    warn "Sudoers rule not found (safe to ignore)."
fi

# ---- 5. Remove pacman hook and rebuild script ----
info "Removing pacman hook..."
if [[ -f /etc/pacman.d/hooks/tuxedo-drivers.hook ]]; then
    sudo rm -f /etc/pacman.d/hooks/tuxedo-drivers.hook
    ok "Removed tuxedo-drivers.hook."
else
    warn "Pacman hook not found (safe to ignore)."
fi

info "Removing rebuild script..."
if [[ -f /usr/local/bin/tuxedo-drivers-rebuild ]]; then
    sudo rm -f /usr/local/bin/tuxedo-drivers-rebuild
    ok "Removed /usr/local/bin/tuxedo-drivers-rebuild."
else
    warn "Rebuild script not found (safe to ignore)."
fi

# ---- 6. Remove tuxedo-drivers source and built modules ----
info "Removing tuxedo-drivers source..."
if [[ -d /usr/src/tuxedo-drivers ]]; then
    sudo rm -rf /usr/src/tuxedo-drivers
    ok "Removed /usr/src/tuxedo-drivers."
else
    warn "/usr/src/tuxedo-drivers not found (safe to ignore)."
fi

info "Removing installed tuxedo kernel modules..."
KVER="$(uname -r)"
MODS_DIR="/lib/modules/$KVER/extra"
if [[ -d "$MODS_DIR" ]]; then
    sudo find "$MODS_DIR" -name "tuxedo_*.ko*" -o -name "clevo_*.ko*" | \
        xargs -r sudo rm -f
    sudo depmod -a
    ok "Kernel modules removed and depmod updated."
else
    warn "No extra modules directory found (safe to ignore)."
fi

# ---- 7. Remove XDG autostart entry ----
info "Removing autostart entry..."
AUTOSTART="$REAL_HOME/.config/autostart/clevo-fancontrol.desktop"
if [[ -f "$AUTOSTART" ]]; then
    rm -f "$AUTOSTART"
    ok "Removed $AUTOSTART."
else
    warn "Autostart entry not found (safe to ignore)."
fi

# ---- 8. Remove saved preferences ----
info "Removing saved preferences..."
PREFS_DIR="$REAL_HOME/.config/lzhwctrl"
if [[ -d "$PREFS_DIR" ]]; then
    rm -rf "$PREFS_DIR"
    ok "Removed $PREFS_DIR."
else
    warn "Preferences directory not found (safe to ignore)."
fi

# ---- Done ----
echo ""
echo -e "${GREEN}Uninstall complete.${NC}"
echo ""
echo "  You can now run the installer to get a fresh install:"
echo "    curl -fsSL https://raw.githubusercontent.com/zvf1/X6780/main/arch/eosinstall.sh | bash"
echo ""
