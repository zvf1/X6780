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
INSTALL_DIR="/opt/clevo-fancontrol"
SCRIPT="$INSTALL_DIR/clevo_fancontrol.py"
SUDOERS_FILE="/etc/sudoers.d/clevo-fancontrol"

# ---- Detect the real user (works whether script is piped or run normally) ----
# $USER is preserved even in a `curl | bash` pipe; $SUDO_USER would only be
# set if the *entire* script was invoked with sudo, which we don't want.
REAL_USER="${SUDO_USER:-$USER}"
[[ -z "$REAL_USER" ]] && die "Could not determine the real username."
REAL_HOME=$(getent passwd "$REAL_USER" | cut -d: -f6)
[[ -z "$REAL_HOME" ]] && die "Could not determine home directory for $REAL_USER."

# ---- Sanity checks ----
[[ "$(id -u)" -eq 0 ]] && die \
  "Do not run this script as root. Run it as your normal user; it will call sudo internally."

command -v sudo >/dev/null 2>&1 || die "sudo is required but not installed."
command -v pacman >/dev/null 2>&1 || die "This installer is for Arch-based systems (pacman required)."

echo ""
echo "  Clevo X6780 fan/keyboard control — installer"
echo "  Installing to : $INSTALL_DIR"
echo "  User          : $REAL_USER"
echo ""

# ---- 1. Dependencies ----
info "Installing Python dependencies via pacman..."
DEPS=(python python-pillow python-pystray tk)
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

# ---- 2. Deploy files ----
info "Creating install directory $INSTALL_DIR ..."
sudo mkdir -p "$INSTALL_DIR"

info "Downloading clevo_fancontrol.py ..."
sudo curl -fsSL "$REPO_RAW/clevo_fancontrol.py" -o "$SCRIPT"
sudo chmod 755 "$SCRIPT"

info "Downloading icon ..."
sudo curl -fsSL "$REPO_RAW/lz4fancontrol.ico" -o "$INSTALL_DIR/lz4fancontrol.ico"

ok "Files installed to $INSTALL_DIR"

# ---- 3. Sudoers rule ----
# The rule allows ONLY this specific script file with the --ec-helper flag,
# nothing else. We validate with visudo -c before writing.
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

# ---- 5. XDG autostart entry ----
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

# ---- 6. Smoke test: verify sudo path works ----
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
echo "  To test fan control before rebooting:"
echo "    sudo -n $SCRIPT --ec-helper set 1 128   # CPU fan ~50%"
echo "    sudo -n $SCRIPT --ec-helper reset        # return to EC auto"
echo ""
echo "  To launch now (without rebooting):"
echo "    $SCRIPT &"
echo ""
echo "  The tray icon will autostart on next login."
echo ""
