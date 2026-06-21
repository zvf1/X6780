#!/usr/bin/env bash
# install.sh — Clevo fan/keyboard control for X6780 (Arch/EndeavourOS)
# Usage: curl -fsSL https://raw.githubusercontent.com/zvf1/X6780/main/mint/eosinstall.sh | bash
set -euo pipefail

# ---- Colour helpers ----
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
info()  { echo -e "${GREEN}[*]${NC} $*"; }
warn()  { echo -e "${YELLOW}[!]${NC} $*"; }
die()   { echo -e "${RED}[✗]${NC} $*" >&2; exit 1; }
ok()    { echo -e "${GREEN}[✓]${NC} $*"; }

# ---- Constants ----
REPO_RAW="https://raw.githubusercontent.com/zvf1/X6780/main/mint"
DRIVERS_REPO="https://github.com/zvf1/tuxedo-drivers"
INSTALL_DIR="/opt/clevo-fancontrol"
SCRIPT="$INSTALL_DIR/lzhwctrl.py"
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

info "Preparing kernel headers (generates autoconf.h — needed on Arch)..."
# Arch linux-headers does not ship a pre-generated autoconf.h, and `make prepare`
# fails because the headers package omits arch-specific Kconfig files referenced
# by crypto/Kconfig (arm, arm64, sparc, etc.). We work around this by:
#   1. Stubbing all missing arch Kconfig files so syncconfig can parse the tree
#   2. Seeding autoconf.h and auto.conf directly from the shipped .config
KBUILD="/lib/modules/$(uname -r)/build"
if [[ ! -f "$KBUILD/include/generated/autoconf.h" ]] || [[ ! -s "$KBUILD/include/generated/autoconf.h" ]]; then
    info "Stubbing missing arch Kconfig files..."
    for arch in arm arm64 loongarch mips powerpc riscv s390 sparc x86; do
        sudo mkdir -p "$KBUILD/arch/$arch/crypto"
        sudo touch "$KBUILD/arch/$arch/crypto/Kconfig"
    done

    info "Seeding autoconf.h and auto.conf from .config..."
    sudo mkdir -p "$KBUILD/include/generated" "$KBUILD/include/config"

    # Build autoconf.h from .config — convert CONFIG_FOO=y/m → #define CONFIG_FOO 1
    # and CONFIG_FOO=<val> → #define CONFIG_FOO <val>; skip CONFIG_CC_VERSION_TEXT
    # (its embedded quotes break make's shell comparisons).
    sudo bash -c '
        KBUILD="'"$KBUILD"'"
        grep "^CONFIG_" "$KBUILD/.config" | grep -v "^CONFIG_CC_VERSION_TEXT" | \
        while IFS="=" read key val; do
            if [ "$val" = "y" ] || [ "$val" = "m" ]; then
                echo "#define $key 1"
            elif [ -n "$val" ]; then
                echo "#define $key $val"
            fi
        done > /tmp/autoconf.h
        cp /tmp/autoconf.h "$KBUILD/include/generated/autoconf.h"
    '

    # auto.conf: full .config content minus CC_VERSION_TEXT (same reason)
    sudo bash -c '
        KBUILD="'"$KBUILD"'"
        grep "^CONFIG_" "$KBUILD/.config" | grep -v "^CONFIG_CC_VERSION_TEXT" \
            > "$KBUILD/include/config/auto.conf"
    '
    sudo touch "$KBUILD/include/config/auto.conf.cmd"
    ok "autoconf.h seeded successfully."
else
    ok "autoconf.h already exists, skipping prepare."
fi

info "Building tuxedo-drivers (this takes a moment)..."
# sudo resets PWD, which causes M=$(PWD) to expand to empty, triggering
# a full kernel syncconfig that fails on x86 headers. sh -c fixes this.
sudo sh -c "cd '$DRIVERS_SRC' && make -j$(nproc)"

info "Installing kernel modules..."
# Use the kernel build system's modules_install target directly, bypassing the
# tuxedo-drivers `make install` which also runs `cp -r usr /` and crashes when
# that directory doesn't exist in the repo.
sudo make -C "/lib/modules/$(uname -r)/build" M="$DRIVERS_SRC" modules_install
sudo depmod -a
ok "tuxedo-drivers built and installed."

# ---- 4. Modprobe tuxedo modules now ----
# clevo_wmi is required to expose white:kbd_backlight on the X6780.
# tuxedo_keyboard pulls in clevo_acpi and tuxedo_io automatically.
for mod in tuxedo_keyboard clevo_wmi; do
    if ! lsmod | grep -q "^$mod"; then
        info "Loading $mod module..."
        sudo modprobe "$mod" || warn "modprobe $mod failed — may need a reboot."
    fi
done

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
KBUILD="/lib/modules/$(uname -r)/build"

if [[ ! -f "$KBUILD/include/generated/autoconf.h" ]] || [[ ! -s "$KBUILD/include/generated/autoconf.h" ]]; then
    echo "[tuxedo-drivers] Seeding autoconf.h from .config..."
    for arch in arm arm64 loongarch mips powerpc riscv s390 sparc x86; do
        mkdir -p "$KBUILD/arch/$arch/crypto"
        touch "$KBUILD/arch/$arch/crypto/Kconfig"
    done
    mkdir -p "$KBUILD/include/generated" "$KBUILD/include/config"
    grep "^CONFIG_" "$KBUILD/.config" | grep -v "^CONFIG_CC_VERSION_TEXT" | \
    while IFS="=" read key val; do
        if [ "$val" = "y" ] || [ "$val" = "m" ]; then
            echo "#define $key 1"
        elif [ -n "$val" ]; then
            echo "#define $key $val"
        fi
    done > "$KBUILD/include/generated/autoconf.h"
    grep "^CONFIG_" "$KBUILD/.config" | grep -v "^CONFIG_CC_VERSION_TEXT" \
        > "$KBUILD/include/config/auto.conf"
    touch "$KBUILD/include/config/auto.conf.cmd"
fi

cd /usr/src/tuxedo-drivers && make clean
make -j$(nproc)
make -C "/lib/modules/$(uname -r)/build" M=/usr/src/tuxedo-drivers modules_install
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

info "Downloading lzhwctrl.py ..."
sudo curl -fsSL "$REPO_RAW/lzhwctrl.py" -o "$SCRIPT"
sudo chmod 755 "$SCRIPT"

info "Downloading icon ..."
sudo curl -fsSL "$REPO_RAW/lzhwctrl.ico" -o "$INSTALL_DIR/lzhwctrl.ico"

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
Icon=$INSTALL_DIR/lzhwctrl.ico
Terminal=false
X-GNOME-Autostart-enabled=true
EOF
ok "Autostart entry written to $AUTOSTART_DIR/clevo-fancontrol.desktop"

# ---- 10. Verify install ----
HELPER_OUT=$(sudo -n "$SCRIPT" --ec-helper 2>&1 || true)
if [[ -f "$SCRIPT" ]] && echo "$HELPER_OUT" | grep -q "ec-helper"; then
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
