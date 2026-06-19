#!/usr/bin/env bash
# install.sh — Clevo fan/keyboard control for X6780 (Arch/EndeavourOS)
# Usage: curl -fsSL https://raw.githubusercontent.com/zvf1/X6780fc/main/install.sh | bash
set -euo pipefail

# ---- Colour helpers ----
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
info()  { echo -e "${GREEN}[*]${NC} $*"; }
warn()  { echo -e "${YELLOW}[!]${NC} $*"; }
die()   { echo -e "${RED}[✗]${NC} $*" >&2; exit 1; }
ok()    { echo -e "${GREEN}[✓]${NC} $*"; }

# ---- Constants ----
REPO_RAW="https://raw.githubusercontent.com/zvf1/X6780fc/main"
DRIVERS_REPO="https://github.com/zvf1/tuxedo-drivers"
INSTALL_DIR="/opt/clevo-fancontrol"
SCRIPT="$INSTALL_DIR/clevo_fancontrol.py"
SUDOERS_FILE="/etc/sudoers.d/clevo-fancontrol"
DRIVERS_SRC="/usr/src/tuxedo-drivers"
REBUILD_BIN="/usr/local/bin/tuxedo-drivers-rebuild"
HOOK_DIR="/etc/pacman.d/hooks"
HOOK_FILE="$HOOK_DIR/tuxedo-drivers.hook"

# ---- Detect the real user ----
# $USER is preserved even in a `curl | bash` pipe; $SUDO_USER would only be
# set if the entire script was invoked with sudo, which we don't want.
REAL_USER="${SUDO_USER:-$USER}"
[[ -z "$REAL_USER" ]] && die "Could not determine the real username."
REAL_HOME=$(getent passwd "$REAL_USER" | cut -d: -f6)
[[ -z "$REAL_HOME" ]] && die "Could not determine home directory for $REAL_USER."

# ---- Sanity checks ----
[[ "$(id -u)" -eq 0 ]] && die \
  "Do not run this script as root. Run it as your normal user; it will call sudo internally."

command -v sudo  >/dev/null 2>&1 || die "sudo is required but not installed."
command -v pacman >/dev/null 2>&1 || die "This installer is for Arch-based systems (pacman required)."

echo ""
echo "  Clevo X6780 fan/keyboard control — installer"
echo "  Fan control script : $INSTALL_DIR"
echo "  Tuxedo drivers src : $DRIVERS_SRC"
echo "  User               : $REAL_USER"
echo ""

# ---- 1. Detect kernel headers package ----
KERNEL=$(uname -r)
if   [[ "$KERNEL" == *lts*      ]]; then HEADERS_PKG="linux-lts-headers"
elif [[ "$KERNEL" == *zen*      ]]; then HEADERS_PKG="linux-zen-headers"
elif [[ "$KERNEL" == *hardened* ]]; then HEADERS_PKG="linux-hardened-headers"
elif [[ "$KERNEL" == *rt-lts*   ]]; then HEADERS_PKG="linux-rt-lts-headers"
elif [[ "$KERNEL" == *rt*       ]]; then HEADERS_PKG="linux-rt-headers"
else                                      HEADERS_PKG="linux-headers"
fi
info "Detected kernel $KERNEL → headers package: $HEADERS_PKG"

# ---- 2. Pacman dependencies ----
info "Installing pacman dependencies..."
DEPS=(git base-devel "$HEADERS_PKG" python python-pillow python-pystray tk)
MISSING=()
for pkg in "${DEPS[@]}"; do
    pacman -Qi "$pkg" &>/dev/null || MISSING+=("$pkg")
done

