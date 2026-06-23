using System;
using System.Runtime.InteropServices;

namespace LzHwCtrl
{
    /// <summary>
    /// Controls the white keyboard backlight via clevo_kb.sys — a minimal
    /// KMDF driver that evaluates the ACPI _DSM method on the CLV0001 device.
    ///
    /// If the driver is not installed, all calls silently return false and
    /// the keyboard row shows a "driver not found" message.
    ///
    /// Install the driver first by running build_and_install.ps1 from the
    /// driver/ subdirectory of the repo (elevated PowerShell).
    /// </summary>
    internal static class Keyboard
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode,
            byte[] lpInBuffer,  uint nInBufferSize,
            byte[] lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);
        private const uint GENERIC_READ_WRITE = 0xC0000000;
        private const uint FILE_SHARE_ALL     = 0x00000007;
        private const uint OPEN_EXISTING      = 3;

        // CTL_CODE(0x22, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)
        private const uint IOCTL_CLEVOKB_SET_LEVEL = 0x00220000 | (0x800 << 2);
        private const uint IOCTL_CLEVOKB_GET_LEVEL = 0x00220000 | (0x801 << 2);

        private const string DevicePath = @"\\.\ClevoKbBacklight";

        public static bool IsDriverPresent
        {
            get
            {
                IntPtr h = CreateFile(DevicePath, GENERIC_READ_WRITE,
                    FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == INVALID_HANDLE) return false;
                CloseHandle(h);
                return true;
            }
        }

        public static bool SetLevel(int level)
        {
            level = Math.Max(0, Math.Min(5, level));
            IntPtr h = CreateFile(DevicePath, GENERIC_READ_WRITE,
                FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE) return false;
            try
            {
                byte[] inBuf = BitConverter.GetBytes((uint)level);
                byte[] outBuf = new byte[4];
                return DeviceIoControl(h, IOCTL_CLEVOKB_SET_LEVEL,
                    inBuf, (uint)inBuf.Length,
                    outBuf, (uint)outBuf.Length,
                    out _, IntPtr.Zero);
            }
            finally { CloseHandle(h); }
        }

        public static bool TryGetLevel(out int level)
        {
            level = 0;
            IntPtr h = CreateFile(DevicePath, GENERIC_READ_WRITE,
                FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE) return false;
            try
            {
                byte[] outBuf = new byte[4];
                bool ok = DeviceIoControl(h, IOCTL_CLEVOKB_GET_LEVEL,
                    null, 0,
                    outBuf, (uint)outBuf.Length,
                    out uint returned, IntPtr.Zero);
                if (ok && returned >= 4)
                    level = (int)BitConverter.ToUInt32(outBuf, 0);
                return ok;
            }
            finally { CloseHandle(h); }
        }
    }
}
