#!/bin/bash
#
# mintoptional.sh  (Linux Mint / Debian-based)
#
# Combined installer + boot script (formerly install-mintlzscript.sh +
# mintLzScript.sh).
#
# Run manually (e.g. via curl | bash) to install:
#   1. Builds and installs intel-undervolt from source (no apt package
#      exists for Debian/Ubuntu/Mint, unlike Arch's pacman repo).
#   2. Sets CPU / CPU Cache undervolt offsets to -155 and enable=yes in
#      /etc/intel-undervolt.conf.
#   3. Installs this script to /usr/local/sbin/mintoptional.sh.
#   4. Registers + enables mintoptional.service to run it on every boot.
#   5. Runs the boot tasks once immediately.
#
# On every subsequent boot (run automatically by systemd), it will:
#   1. Apply intel-undervolt settings.
#   2. Check Microsoft's repo for the latest libmsquic and update it if
#      newer (used by Technitium DNS for DoH3).
#
# Logs only on errors or when something actually changes.
# View logs with: journalctl -u mintoptional.service
#

set -uo pipefail

RAW_BASE="https://raw.githubusercontent.com/zvf1/X6780/main/mint"
UNDERVOLT_REPO="https://github.com/kitsunyan/intel-undervolt.git"
SCRIPT_PATH="/usr/local/sbin/mintoptional.sh"
SERVICE_PATH="/etc/systemd/system/mintoptional.service"

