namespace LzHwCtrl
{
    /// <summary>
    /// Replaces set_kb_level() from lzhwctrl.py.
    ///
    /// On Linux that function wrote to /sys/class/leds/white:kbd_backlight,
    /// a path supplied by the tuxedo_keyboard driver -- which itself talks
    /// to the EC using a Clevo-specific keyboard-backlight command sequence
    /// that ISN'T the same as the fan-duty command (0x99) above.
    ///
    /// There's no Windows sysfs equivalent, and I don't have the exact EC
    /// command/data bytes tuxedo_keyboard sends for backlight level on the
    /// P65/P67-class boards (it varies by Clevo barebone revision and is
    /// usually a multi-byte command sequence, not a single Out32 call).
    ///
    /// To port this for real:
    ///   1. Find the relevant function in tuxedo-drivers, e.g.
    ///      tuxedo_keyboard.c / clevo_keyboard.c in your forked repo --
    ///      look for the brightness "store" callback and trace which
    ///      EC command byte(s) it writes before/after the brightness value.
    ///   2. Port that exact byte sequence here as another EcPort.Write()
    ///      call sequence (same pattern as SetFan above).
    /// Until then this is a no-op so the rest of the app still builds/runs.
    /// </summary>
    internal static class Keyboard
    {
        public static void SetLevel(int level)
        {
            // TODO: port the tuxedo_keyboard EC command sequence here.
            // level is 0-5, same scale as the Linux/Windows UI buttons.
        }
    }
}