if [[ ${#MISSING[@]} -gt 0 ]]; then
    info "Installing: ${MISSING[*]}"
    sudo pacman -S --needed --noconfirm "${MISSING[@]}"
else
    ok "All pacman dependencies already installed."
fi

# ---- 3. Clone and build tuxedo-drivers ----
info "Cloning tuxedo-drivers from $DRIVERS_REPO ..."
if [[ -d "$DRIVERS_SRC/.git" ]]; then
    warn "$DRIVERS_SRC already exists — pulling latest..."
    sudo git -C "$DRIVERS_SRC" pull --ff-only
else
    sudo git clone --depth=1 "$DRIVERS_REPO" "$DRIVERS_SRC"
fi

info "Building tuxedo-drivers (this takes a moment)..."
sudo make -C "$DRIVERS_SRC" -j"$(nproc)"

info "Installing kernel modules..."
sudo make -C "$DRIVERS_SRC" install
sudo depmod -a
ok "tuxedo-drivers built and installed."

# ---- 4. Modprobe tuxedo_keyboard now ----
if ! lsmod | grep -q tuxedo_keyboard; then
    info "Loading tuxedo_keyboard module..."
    sudo modprobe tuxedo_keyboard || warn "modprobe tuxedo_keyboard failed — may need a reboot."
fi

# Confirm the LED device exists
if [[ -d /sys/class/leds/white:kbd_backlight ]]; then
    ok "Keyboard backlight device is present (/sys/class/leds/white:kbd_backlight)"
else
    warn "Keyboard backlight device not yet visible — a reboot may be needed."
fi

# ---- 5. Pacman hook: auto-rebuild on kernel upgrade ----
# This replaces what DKMS would do, using only official-repo tooling.
info "Installing pacman hook for automatic kernel-upgrade rebuilds..."

sudo install -dm755 "$HOOK_DIR"

sudo tee "$REBUILD_BIN" > /dev/null << 'EOF'
#!/usr/bin/bash
set -e
echo "[tuxedo-drivers] Rebuilding for kernel $(uname -r)..."
make -C /usr/src/tuxedo-drivers clean
make -C /usr/src/tuxedo-drivers -j$(nproc)
make -C /usr/src/tuxedo-drivers install
depmod -a
echo "[tuxedo-drivers] Done."
EOF
sudo chmod 755 "$REBUILD_BIN"

sudo tee "$HOOK_FILE" > /dev/null << 'EOF'
[Trigger]
Operation = Install
Operation = Upgrade
Type = Package
Target = linux
Target = linux-lts
Target = linux-zen
Target = linux-hardened
Target = linux-rt
Target = linux-rt-lts

[Action]
Description = Rebuilding tuxedo-drivers for updated kernel...
When = PostTransaction
Exec = /usr/local/bin/tuxedo-drivers-rebuild
EOF

ok "Pacman hook installed — tuxedo-drivers will rebuild automatically after kernel upgrades."

# ---- 6. Disable TCC daemon ----
info "Stopping and disabling tccd (TCC daemon) ..."
if systemctl is-active --quiet tccd 2>/dev/null; then
    sudo systemctl disable --now tccd
    ok "tccd disabled."
elif systemctl list-unit-files tccd.service &>/dev/null 2>&1; then
    sudo systemctl disable tccd 2>/dev/null || true
    ok "tccd was not running but has been disabled."
else
    warn "tccd service not found — nothing to disable (safe to ignore)."
fi

# ---- 7. Deploy fan control script ----
info "Creating install directory $INSTALL_DIR ..."
sudo mkdir -p "$INSTALL_DIR"

info "Downloading clevo_fancontrol.py ..."
sudo curl -fsSL "$REPO_RAW/clevo_fancontrol.py" -o "$SCRIPT"
sudo chmod 755 "$SCRIPT"

info "Downloading icon ..."
sudo curl -fsSL "$REPO_RAW/lz4fancontrol.ico" -o "$INSTALL_DIR/lz4fancontrol.ico"

ok "Fan control script installed to $INSTALL_DIR"

# ---- 8. Sudoers rule ----
info "Writing sudoers rule (scoped to $SCRIPT --ec-helper only) ..."
SUDOERS_CONTENT="$REAL_USER ALL=(root) NOPASSWD: $SCRIPT --ec-helper *"
SUDOERS_TMP="$(mktemp)"
echo "$SUDOERS_CONTENT" > "$SUDOERS_TMP"

if sudo visudo -c -f "$SUDOERS_TMP" &>/dev/null; then
    sudo install -m 0440 "$SUDOERS_TMP" "$SUDOERS_FILE"
    ok "Sudoers rule written to $SUDOERS_FILE"
else
    rm -f "$SUDOERS_TMP"
    die "visudo syntax check failed — sudoers rule was NOT written."
fi
rm -f "$SUDOERS_TMP"

# ---- 9. XDG autostart entry ----
info "Writing autostart entry ..."
AUTOSTART_DIR="$REAL_HOME/.config/autostart"
mkdir -p "$AUTOSTART_DIR"
cat > "$AUTOSTART_DIR/clevo-fancontrol.desktop" << EOF
[Desktop Entry]
Type=Application
Name=Clevo Fan Control
Comment=Fan and keyboard backlight control for Clevo X6780
Exec=$SCRIPT
Icon=$INSTALL_DIR/lz4fancontrol.ico
Terminal=false
X-GNOME-Autostart-enabled=true
EOF
ok "Autostart entry written to $AUTOSTART_DIR/clevo-fancontrol.desktop"

# ---- 10. Smoke test: verify sudo path works ----
info "Testing privileged helper path..."
if sudo -n "$SCRIPT" --ec-helper 2>&1 | grep -q "usage:"; then
    ok "Privileged helper is reachable via sudo."
else
    warn "Could not confirm helper path — check the sudoers rule manually:"
    warn "  sudo -n $SCRIPT --ec-helper"
fi

# ---- Done ----
echo ""
echo -e "${GREEN}Installation complete.${NC}"
echo ""
echo "  To test fan control:"
echo "    sudo -n $SCRIPT --ec-helper set 1 255   # CPU fan 100%"
echo "    sudo -n $SCRIPT --ec-helper reset        # return to EC auto"
echo ""
echo "  To launch now:"
echo "    $SCRIPT &"
echo ""
echo "  The tray icon will autostart on next login."
echo "  tuxedo-drivers will rebuild automatically after kernel upgrades."
echo ""