# =========================================================================
# run_boot_tasks: apply undervolt + check/update libmsquic
# =========================================================================
run_boot_tasks() {
    # ---- 1. Apply intel-undervolt ----
    if command -v intel-undervolt &>/dev/null; then
        intel-undervolt apply
    else
        echo "Error: intel-undervolt not found." >&2
    fi

    # ---- 2. Check/update libmsquic for Technitium DoQ/DoH3 ----
    local MS_REPO_URL="https://packages.microsoft.com/debian/12/prod/pool/main/libm/libmsquic/"
    local VERSION_FILE="/usr/lib/libmsquic.installed_version"

    local DEB_ARCH
    case "$(uname -m)" in
        x86_64)  DEB_ARCH="amd64" ;;
        aarch64) DEB_ARCH="arm64" ;;
        armv7l)  DEB_ARCH="armhf" ;;
        *)
            echo "Error: unsupported architecture $(uname -m)" >&2
            return 1
            ;;
    esac

    local MISSING=()
    for cmd in curl ar tar ldconfig; do
        command -v "$cmd" &>/dev/null || MISSING+=("$cmd")
    done
    if [[ ${#MISSING[@]} -gt 0 ]]; then
        echo "Error: missing required tools: ${MISSING[*]}" >&2
        return 1
    fi

    local WORKDIR
    WORKDIR=$(mktemp -d)
    trap 'rm -rf "$WORKDIR"' RETURN

    # Wait for DNS/network to actually be usable (network-online.target doesn't
    # always guarantee this in time, especially on Wi-Fi/DHCP).
    local NET_READY=0
    for i in {1..10}; do
        if curl -fsSL --max-time 5 -o /dev/null "$MS_REPO_URL"; then
            NET_READY=1
            break
        fi
        sleep 3
    done

    if [[ "$NET_READY" -eq 0 ]]; then
        echo "Error: network unreachable after retries, skipping libmsquic check." >&2
        return 0
    fi

    local LATEST_DEB
    LATEST_DEB=$(curl -fsSL "$MS_REPO_URL" \
        | grep -oP "libmsquic_[0-9]+\.[0-9]+\.[0-9]+_${DEB_ARCH}\.deb" \
        | sort -V -u \
        | tail -n1)

    if [[ -z "$LATEST_DEB" ]]; then
        echo "Error: could not find a libmsquic package for ${DEB_ARCH} in the repo." >&2
        return 0
    fi

    local LATEST_VERSION
    LATEST_VERSION=$(grep -oP '[0-9]+\.[0-9]+\.[0-9]+' <<< "$LATEST_DEB")

    local CURRENT_VERSION=""
    if [[ -f "$VERSION_FILE" ]]; then
        CURRENT_VERSION=$(cat "$VERSION_FILE")
    fi

    echo "libmsquic check: installed=${CURRENT_VERSION:-none} latest=$LATEST_VERSION"

    if [[ "$CURRENT_VERSION" == "$LATEST_VERSION" ]]; then
        return 0
    fi

    curl -fsSL "${MS_REPO_URL}${LATEST_DEB}" -o "$WORKDIR/$LATEST_DEB"

    (
        cd "$WORKDIR"
        ar x "$LATEST_DEB"

        if ! ls data.tar.* &>/dev/null; then
            echo "Error: data.tar.* not found inside the .deb." >&2
            exit 1
        fi

        if ! tar -xf data.tar.* 2>/dev/null; then
            echo "Error: failed to extract data.tar.* — 'zstd' may be required." >&2
            exit 1
        fi

        LIBDIR="usr/lib/x86_64-linux-gnu"
        [[ "$DEB_ARCH" == "arm64" ]] && LIBDIR="usr/lib/aarch64-linux-gnu"
        [[ "$DEB_ARCH" == "armhf" ]] && LIBDIR="usr/lib/arm-linux-gnueabihf"

        if [[ ! -d "$LIBDIR" ]]; then
            echo "Error: expected library directory '$LIBDIR' not found in package." >&2
            exit 1
        fi

        SO_FILE=$(ls "$LIBDIR"/libmsquic.so.* | xargs -n1 basename | sort -V | tail -n1)
        install -Dm755 "$LIBDIR/$SO_FILE" "/usr/lib/$SO_FILE"

        SO_MAJOR=$(grep -oP '(?<=\.so\.)[0-9]+' <<< "$SO_FILE")
        ln -sf "/usr/lib/$SO_FILE" "/usr/lib/libmsquic.so.$SO_MAJOR"
        ln -sf "/usr/lib/libmsquic.so.$SO_MAJOR" "/usr/lib/libmsquic.so"

        if ls "$LIBDIR"/libmsquic.lttng.so.* &>/dev/null; then
            LTTNG_FILE=$(ls "$LIBDIR"/libmsquic.lttng.so.* | xargs -n1 basename | sort -V | tail -n1)
            install -Dm755 "$LIBDIR/$LTTNG_FILE" "/usr/lib/$LTTNG_FILE"
            LTTNG_MAJOR=$(grep -oP '(?<=\.so\.)[0-9]+' <<< "$LTTNG_FILE")
            ln -sf "/usr/lib/$LTTNG_FILE" "/usr/lib/libmsquic.lttng.so.$LTTNG_MAJOR"
        fi
    ) || return 1

    ldconfig

    echo "$LATEST_VERSION" > "$VERSION_FILE"
    echo "libmsquic updated: ${CURRENT_VERSION:-none} -> $LATEST_VERSION"

    local TECH_SERVICE
    TECH_SERVICE=$(systemctl list-unit-files 2>/dev/null | grep -i technitium | awk '{print $1}' | head -n1 || true)
    if [[ -n "$TECH_SERVICE" ]]; then
        systemctl restart "$TECH_SERVICE"
        echo "Restarted $TECH_SERVICE for new libmsquic"
    fi
}

# =========================================================================
# If running under systemd (i.e. this is the boot-time invocation), just
# run the boot tasks and exit.
# =========================================================================
if [[ -n "${INVOCATION_ID:-}" ]]; then
    run_boot_tasks
    exit 0
fi

# =========================================================================
# Otherwise this is a manual/interactive run: do the one-time setup.
# =========================================================================

set -e

# ---- 1. Build and install intel-undervolt from source ----
if command -v intel-undervolt &>/dev/null; then
    echo "==> intel-undervolt already installed, skipping build."
else
    echo "==> Installing build dependencies for intel-undervolt..."
    sudo apt-get update
    sudo apt-get install -y build-essential git msr-tools

    echo "==> Building intel-undervolt from source..."
    BUILD_DIR=$(mktemp -d)
    trap 'rm -rf "$BUILD_DIR"' EXIT

    git clone --depth=1 "$UNDERVOLT_REPO" "$BUILD_DIR/intel-undervolt"
    (
        cd "$BUILD_DIR/intel-undervolt"
        ./configure --enable-systemd
        make
        sudo make install
    )

    rm -rf "$BUILD_DIR"
    trap - EXIT

    echo "==> Loading msr kernel module (required by intel-undervolt)..."
    sudo modprobe msr || true
    if ! grep -qx msr /etc/modules 2>/dev/null; then
        echo "msr" | sudo tee -a /etc/modules >/dev/null
    fi
fi

echo "==> Setting CPU and CPU Cache undervolt offsets to -155 in"
echo "    /etc/intel-undervolt.conf..."
sudo sed -i \
    -e "s/^undervolt 0 'CPU' .*/undervolt 0 'CPU' -155/" \
    -e "s/^undervolt 2 'CPU Cache' .*/undervolt 2 'CPU Cache' -155/" \
    -e "s/^enable no/enable yes/" \
    /etc/intel-undervolt.conf

echo "==> NOTE: review /etc/intel-undervolt.conf for any other domains"
echo "    (GPU, System Agent, etc.) you may also want to tune for your CPU."

# ---- 2. Install mintoptional.sh ----
echo "==> Installing mintoptional.sh to ${SCRIPT_PATH}..."
if [[ -f "$0" && "$0" != "bash" && "$0" != "-bash" && -r "$0" ]]; then
    sudo install -Dm755 "$0" "$SCRIPT_PATH" 2>/dev/null || sudo curl -fsSL "$RAW_BASE/mintoptional.sh" -o "$SCRIPT_PATH"
else
    sudo curl -fsSL "$RAW_BASE/mintoptional.sh" -o "$SCRIPT_PATH"
fi
sudo chmod 755 "$SCRIPT_PATH"

# ---- 3. Install mintoptional.service ----
echo "==> Installing mintoptional.service..."
sudo install -Dm644 /dev/stdin "$SERVICE_PATH" <<EOF
[Unit]
Description=Apply intel-undervolt settings and check for libmsquic updates
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=${SCRIPT_PATH}

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable mintoptional.service

echo "==> Done. mintoptional.service is enabled and will run on next boot."
echo "    Run it now with: sudo systemctl start mintoptional.service"
echo "    View logs with:  journalctl -u mintoptional.service"
