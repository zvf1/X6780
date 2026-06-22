using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LzHwCtrl
{
    /// <summary>
    /// Controls the white keyboard backlight via the Clevo WMI method interface.
    ///
    /// HOW THE LINUX DRIVER ACTUALLY WORKS (clevo_wmi.c):
    ///   wmi_evaluate_method(CLEVO_WMI_METHOD_GUID,
    ///       instance  = 0x00,
    ///       method_id = cmd,          // e.g. 0x27 = set KB brightness
    ///       in_buf    = &arg as u32,  // e.g. brightness 0-5
    ///       out_buf   = ...)
    ///
    /// On Windows, wmi_evaluate_method maps to IOCTL_ACPI_EVAL_METHOD sent to
    /// the PNP0C14 device with:
    ///   ACPI_EVAL_INPUT_BUFFER_SIMPLE_INTEGER:
    ///     Signature         = 0x01AA0001
    ///     MethodNameAsUlong = cmd packed as ULONG (e.g. 0x00000027)
    ///     IntegerArgument   = arg (e.g. brightness)
    ///
    /// The PNP0C14 device is opened via its symbolic link in the device
    /// interface list. Since no interface GUID is registered, we use the
    /// CM_Get_Device_Interface_List API with the ACPI WMI device GUID, and
    /// if that fails, fall back to opening by the known symlink path that
    /// wmiacpi.sys creates at \\\\.\\WMIDataDevice (the standard WMI device
    /// user-mode open path).
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
            byte[] lpInBuffer, uint nInBufferSize,
            byte[] lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);
        private const uint GENERIC_READ_WRITE = 0xC0000000;
        private const uint FILE_SHARE_ALL     = 0x00000007;
        private const uint OPEN_EXISTING      = 3;

        // IOCTL_ACPI_EVAL_METHOD (acpiioct.h: FILE_DEVICE_ACPI=0x22, func=0x100,
        // METHOD_BUFFERED=0, FILE_READ_ACCESS|FILE_WRITE_ACCESS=3 -> 0x00224010)
        private const uint IOCTL_ACPI_EVAL_METHOD = 0x00224010;

        // ACPI_EVAL_INPUT_BUFFER_SIMPLE_INTEGER_SIGNATURE (acpiioct.h: 0x01AA0001)
        // Used when the method takes exactly one ULONG argument.
        private const uint ACPI_EVAL_INPUT_BUFFER_SIMPLE_INTEGER_SIGNATURE = 0x01AA0001;

        // Clevo WMI method IDs (clevo_interfaces.h)
        private const uint CmdSetKb = 0x27;
        private const uint CmdGetKb = 0x3D;

        // The WMI device that wmiacpi.sys exposes for PNP0C14 devices.
        // User-mode WMI calls go through this device; it routes to the
        // correct PNP0C14 instance based on the GUID in the request.
        // This is the same device that WMI queries use internally.
        private static readonly string[] DevicePaths =
        {
            @"\\.\WMIDataDevice",       // Standard wmiacpi.sys user-mode path
            @"\\.\GLOBALROOT\Device\WMIDataDevice",
        };

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

        private static bool Invoke(uint cmd, uint arg, out uint result)
        {
            result = 0;
            foreach (string path in DevicePaths)
            {
                IntPtr h = CreateFile(path, GENERIC_READ_WRITE,
                    FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                if (h == INVALID_HANDLE)
                {
                    Log($"  CreateFile({path}) failed: err={Marshal.GetLastWin32Error()}");
                    continue;
                }

                Log($"  Opened {path}");
                try
                {
                    if (TryEval(h, cmd, arg, out result))
                        return true;
                }
                finally { CloseHandle(h); }
            }

            Log("  All paths failed.");
            return false;
        }

        /// <summary>
        /// Sends IOCTL_ACPI_EVAL_METHOD with ACPI_EVAL_INPUT_BUFFER_SIMPLE_INTEGER.
        ///
        /// Layout (acpiioct.h):
        ///   [0..3]  Signature         = 0x01AA0001
        ///   [4..7]  MethodNameAsUlong = cmd (packed as little-endian ULONG)
        ///   [8..15] IntegerArgument   = arg (ULONG64)
        ///
        /// This matches how clevo_wmi.c calls wmi_evaluate_method():
        ///   instance=0, method_id=cmd, in_buf=&arg(u32)
        /// </summary>
        private static bool TryEval(IntPtr hDev, uint cmd, uint arg, out uint result)
        {
            result = 0;
            try
            {
                // ACPI_EVAL_INPUT_BUFFER_SIMPLE_INTEGER (16 bytes)
                byte[] inBuf = new byte[16];
                WL(inBuf, 0, ACPI_EVAL_INPUT_BUFFER_SIMPLE_INTEGER_SIGNATURE);
                WL(inBuf, 4, cmd);          // MethodNameAsUlong
                WL(inBuf, 8, arg);          // IntegerArgument low 32 bits
                WL(inBuf, 12, 0);           // IntegerArgument high 32 bits

                byte[] outBuf = new byte[256];
                bool ok = DeviceIoControl(hDev, IOCTL_ACPI_EVAL_METHOD,
                    inBuf, (uint)inBuf.Length,
                    outBuf, (uint)outBuf.Length,
                    out uint returned, IntPtr.Zero);

                int err = Marshal.GetLastWin32Error();
                Log($"    cmd=0x{cmd:X2} arg={arg}: ok={ok} winerr={err} returned={returned}");

                // ACPI_EVAL_OUTPUT_BUFFER:
                //   [0..3]  Signature
                //   [4..7]  Length
                //   [8..11] Count
                //   [12..13] Arg[0].Type
                //   [14..15] Arg[0].DataLength
                //   [16..19] Arg[0].Data
                if (ok && returned >= 20)
                    result = RL(outBuf, 16);

                return ok;
            }
            catch (Exception ex)
            {
                Log($"    Exception: {ex.Message}");
                return false;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static void WL(byte[] b, int o, uint v)
        {
            b[o]=(byte)v; b[o+1]=(byte)(v>>8); b[o+2]=(byte)(v>>16); b[o+3]=(byte)(v>>24);
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
