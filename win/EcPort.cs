using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LzHwCtrl
{
    /// <summary>
    /// Raw EC port I/O via inpoutx64.dll. This is the Windows equivalent of
    /// the Linux ECPort class in lzhwctrl.py, which used /dev/port at the
    /// same fixed addresses (0x62 data / 0x66 cmd). Protocol constants below
    /// are carried over unchanged from both the Linux and original Windows
    /// scripts -- the EC doesn't know or care which OS is poking it.
    /// </summary>
    internal static class EcPort
    {
        // inpoutx64.dll exports (standard InpOut32 family API).
        // DLL must sit next to the exe, or on PATH.
        [DllImport("inpoutx64.dll", EntryPoint = "Out32")]
        private static extern void Out32(short address, short data);

        [DllImport("inpoutx64.dll", EntryPoint = "Inp32")]
        private static extern short Inp32(short address);

        [DllImport("inpoutx64.dll", EntryPoint = "IsInpOutDriverOpen")]
        private static extern uint IsInpOutDriverOpen();

        public const short EC_DATA_PORT = 0x62;
        public const short EC_CMD_PORT  = 0x66;
        public const byte  EC_IBF_FLAG  = 0x02;
        public const byte  FAN_SET_CMD  = 0x99;
        public const byte  EC_AUTO_DUTY = 0xCC;
        public const byte  EC_RELEASE_CMD = 0x98;

        public static void EnsureDriverLoaded()
        {
            if (IsInpOutDriverOpen() == 0)
                throw new InvalidOperationException(
                    "inpoutx64 kernel driver is not loaded. Run this program " +
                    "as Administrator (it self-elevates on launch) -- the " +
                    "driver service is installed/started automatically the " +
                    "first time inpoutx64.dll is loaded by an elevated process.");
        }

        public static byte Read(short port) => (byte)Inp32(port);

        public static void Write(short port, byte value) => Out32(port, value);

        public static bool WaitReady()
        {
            for (int i = 0; i < 10000; i++)
            {
                if ((Read(EC_CMD_PORT) & EC_IBF_FLAG) == 0)
                    return true;
                Thread.Sleep(0); // ~0.1ms equivalent isn't available; yield is close enough
            }
            return false;
        }

        public static void SetFan(byte index, byte duty)
        {
            WaitReady();
            Write(EC_CMD_PORT, FAN_SET_CMD);
            WaitReady();
            Write(EC_DATA_PORT, index);
            WaitReady();
            Write(EC_DATA_PORT, duty);
        }

        public static void ResetToAuto()
        {
            SetFan(1, EC_AUTO_DUTY);
            SetFan(2, EC_AUTO_DUTY);
            SetFan(3, EC_AUTO_DUTY);
            Thread.Sleep(2000);
            WaitReady();
            Write(EC_CMD_PORT, EC_RELEASE_CMD);
        }
    }
}
