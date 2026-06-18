#!/usr/bin/env python3
"""
Clevo/EC fan + keyboard backlight control — Linux port.

ARCHITECTURE
------------
Reading temperatures needs no privilege (plain sysfs reads under
/sys/class/hwmon). Writing fan duty cycles does need root, because it
pokes the embedded controller directly via /dev/port at the same
0x62/0x66 ports your Windows script used.

Rather than running the whole GUI as root (messy with tray icons /
Wayland / D-Bus session ownership), this single file plays two roles:

  1. Normal mode (no args): runs as your regular user. GUI, tray,
     temperature polling, fan curve logic. Whenever it needs to
     actually change a fan duty cycle or the keyboard backlight, it
     shells out to mode 2 via `sudo -n`.

  2. Helper mode (`--ec-helper <cmd> ...`): meant to be invoked only
     via sudo. Validates its arguments tightly, does the raw EC
     read/write or LED sysfs write, then exits immediately. No
     daemon, no persistent root process.

ONE-TIME SETUP
--------------
1. Make sure TCC's daemon is not fighting you for the EC:
       sudo systemctl disable --now tccd
   (You can leave the `tuxedo-drivers` kernel modules installed —
   tuxedo_keyboard is what gives you the /sys/class/leds keyboard
   backlight device used below, and it's just a passive driver, not
   a daemon. Uninstall `tuxedo-control-center` itself if you want it
   fully gone.)

2. Put this file somewhere stable and make it executable:
       sudo install -Dm755 clevo_fancontrol.py /opt/clevo-fancontrol/fancontrol.py

3. Add a sudoers rule scoped *only* to this exact file and the
   --ec-helper flag (replace `yourusername`):
       echo 'yourusername ALL=(root) NOPASSWD: /opt/clevo-fancontrol/fancontrol.py --ec-helper *' \
         | sudo tee /etc/sudoers.d/clevo-fancontrol
       sudo chmod 0440 /etc/sudoers.d/clevo-fancontrol
       sudo visudo -c

4. Test the privileged path manually before trusting the GUI:
       sudo -n /opt/clevo-fancontrol/fancontrol.py --ec-helper set 1 128
       sudo -n /opt/clevo-fancontrol/fancontrol.py --ec-helper reset

5. Autostart the GUI in your session (XDG autostart), e.g.
   ~/.config/autostart/clevo-fancontrol.desktop:
       [Desktop Entry]
       Type=Application
       Exec=/opt/clevo-fancontrol/fancontrol.py
       Name=Clevo Fan Control
"""

import os
import sys
import time
import threading
import subprocess
import tkinter as tk
import pystray
from PIL import Image

try:
    from PIL import ImageTk
except Exception:
    ImageTk = None

# ---- CONFIGURATION ----
script_dir = os.path.dirname(os.path.abspath(__file__))
ICON_PATH = os.path.join(script_dir, "lz4fancontrol.ico")

# Temperature (C) -> fan duty (0x00-0xFF). Carried over from the
# Windows version, unchanged — the EC protocol and duty scale are
# identical on Linux.
FAN_CURVE = [
    (0,  0x60),
    (40, 0x60),
    (50, 0x80),
    (60, 0x90),
    (70, 0xB0),
    (80, 0xD0),
    (90, 0xFF),
]

FAN_BUTTONS = [
    ("AUTO", None),
    ("38%",  0x60),
    ("50%",  0x80),
    ("56%",  0x90),
    ("69%",  0xB0),
    ("82%",  0xD0),
    ("100%", 0xFF),
]

POLL_INTERVAL = 3

# EC protocol constants (same as the Windows script)
EC_DATA_PORT = 0x62
EC_CMD_PORT  = 0x66
EC_IBF_FLAG  = 0x02
FAN_SET_CMD  = 0x99
EC_AUTO_DUTY = 0xCC
EC_RELEASE_CMD = 0x98

