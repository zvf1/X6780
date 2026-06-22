using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LzHwCtrl
{
    /// <summary>
    /// Controls the white keyboard backlight by sending IOCTL_ACPI_EVAL_METHOD
    /// directly to the PNP0C14 ACPI WMI device.
    ///
    /// The two PNP0C14 instances on this board have no registered device
    /// interface GUIDs (confirmed via Get-PnpDeviceProperty), so the normal
    /// SetupDi enumeration finds nothing. However their PDO names are known:
    ///   ACPI\PNP0C14\0    -> \Device\00000028
    ///   ACPI\PNP0C14\MXM2 -> \Device\00000031
    ///
    /// CreateFile can open kernel device objects directly using the
    /// \\.\GlobalRoot\Device\XXXXXXXX syntax, which bypasses the device
    /// interface layer entirely.
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
        private const uint GENERIC_READ_WRITE  = 0xC0000000;
        private const uint FILE_SHARE_ALL      = 0x00000007;
        private const uint OPEN_EXISTING       = 3;

        // IOCTL_ACPI_EVAL_METHOD (acpiioct.h)
        private const uint IOCTL_ACPI_EVAL_METHOD = 0x00224010;

        // ACPI_EVAL_INPUT_BUFFER.Signature
        private const uint ACPI_EVAL_INPUT_BUFFER_SIGNATURE = 0x44494E49; // 'INDI'

        // ACPI_METHOD_ARGUMENT.Type
        private const ushort ACPI_METHOD_ARGUMENT_INTEGER = 0;

        // Clevo WMI command IDs (from clevo_wmi.c)
        private const uint CmdSetKb = 0x27;
        private const uint CmdGetKb = 0x3D;

        // Device paths: \\.\GlobalRoot maps to the NT object namespace root,
        // so \\.\GlobalRoot\Device\00000028 opens \Device\00000028 directly.
        private static readonly string[] DevicePaths =
        {
            @"\\.\GlobalRoot\Device\00000028",  // ACPI\PNP0C14\0
            @"\\.\GlobalRoot\Device\00000031",  // ACPI\PNP0C14\MXM2
        };

        // ACPI method names the WMI bridge generates for GUID ABBC0F6D.
        private static readonly string[] MethodNames = { "WMAB", "WMAD", "WMA0", "WMBC" };

        private static readonly string LogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "keyboard_debug.log");

        // ── Public API ──────────────────────────────────────────────────────

        public static bool SetLevel(int level)
        {
            level = Math.Max(0, Math.Min(5, level));
            Log($"SetLevel({level})");
            bool ok = Invoke(CmdSetKb, (uint)level, out uint result);
            Log($"SetLevel done: ok={ok} result=0x{result:X}");
            return ok;
        }

        public static bool TryGetLevel(out int level)
        {
            level = 0;
            if (!Invoke(CmdGetKb, 0, out uint r)) return false;
            level = (int)(r & 0xFF);
            return true;
        }

        // ── Core ────────────────────────────────────────────────────────────

        private static bool Invoke(uint methodId, uint arg, out uint result)
        {
            result = 0;
            foreach (string devPath in DevicePaths)
            {
                IntPtr h = CreateFile(devPath, GENERIC_READ_WRITE,
                    FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                if (h == INVALID_HANDLE)
                {
                    Log($"  CreateFile({devPath}) failed: err={Marshal.GetLastWin32Error()}");
                    continue;
                }

                Log($"  Opened {devPath}");
                try
                {
                    foreach (string method in MethodNames)
                    {
                        if (TryEval(h, method, methodId, arg, out result))
                        {
                            Log($"  SUCCESS: {devPath} / {method}");
                            return true;
                        }
                    }
                }
                finally { CloseHandle(h); }
            }

            Log("  No device/method combination succeeded.");
            return false;
        }

        private static bool TryEval(IntPtr hDev, string methodName,
            uint methodId, uint arg, out uint result)
        {
            result = 0;
            try
            {
                // Pack method name as little-endian ULONG (e.g. "WMAB" -> 0x42414D57)
                if (methodName.Length < 4) methodName = methodName.PadRight(4);
                uint nameUlong = (uint)(
                    methodName[0]         |
                    (methodName[1] <<  8) |
                    (methodName[2] << 16) |
                    (methodName[3] << 24));

                // ACPI_EVAL_INPUT_BUFFER layout (acpiioct.h):
                //  [0..3]   Signature         ULONG  = 'INDI'
                //  [4..7]   MethodNameAsUlong ULONG  = packed method name
                //  [8..11]  ArgumentCount     ULONG  = 2
                //  -- ACPI_METHOD_ARGUMENT[0] (MethodId) --
                //  [12..13] Type     USHORT = 0 (INTEGER)
                //  [14..15] DataLen  USHORT = 4
                //  [16..19] Data     ULONG  = methodId
                //  -- ACPI_METHOD_ARGUMENT[1] (arg/brightness) --
                //  [20..21] Type     USHORT = 0 (INTEGER)
                //  [22..23] DataLen  USHORT = 4
                //  [24..27] Data     ULONG  = arg
                byte[] inBuf = new byte[28];
                WL(inBuf,  0, ACPI_EVAL_INPUT_BUFFER_SIGNATURE);
                WL(inBuf,  4, nameUlong);
                WL(inBuf,  8, 2);
                WS(inBuf, 12, ACPI_METHOD_ARGUMENT_INTEGER);
                WS(inBuf, 14, 4);
                WL(inBuf, 16, methodId);
                WS(inBuf, 20, ACPI_METHOD_ARGUMENT_INTEGER);
                WS(inBuf, 22, 4);
                WL(inBuf, 24, arg);

                byte[] outBuf = new byte[256];
                bool ok = DeviceIoControl(hDev, IOCTL_ACPI_EVAL_METHOD,
                    inBuf, (uint)inBuf.Length,
                    outBuf, (uint)outBuf.Length,
                    out uint returned, IntPtr.Zero);

                int err = Marshal.GetLastWin32Error();
                Log($"    {methodName} id=0x{methodId:X2} arg={arg}: ok={ok} winerr={err} returned={returned}");

                if (ok && returned >= 20)
                    result = RL(outBuf, 16);

                return ok;
            }
            catch (Exception ex)
            {
                Log($"    {methodName}: exception: {ex.Message}");
                return false;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static void WL(byte[] b, int o, uint v)
        {
            b[o]=(byte)v; b[o+1]=(byte)(v>>8); b[o+2]=(byte)(v>>16); b[o+3]=(byte)(v>>24);
        }
        private static void WS(byte[] b, int o, ushort v)
        {
            b[o]=(byte)v; b[o+1]=(byte)(v>>8);
        }
        private static uint RL(byte[] b, int o) =>
            (uint)(b[o]|(b[o+1]<<8)|(b[o+2]<<16)|(b[o+3]<<24));

        private static void Log(string msg)
        {
            try
            {
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
                Console.Error.WriteLine(line);
                File.AppendAllText(LogPath, line + "\r\n", System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}
