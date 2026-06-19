#!/usr/bin/env bash
# mintinstall.sh — Clevo fan/keyboard control for X6780 (Linux Mint / Ubuntu-based)
# Usage: curl -fsSL https://raw.githubusercontent.com/zvf1/X6780fc/main/mintinstall.sh | bash
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
DKMS_NAME="tuxedo-drivers"
DKMS_VERSION="1.0"   # bump if you re-pull a newer drivers tree

# ---- Detect the real user ----
REAL_USER="${SUDO_USER:-$USER}"
[[ -z "$REAL_USER" ]] && die "Could not determine the real username."
REAL_HOME=$(getent passwd "$REAL_USER" | cut -d: -f6)
[[ -z "$REAL_HOME" ]] && die "Could not determine home directory for $REAL_USER."

# ---- Sanity checks ----
[[ "$(id -u)" -eq 0 ]] && die \
  "Do not run this script as root. Run it as your normal user; it will call sudo internally."

command -v sudo >/dev/null 2>&1 || die "sudo is required but not installed."
command -v apt-get >/dev/null 2>&1 || die "This installer is for Debian/Ubuntu-based systems (apt-get required)."

echo ""
echo "  Clevo X6780 fan/keyboard control — installer (Linux Mint / Ubuntu-based)"
echo "  Fan control script : $INSTALL_DIR"
echo "  Tuxedo drivers src : $DRIVERS_SRC"
echo "  User               : $REAL_USER"
echo ""

# ---- 1. apt dependencies ----
info "Updating apt package lists..."
sudo apt-get update -qq

info "Installing apt dependencies..."
DEPS=(git build-essential dkms "linux-headers-$(uname -r)" python3 python3-pip python3-tk python3-pil)
sudo apt-get install -y "${DEPS[@]}"

# clevo_fancontrol.py is invoked by `sudo` directly via its own
# #!/usr/bin/env python3 shebang (see EC_HELPER_SCRIPT in the script) — there
# is no wrapper interpreter in that call. That means pystray/Pillow MUST be
# importable by the *system* python3, not a venv, or every privileged
# --ec-helper call will fail with ImportError before it even parses argv.
# python3-pystray isn't reliably packaged on Mint/Ubuntu, so fall back to pip
# straight into the system environment.
info "Checking for pystray (system Python)..."
if ! python3 -c "import pystray" &>/dev/null; then
    info "pystray not found system-wide — installing via pip..."
    sudo pip install --break-system-packages pystray
else
    ok "pystray already importable system-wide."
fi
if ! python3 -c "from PIL import Image" &>/dev/null; then
    info "Pillow not found system-wide — installing via pip..."
    sudo pip install --break-system-packages Pillow
else
    ok "Pillow already importable system-wide."
fi

# ---- 2. Clone tuxedo-drivers and register with DKMS ----
info "Cloning tuxedo-drivers from $DRIVERS_REPO ..."
if [[ -d "$DRIVERS_SRC/.git" ]]; then
    warn "$DRIVERS_SRC already exists — pulling latest..."
    sudo git -C "$DRIVERS_SRC" pull --ff-only
else
    sudo git clone --depth=1 "$DRIVERS_REPO" "$DRIVERS_SRC"
fi

if [[ ! -f "$DRIVERS_SRC/dkms.conf" ]]; then
    die "No dkms.conf found in $DRIVERS_SRC — this script expects tuxedo-drivers to support DKMS. Check upstream repo structure."
fi

# Reuse upstream's own PACKAGE_NAME from dkms.conf if present.
if grep -q '^PACKAGE_NAME=' "$DRIVERS_SRC/dkms.conf"; then
    DKMS_NAME=$(grep '^PACKAGE_NAME=' "$DRIVERS_SRC/dkms.conf" | head -1 | cut -d'=' -f2 | tr -d '"')
fi