# Keyboard backlight LED device, as seen via:
#   ls /sys/class/leds/
KB_LED_PATH = "/sys/class/leds/white:kbd_backlight"

EC_HELPER_SCRIPT = os.path.abspath(__file__)
# ---- END CONFIGURATION ----

# ---- SHARED STATE ----
state = {
    "cpu_temp": 0.0,
    "gpu_temp": 0.0,
    "cpu_duty": 0,
    "gpu_duty": 0,
    "cpu_pct":  0,
    "gpu_pct":  0,
}

cpu_override = None
gpu_override = None
override_lock = threading.Lock()


# =====================================================================
#  PRIVILEGED HELPER (only runs when invoked as: --ec-helper <cmd> ...)
# =====================================================================

class ECPort:
    """Thin wrapper around /dev/port for single-byte I/O at fixed addresses."""

    def __init__(self):
        try:
            self.fd = os.open("/dev/port", os.O_RDWR)
        except FileNotFoundError:
            print("/dev/port does not exist - CONFIG_DEVPORT may be "
                  "disabled in this kernel.", file=sys.stderr)
            raise
        except PermissionError:
            print("permission denied opening /dev/port - this helper "
                  "must run as root.", file=sys.stderr)
            raise

    def read(self, port):
        os.lseek(self.fd, port, os.SEEK_SET)
        return os.read(self.fd, 1)[0]

    def write(self, port, value):
        os.lseek(self.fd, port, os.SEEK_SET)
        os.write(self.fd, bytes([value & 0xFF]))

    def close(self):
        os.close(self.fd)


def ec_wait_ready(ec):
    for _ in range(10000):
        if not (ec.read(EC_CMD_PORT) & EC_IBF_FLAG):
            return True
        time.sleep(0.0001)
    return False


def ec_set_fan(ec, index, duty):
    ec_wait_ready(ec)
    ec.write(EC_CMD_PORT, FAN_SET_CMD)
    ec_wait_ready(ec)
    ec.write(EC_DATA_PORT, index)
    ec_wait_ready(ec)
    ec.write(EC_DATA_PORT, duty)


def ec_reset_to_auto(ec):
    ec_set_fan(ec, 1, EC_AUTO_DUTY)
    ec_set_fan(ec, 2, EC_AUTO_DUTY)
    ec_set_fan(ec, 3, EC_AUTO_DUTY)
    time.sleep(2)
    ec_wait_ready(ec)
    ec.write(EC_CMD_PORT, EC_RELEASE_CMD)


def ec_helper_main(argv):
    if os.geteuid() != 0:
        print("--ec-helper must be invoked via sudo (root required)", file=sys.stderr)
        sys.exit(1)

    if not argv:
        print("usage: --ec-helper [set IDX DUTY | reset | kb VALUE]", file=sys.stderr)
        sys.exit(1)

    cmd = argv[0]

    if cmd == "set" and len(argv) == 3:
        try:
            index = int(argv[1], 0)
            duty = int(argv[2], 0)
        except ValueError:
            print("invalid numeric arguments", file=sys.stderr)
            sys.exit(1)
        if index not in (1, 2, 3) or not (0 <= duty <= 255):
            print("index must be 1-3, duty must be 0-255", file=sys.stderr)
            sys.exit(1)
        ec = ECPort()
        try:
            ec_set_fan(ec, index, duty)
        finally:
            ec.close()

    elif cmd == "reset" and len(argv) == 1:
        ec = ECPort()
        try:
            ec_reset_to_auto(ec)
        finally:
            ec.close()

    elif cmd == "kb" and len(argv) == 2:
        try:
            value = int(argv[1], 0)
        except ValueError:
            print("invalid brightness value", file=sys.stderr)
            sys.exit(1)
        if value < 0:
            print("brightness value must be >= 0", file=sys.stderr)
            sys.exit(1)
        try:
            with open(os.path.join(KB_LED_PATH, "brightness"), "w") as f:
                f.write(str(value))
        except OSError as e:
            print(f"failed to write keyboard brightness: {e}", file=sys.stderr)
            sys.exit(1)

    else:
        print("unknown or malformed --ec-helper command", file=sys.stderr)
        sys.exit(1)


