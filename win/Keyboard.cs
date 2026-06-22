using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LzHwCtrl
{
    /// <summary>
    /// Keyboard backlight control via clevo_kbd.sys.
    ///
    /// The driver exposes a Win32 device at \\.\ClevoKbd.  It is loaded
    /// automatically when Windows starts (the INF sets StartType = DEMAND_START,
    /// but pnputil starts it on first device-arrival after install).
    ///
    /// IOCTLs (METHOD_BUFFERED, FILE_ANY_ACCESS so no admin needed here):
    ///   SET  0x22200400  --  input : DWORD level (0-5)
    ///   GET  0x22200404  --  output: DWORD level
    ///
    /// IOCTL codes match the #defines in clevo_kbd.c:
    ///   CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, 0x800, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
    ///   = (0x22 << 16) | (0x800 << 2) | 0 = 0x22002000
    ///   CTL_CODE(0x22, 0x801, 0, 0) = 0x22002004
    ///
    /// Levels: 0 = off, 1-5 = increasing brightness.
    /// Some boards only support 0/1/2 — if you see no change above level 2,
    /// clamp calls to min(level, 2) or adjust the UI buttons.
    /// </summary>
    internal static class Keyboard
    {
        // ----------------------------------------------------------------
        // These must match the CTL_CODE values in clevo_kbd.c exactly.
        // CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)
        // FILE_DEVICE_UNKNOWN = 0x00000022
        // METHOD_BUFFERED     = 0
        // FILE_ANY_ACCESS     = 0
        // Result: (0x22 << 16) | (0x800 << 2) | 0 = 0x22002000
        // ----------------------------------------------------------------
        private const uint IOCTL_SET_LEVEL = 0x22002000;
        private const uint IOCTL_GET_LEVEL = 0x22002004;

        private const string DevicePath = @"\\.\ClevoKbd";

        // Win32 constants
        private const uint GENERIC_READ_WRITE = 0xC0000000;
        private const uint FILE_SHARE_READ    = 0x00000001;
        private const uint FILE_SHARE_WRITE   = 0x00000002;
        private const uint OPEN_EXISTING      = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // ----------------------------------------------------------------
        // P/Invoke
        // ----------------------------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint   dwDesiredAccess,
            uint   dwShareMode,
            IntPtr lpSecurityAttributes,
            uint   dwCreationDisposition,
            uint   dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint   dwIoControlCode,
            IntPtr lpInBuffer,
            uint   nInBufferSize,
            IntPtr lpOutBuffer,
            uint   nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // ----------------------------------------------------------------
        // Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns true if the clevo_kbd.sys device is present and openable.
        /// Call this once at startup to decide whether to show the KB row.
        /// </summary>
        public static bool IsSupported
        {
            get
            {
                IntPtr h = OpenDevice();
                if (h == INVALID_HANDLE_VALUE) return false;
                CloseHandle(h);
                return true;
            }
        }

        /// <summary>
        /// Sets keyboard backlight to <paramref name="level"/> (0 = off, 1-5).
        /// Returns true on success.
        /// </summary>
        public static bool SetLevel(int level)
        {
            if (level < 0 || level > 5) return false;

            IntPtr h = OpenDevice();
            if (h == INVALID_HANDLE_VALUE) return false;

            try
            {
                return SendSet(h, (uint)level);
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>
        /// Reads the current backlight level from the driver.
        /// Returns true and sets <paramref name="level"/> on success.
        /// </summary>
        public static bool TryGetLevel(out int level)
        {
            level = 0;

            IntPtr h = OpenDevice();
            if (h == INVALID_HANDLE_VALUE) return false;

            try
            {
                if (!SendGet(h, out uint raw)) return false;
                level = (int)raw;
                return true;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        // ----------------------------------------------------------------
        // Private helpers
        // ----------------------------------------------------------------

        private static IntPtr OpenDevice()
        {
            return CreateFile(
                DevicePath,
                GENERIC_READ_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);
        }

        private static bool SendSet(IntPtr h, uint level)
        {
            // Pin a DWORD on the stack and pass it as the input buffer.
            unsafe
            {
                uint val = level;
                IntPtr pIn = new IntPtr(&val);
                return DeviceIoControl(
                    h,
                    IOCTL_SET_LEVEL,
                    pIn,  (uint)sizeof(uint),
                    IntPtr.Zero, 0,
                    out _,
                    IntPtr.Zero);
            }
        }

        private static bool SendGet(IntPtr h, out uint level)
        {
            unsafe
            {
                uint val = 0;
                IntPtr pOut = new IntPtr(&val);
                bool ok = DeviceIoControl(
                    h,
                    IOCTL_GET_LEVEL,
                    IntPtr.Zero, 0,
                    pOut, (uint)sizeof(uint),
                    out _,
                    IntPtr.Zero);
                level = val;
                return ok;
            }
        }
    }
}