# PACKAGE_VERSION in upstream dkms.conf is often a placeholder like
# "#MODULE_VERSION#", which distro packagers substitute at build time.
# A raw `git clone` won't have that substitution done, so do it ourselves —
# derive a real version string from the git checkout and patch dkms.conf
# in-place before registering with DKMS.
RAW_VERSION=""
if grep -q '^PACKAGE_VERSION=' "$DRIVERS_SRC/dkms.conf"; then
    RAW_VERSION=$(grep '^PACKAGE_VERSION=' "$DRIVERS_SRC/dkms.conf" | head -1 | cut -d'=' -f2 | tr -d '"')
fi

if [[ -z "$RAW_VERSION" || "$RAW_VERSION" == *"#"* ]]; then
    if GIT_VERSION=$(sudo git -C "$DRIVERS_SRC" describe --tags --always 2>/dev/null); then
        DKMS_VERSION="${GIT_VERSION#v}"
    else
        DKMS_VERSION="0.0.$(date +%Y%m%d)"
    fi
    warn "dkms.conf had a placeholder PACKAGE_VERSION — using derived version $DKMS_VERSION instead."
    sudo sed -i "s/^PACKAGE_VERSION=.*/PACKAGE_VERSION=\"$DKMS_VERSION\"/" "$DRIVERS_SRC/dkms.conf"
else
    DKMS_VERSION="$RAW_VERSION"
fi
info "Using DKMS package: ${DKMS_NAME}/${DKMS_VERSION}"

DKMS_DEST="/usr/src/${DKMS_NAME}-${DKMS_VERSION}"
if [[ "$DRIVERS_SRC" != "$DKMS_DEST" ]]; then
    info "Linking source tree to DKMS-expected path $DKMS_DEST ..."
    sudo rm -rf "$DKMS_DEST"
    sudo cp -a "$DRIVERS_SRC" "$DKMS_DEST"
fi

info "Registering and building module with DKMS (this takes a moment)..."
if sudo dkms status | grep -q "^${DKMS_NAME}/${DKMS_VERSION}"; then
    warn "DKMS module already registered — removing old registration first."
    sudo dkms remove "${DKMS_NAME}/${DKMS_VERSION}" --all || true
fi
sudo dkms add "$DKMS_DEST"
sudo dkms build "${DKMS_NAME}/${DKMS_VERSION}"
sudo dkms install "${DKMS_NAME}/${DKMS_VERSION}"
ok "tuxedo-drivers built and installed via DKMS — future kernel upgrades will trigger automatic rebuilds."

# ---- 3. Modprobe tuxedo modules now ----
# clevo_wmi is required to expose white:kbd_backlight on the X6780.
# tuxedo_keyboard pulls in clevo_acpi and tuxedo_io automatically.
for mod in tuxedo_keyboard clevo_wmi; do
    if ! lsmod | grep -q "^$mod"; then
        info "Loading $mod module..."
        sudo modprobe "$mod" || warn "modprobe $mod failed — may need a reboot."
    fi
done

if [[ -d /sys/class/leds/white:kbd_backlight ]]; then
    ok "Keyboard backlight device is present (/sys/class/leds/white:kbd_backlight)"
else
    warn "Keyboard backlight device not yet visible — a reboot may be needed."
fi

# ---- 4. Disable TCC daemon ----
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

# ---- 5. Deploy fan control script ----
info "Creating install directory $INSTALL_DIR ..."
sudo mkdir -p "$INSTALL_DIR"

info "Downloading clevo_fancontrol.py ..."
sudo curl -fsSL "$REPO_RAW/clevo_fancontrol.py" -o "$SCRIPT"
sudo chmod 755 "$SCRIPT"

info "Downloading icon ..."
sudo curl -fsSL "$REPO_RAW/lz4fancontrol.ico" -o "$INSTALL_DIR/lz4fancontrol.ico"

ok "Fan control script installed to $INSTALL_DIR"

# ---- 6. Sudoers rule ----
# Scoped exactly as the script's own docstring expects: the file invoked
# directly (via its #!/usr/bin/env python3 shebang), not via an interpreter
# prefix — this must match how ec_call() in the script itself shells out.
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

# ---- 7. XDG autostart entry ----
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

# ---- 8. Verify install ----
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
echo "  tuxedo-drivers will rebuild automatically after kernel upgrades (via DKMS)."
echo ""
