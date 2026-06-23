using System.Threading;

namespace LzHwCtrl
{
    /// <summary>
    /// Controls the white keyboard backlight via direct EC port I/O,
    /// using the same inpoutx64.dll mechanism that fan control uses.
    ///
    /// The EC command protocol mirrors the fan control sequence in EcPort.cs:
    ///   WaitReady -> Write(CMD_PORT, KB_SET_CMD) -> WaitReady -> Write(DATA_PORT, level)
    ///
    /// Command 0x27 = CLEVO_CMD_SET_KB_WHITE_LEDS (from clevo_interfaces.h)
    /// Command 0x3D = CLEVO_CMD_GET_KB_WHITE_LEDS
    /// Brightness is 0-5 matching the button row (0 = off).
    ///
    /// This is the same EC register access that the BIOS uses internally when
    /// Fn+F4 cycles brightness — we're just setting the level directly instead
    /// of cycling it.
    /// </summary>
    internal static class Keyboard
    {
        private const byte KB_SET_CMD = 0x27;
        private const byte KB_GET_CMD = 0x3D;

        public static bool SetLevel(int level)
        {
            level = System.Math.Max(0, System.Math.Min(5, level));
            try
            {
                EcPort.WaitReady();
                EcPort.Write(EcPort.EC_CMD_PORT, KB_SET_CMD);
                EcPort.WaitReady();
                EcPort.Write(EcPort.EC_DATA_PORT, (byte)level);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetLevel(out int level)
        {
            level = 0;
            try
            {
                EcPort.WaitReady();
                EcPort.Write(EcPort.EC_CMD_PORT, KB_GET_CMD);
                EcPort.WaitReady();
                level = EcPort.Read(EcPort.EC_DATA_PORT);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