# =====================================================================
#  UNPRIVILEGED SIDE
# =====================================================================

def ec_call(*args, timeout=5):
    """Invoke this same script's privileged helper mode via sudo."""
    cmd = ["sudo", "-n", EC_HELPER_SCRIPT, "--ec-helper", *map(str, args)]
    try:
        subprocess.run(cmd, check=True, timeout=timeout,
                        stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        return True
    except subprocess.CalledProcessError as e:
        msg = e.stderr.decode(errors="replace").strip()
        print(f"[ec] helper failed: {msg}", file=sys.stderr)
    except Exception as e:
        print(f"[ec] helper error: {e}", file=sys.stderr)
    return False


def set_cpu_fan(duty):
    ec_call("set", 1, duty)


def set_gpu_fans(duty):
    ec_call("set", 2, duty)
    ec_call("set", 3, duty)


def reset_to_ec_control():
    ec_call("reset", timeout=10)


def get_duty_for_temp(temp):
    duty = FAN_CURVE[0][1]
    for threshold, fan_duty in FAN_CURVE:
        if temp >= threshold:
            duty = fan_duty
    return duty


# ---- SENSOR DISCOVERY (hwmon numbering can shift across boots, so we
#      look chips up by name each run rather than hardcoding paths) ----

def find_hwmon_by_name(target_name):
    base = "/sys/class/hwmon"
    try:
        entries = os.listdir(base)
    except OSError:
        return None
    for entry in entries:
        name_path = os.path.join(base, entry, "name")
        try:
            with open(name_path) as f:
                if f.read().strip() == target_name:
                    return os.path.join(base, entry)
        except OSError:
            continue
    return None


def find_coretemp_package_input():
    hwmon_path = find_hwmon_by_name("coretemp")
    if not hwmon_path:
        return None
    try:
        entries = os.listdir(hwmon_path)
    except OSError:
        return None
    for fname in entries:
        if not fname.endswith("_label"):
            continue
        try:
            with open(os.path.join(hwmon_path, fname)) as f:
                label = f.read().strip()
        except OSError:
            continue
        if label.lower().startswith("package id"):
            return os.path.join(hwmon_path, fname.replace("_label", "_input"))
    return None


def find_nouveau_temp_input():
    hwmon_path = find_hwmon_by_name("nouveau")
    if not hwmon_path:
        return None
    candidate = os.path.join(hwmon_path, "temp1_input")
    return candidate if os.path.exists(candidate) else None


CPU_TEMP_PATH = find_coretemp_package_input()
GPU_TEMP_PATH = find_nouveau_temp_input()


def _read_millideg(path):
    if not path:
        return None
    try:
        with open(path) as f:
            return int(f.read().strip())
    except (OSError, ValueError):
        return None


def get_temps():
    cpu_raw = _read_millideg(CPU_TEMP_PATH)
    gpu_raw = _read_millideg(GPU_TEMP_PATH)
    cpu_temp = cpu_raw / 1000.0 if cpu_raw is not None else None
    gpu_temp = gpu_raw / 1000.0 if gpu_raw is not None else None
    return cpu_temp, gpu_temp


# ---- KEYBOARD BACKLIGHT ----

def get_kb_max_brightness():
    try:
        with open(os.path.join(KB_LED_PATH, "max_brightness")) as f:
            return max(1, int(f.read().strip()))
    except OSError:
        return 1


def set_kb_level(level):
    """level is 0-5 (matching the original button row); scaled onto
    whatever range this LED device actually supports."""
    max_b = get_kb_max_brightness()
    value = 0 if level <= 0 else round(level / 5 * max_b)
    brightness_file = os.path.join(KB_LED_PATH, "brightness")
    try:
        with open(brightness_file, "w") as f:
            f.write(str(value))
    except PermissionError:
        ec_call("kb", value)
    except OSError:
        pass


# ---- GUI WINDOW ----
window_root = None
window_visible = False


def build_window():
    global window_root, window_visible

    BG      = "#1a1a1a"
    CARD    = "#242424"
    ACCENT  = "#e8a020"
    TEXT    = "#f0f0f0"
    SUBTEXT = "#888888"
    BAR_BG  = "#333333"

    window_root = tk.Tk()
    window_root.title("lz4 Fan Control")
    window_root.resizable(False, False)
    window_root.configure(bg=BG)

    if os.path.exists(ICON_PATH) and ImageTk is not None:
        try:
            icon_img = ImageTk.PhotoImage(Image.open(ICON_PATH))
            window_root.iconphoto(False, icon_img)
            window_root._icon_img_ref = icon_img  # keep alive
        except Exception:
            pass

    def on_close():
        global window_visible
        window_root.withdraw()
        window_visible = False

    window_root.protocol("WM_DELETE_WINDOW", on_close)

    def make_temp_row(parent, label):
        row = tk.Frame(parent, bg=CARD)
        row.pack(fill="x", padx=14, pady=3)
        tk.Label(row, text=label, font=("Consolas", 9), bg=CARD,
                 fg=SUBTEXT, width=6, anchor="w").pack(side="left")
        temp_lbl = tk.Label(row, text="--C", font=("Consolas", 9),
                             bg=CARD, fg=SUBTEXT, width=6, anchor="w")
        temp_lbl.pack(side="left")
        bar = tk.Canvas(row, bg=BAR_BG, height=8, highlightthickness=0)
        bar.pack(side="left", fill="x", expand=True, padx=(6, 8))
        return bar, temp_lbl

    temp_card = tk.Frame(window_root, bg=CARD, pady=12)
    temp_card.pack(fill="x", padx=18)
    cpu_bar, cpu_temp_lbl = make_temp_row(temp_card, "CPU")
    gpu_bar, gpu_temp_lbl = make_temp_row(temp_card, "GPU")

    def make_fan_row(parent, label):
        row = tk.Frame(parent, bg=CARD)
        row.pack(fill="x", padx=14, pady=3)
        tk.Label(row, text=label, font=("Consolas", 9), bg=CARD,
                 fg=SUBTEXT, width=6, anchor="w").pack(side="left")
        pct_lbl = tk.Label(row, text="--%", font=("Consolas", 9),
                            bg=CARD, fg=SUBTEXT, width=6, anchor="w")
        pct_lbl.pack(side="left")
        bar = tk.Canvas(row, bg=BAR_BG, height=8, highlightthickness=0)
        bar.pack(side="left", fill="x", expand=True, padx=(6, 8))
        return bar, pct_lbl

    fan_card = tk.Frame(window_root, bg=CARD, pady=12)
    fan_card.pack(fill="x", padx=18)
    cpu_fan_bar, cpu_fan_lbl = make_fan_row(fan_card, "CPU")
    gpu_fan_bar, gpu_fan_lbl = make_fan_row(fan_card, "GPU")

    def make_fan_buttons(parent, label, set_override_fn):
        section = tk.Frame(parent, bg=CARD, pady=10)
        section.pack(fill="x", padx=18, pady=(0, 6))
        header = tk.Frame(section, bg=CARD)
        header.pack(fill="x", padx=14, pady=(0, 8))
        tk.Label(header, text=label, font=("Consolas", 9),
                 bg=CARD, fg=SUBTEXT, anchor="w").pack(side="left")
        btn_frame = tk.Frame(section, bg=CARD)
        btn_frame.pack(fill="x", padx=14)
        btn_refs = {}

        def on_click(duty, btns):
            set_override_fn(duty)
            for d, b in btns.items():
                if d == duty:
                    b.config(bg=ACCENT, fg="#1a1a1a")
                else:
                    b.config(bg="#333333", fg=SUBTEXT)

        for lbl_text, duty in FAN_BUTTONS:
            is_auto = (duty is None)
            btn = tk.Button(
                btn_frame, text=lbl_text, font=("Consolas", 8, "bold"),
                bg=ACCENT if is_auto else "#333333",
                fg="#1a1a1a" if is_auto else SUBTEXT,
                activebackground=ACCENT, activeforeground="#1a1a1a",
                relief="flat", padx=5, pady=4, cursor="hand2",
            )
            btn.pack(side="left", padx=(0, 4))
            btn_refs[duty] = btn

        for duty, btn in btn_refs.items():
            btn.config(command=lambda d=duty, b=btn_refs: on_click(d, b))

        return section

    def set_cpu_override(duty):
        global cpu_override
        with override_lock:
            cpu_override = duty

    def set_gpu_override(duty):
        global gpu_override
        with override_lock:
            gpu_override = duty

    tk.Frame(window_root, bg=BG, height=6).pack(fill="x")
    make_fan_buttons(window_root, "CPU FAN", set_cpu_override)
    make_fan_buttons(window_root, "GPU FAN", set_gpu_override)

    kb_card = tk.Frame(window_root, bg=CARD, pady=10)
    kb_card.pack(fill="x", padx=18)
    tk.Label(kb_card, text="KEYBOARD", font=("Consolas", 9),
             bg=CARD, fg=SUBTEXT, anchor="w").pack(fill="x", padx=14, pady=(0, 8))
    kb_btn_frame = tk.Frame(kb_card, bg=CARD)
    kb_btn_frame.pack(fill="x", padx=14)
    kb_refs = {}

    def on_kb_click(level):
        set_kb_level(level)
        for lvl, btn in kb_refs.items():
            btn.config(bg=ACCENT if lvl == level else "#333333",
                       fg="#1a1a1a" if lvl == level else SUBTEXT)

    for i, lbl_text in enumerate(["Off", "1", "2", "3", "4", "5"]):
        btn = tk.Button(
            kb_btn_frame, text=lbl_text, font=("Consolas", 9, "bold"),
            bg=ACCENT if i == 0 else "#333333",
            fg="#1a1a1a" if i == 0 else SUBTEXT,
            activebackground=ACCENT, activeforeground="#1a1a1a",
            relief="flat", padx=8, pady=4, cursor="hand2",
            command=lambda lvl=i: on_kb_click(lvl)
        )
        btn.pack(side="left", padx=(0, 6))
        kb_refs[i] = btn

    footer = tk.Frame(window_root, bg=BG)
    footer.pack(fill="x", padx=18, pady=(10, 10))
    status_lbl = tk.Label(footer, text="initializing...", font=("Consolas", 8),
                           bg=BG, fg=SUBTEXT)
    status_lbl.pack(side="left")

    def temp_color(temp):
        if temp < 60:
            return "#50c878"
        elif temp < 75:
            return "#e8a020"
        else:
            return "#e84020"

    def draw_bar(canvas, value, max_value=100, color="#e8a020"):
        canvas.update_idletasks()
        w = canvas.winfo_width()
        h = canvas.winfo_height()
        if w <= 1:
            return
        canvas.delete("all")
        fill_w = int(w * min(value / max_value, 1.0))
        if fill_w > 0:
            canvas.create_rectangle(0, 0, fill_w, h, fill=color, outline="")

    def refresh():
        cpu = state["cpu_temp"]
        gpu = state["gpu_temp"]
        cpct = state["cpu_pct"]
        gpct = state["gpu_pct"]
        cpu_temp_lbl.config(text=f"{cpu:.0f}C")
        gpu_temp_lbl.config(text=f"{gpu:.0f}C")
        cpu_fan_lbl.config(text=f"{cpct}%")
        gpu_fan_lbl.config(text=f"{gpct}%")
        draw_bar(cpu_bar, cpu, color=temp_color(cpu))
        draw_bar(gpu_bar, gpu, color=temp_color(gpu))
        draw_bar(cpu_fan_bar, cpct, color="#5090e8")
        draw_bar(gpu_fan_bar, gpct, color="#5090e8")
        status_lbl.config(text=f"cpu duty {hex(state['cpu_duty'])}  -  gpu duty {hex(state['gpu_duty'])}")
        window_root.after(POLL_INTERVAL * 1000, refresh)

    window_root.after(POLL_INTERVAL * 1000, refresh)
    window_root.update_idletasks()
    window_root.geometry(f"340x{window_root.winfo_reqheight()}")
    window_root.withdraw()
    window_root.mainloop()


def show_window():
    global window_visible
    if window_root:
        window_root.deiconify()
        window_root.lift()
        window_root.focus_force()
        window_visible = True


# ---- FAN CONTROL LOOP ----
stop_event = threading.Event()


def control_loop():
    global cpu_override, gpu_override
    last_cpu_duty = None
    last_gpu_duty = None

    while not stop_event.is_set():
        cpu_temp, gpu_temp = get_temps()
        if cpu_temp is None and gpu_temp is None:
            time.sleep(POLL_INTERVAL)
            continue

        with override_lock:
            c_override = cpu_override
            g_override = gpu_override

        if c_override is not None:
            cpu_duty = c_override
        else:
            cpu_duty = get_duty_for_temp(cpu_temp) if cpu_temp else FAN_CURVE[0][1]

        if g_override is not None:
            gpu_duty = g_override
        else:
            gpu_duty = get_duty_for_temp(gpu_temp) if gpu_temp else FAN_CURVE[0][1]

        state["cpu_temp"] = cpu_temp or 0.0
        state["gpu_temp"] = gpu_temp or 0.0
        state["cpu_duty"] = cpu_duty
        state["gpu_duty"] = gpu_duty
        state["cpu_pct"]  = round(cpu_duty / 255 * 100)
        state["gpu_pct"]  = round(gpu_duty / 255 * 100)

        if cpu_duty != last_cpu_duty:
            set_cpu_fan(cpu_duty)
            last_cpu_duty = cpu_duty

        if gpu_duty != last_gpu_duty:
            set_gpu_fans(gpu_duty)
            last_gpu_duty = gpu_duty

        time.sleep(POLL_INTERVAL)

    reset_to_ec_control()


# ---- ENTRY POINT ----

def main():
    set_kb_level(0)

    fan_thread = threading.Thread(target=control_loop, daemon=True)
    fan_thread.start()

    gui_thread = threading.Thread(target=build_window, daemon=True)
    gui_thread.start()

    time.sleep(1)

    if os.path.exists(ICON_PATH):
        icon_image = Image.open(ICON_PATH)
    else:
        icon_image = Image.new("RGB", (16, 16), "#e8a020")

    def on_show(icon, item):
        if window_root:
            window_root.after(0, show_window)

    def on_exit(icon, item):
        stop_event.set()
        if window_root:
            window_root.after(0, window_root.quit)
        icon.stop()

    menu = pystray.Menu(
        pystray.MenuItem("Show", on_show, default=True),
        pystray.Menu.SEPARATOR,
        pystray.MenuItem("Exit", on_exit),
    )

    icon = pystray.Icon("lz4fancontrol", icon_image, "lz4 Fan Control", menu)
    icon.run()

    # Make sure the reset-to-auto sequence actually completes before
    # the process exits (the original Windows script raced this).
    fan_thread.join(timeout=5)


if __name__ == "__main__":
    if len(sys.argv) >= 2 and sys.argv[1] == "--ec-helper":
        ec_helper_main(sys.argv[2:])
        sys.exit(0)
    main()
