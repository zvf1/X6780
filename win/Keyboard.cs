using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LzHwCtrl
{
    /// <summary>
    /// Controls the white-only keyboard backlight on this Clevo board.
    ///
    /// BACKGROUND
    /// ----------
    /// On Linux, tuxedo-drivers' clevo_wmi.c exposes the backlight as
    /// /sys/class/leds/white:kbd_backlight. It works by calling Linux's
    /// wmi_evaluate_method() against GUID ABBC0F6D-8EA1-11D1-00A0-C90629100000
    /// (CLEVO_WMI_METHOD_GUID) with method_id = 0x27 (set brightness).
    ///
    /// On Windows, wmiacpi.sys only generates a WMI class for a GUID if the
    /// BIOS _WDG table includes a "method" entry for it. This board's BIOS
    /// does NOT do that — so the GUID never appears in root\WMI and the
    /// WMI approach is a dead end.
    ///
    /// HOW THIS WORKS
    /// --------------
    /// wmi_evaluate_method() in the Linux kernel is ultimately just a thin
    /// wrapper around an ACPI control method call. The Windows ACPI driver
    /// (acpi.sys) exposes the same ACPI namespace and accepts method
    /// evaluation requests via DeviceIoControl with IOCTL_ACPI_EVAL_METHOD.
    ///
    /// The call chain mirrors what clevo_wmi.c does:
    ///   clevo_wmi_evaluate(0x27, brightness)
    ///   -> evaluate ACPI method \WMAB (or \WMAD / similar) with:
    ///        MethodId = 0x27
    ///        Arg0     = brightness (0–5)
    ///
    /// The WMI-to-ACPI bridge on most Clevo boards maps to a method named
    /// WMAx in the ACPI namespace. The conventional names used by wmiacpi.sys
    /// are WMAB (for the method GUID ABBC0F6D) and WMAD. We try both.
    ///
    /// If neither works on your board, run:
    ///   powershell -c "& { [System.IO.File]::WriteAllBytes('dsdt.bin',
    ///     (Get-WmiObject -Namespace root\wmi -Class MSAcpi_RawSMBiosTables
    ///     | Select -First 1).SMBiosData) }" 
    /// (or use RWEverything / acpidump) and decompile the DSDT to find the
    /// real method name for GUID ABBC0F6D.
    /// </summary>
    internal static class Keyboard
    {
        // ACPI IOCTL to evaluate a method by path under a device
        // (from ntifs.h / acpiioct.h)
        private const uint IOCTL_ACPI_EVAL_METHOD        = 0x00224010;
        private const uint IOCTL_ACPI_EVAL_METHOD_EX     = 0x00224018;

        // ACPI_EVAL_INPUT_BUFFER.Signature values
        private const uint ACPI_EVAL_INPUT_BUFFER_SIGNATURE       = 0x44494e49; // 'INDI'
        private const uint ACPI_EVAL_INPUT_BUFFER_SIMPLE_INTEGER_SIGNATURE = 0x01aa0001;

        // Clevo WMI method command IDs (from clevo_wmi.c)
        private const uint CmdSetKbWhiteLeds = 0x27;
        private const uint CmdGetKbWhiteLeds = 0x3D;

        // ACPI method names the WMI mapper uses for GUID ABBC0F6D.
        // Try WMAB first (most Clevos), then WMAD as a fallback.
        private static readonly string[] AcpiMethodNames = { "WMAB", "WMAD", "WMA0" };

        // Path to the ACPI WMI device that owns the Clevo namespace.
        // On most systems this is the first ACPI device under PNP0C14.
        private const string AcpiDevicePath = @"\\.\ACPI#PNP0C14#0#{ad2e0f5e-7b0d-4f78-a7ef-2f2afc5acfb2}";

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
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
            IntPtr  hDevice,
            uint    dwIoControlCode,
            byte[]  lpInBuffer,
            uint    nInBufferSize,
            byte[]  lpOutBuffer,
            uint    nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr  lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        private const uint GENERIC_READ_WRITE  = 0xC0000000;
        private const uint FILE_SHARE_READ_WRITE = 0x00000003;
        private const uint OPEN_EXISTING      = 3;

        public static bool SetLevel(int level)
        {
            level = Math.Max(0, Math.Min(5, level));
            return InvokeAcpiMethod(CmdSetKbWhiteLeds, (uint)level, out _);
        }

        public static bool TryGetLevel(out int level)
        {
            level = 0;
            if (!InvokeAcpiMethod(CmdGetKbWhiteLeds, 0, out uint result))
                return false;
            level = (int)(result & 0xFF);
            return true;
        }

        /// <summary>
        /// Calls the ACPI WMI method by opening the PNP0C14 device and
        /// sending IOCTL_ACPI_EVAL_METHOD_EX with the method path and args.
        ///
        /// The input buffer layout is:
        ///   ACPI_EVAL_INPUT_BUFFER_EX:
        ///     ULONG  Signature   = ACPI_EVAL_INPUT_BUFFER_SIGNATURE_EX
        ///     CHAR   MethodName[256]   (null-terminated, relative path)
        ///     ULONG  ArgumentCount = 2
        ///     ACPI_METHOD_ARGUMENT[2]:
        ///       [0] Type=INTEGER, DataLength=4, Data=MethodId (0x27 etc.)
        ///       [1] Type=INTEGER, DataLength=4, Data=arg
        ///
        /// The method path under the WMI device is "\WMAB" etc.
        /// </summary>
        private static bool InvokeAcpiMethod(uint methodId, uint arg, out uint result)
        {
            result = 0;

            // Try the WMI device path for PNP0C14 instance 0, then instance
            // CLVD (some Clevo BIOSes use that identifier).
            string[] devicePaths =
            {
                @"\\.\ACPI#PNP0C14#0#{ad2e0f5e-7b0d-4f78-a7ef-2f2afc5acfb2}",
                @"\\.\ACPI#PNP0C14#clvd#{ad2e0f5e-7b0d-4f78-a7ef-2f2afc5acfb2}",
                @"\\.\ACPI#PNP0C14#wmi0#{ad2e0f5e-7b0d-4f78-a7ef-2f2afc5acfb2}",
            };

            foreach (string devPath in devicePaths)
            {
                IntPtr hDev = CreateFile(devPath, GENERIC_READ_WRITE,
                    FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                if (hDev == INVALID_HANDLE_VALUE)
                    continue;

                try
                {
                    foreach (string methodName in AcpiMethodNames)
                    {
                        if (TryEvalMethod(hDev, methodName, methodId, arg, out result))
                            return true;
                    }
                }
                finally
                {
                    CloseHandle(hDev);
                }
            }

            // Last resort: try the direct ACPI namespace path via the ACPI
            // driver itself (requires no device enumeration).
            return TryDirectAcpiPath(methodId, arg, out result);
        }

        private static bool TryEvalMethod(IntPtr hDev, string methodName,
            uint methodId, uint arg, out uint result)
        {
            result = 0;
            try
            {
                // ACPI_EVAL_INPUT_BUFFER_EX layout (packed):
                //   [0..3]    Signature (ULONG) = 0x45584950 ("PIXE")
                //   [4..259]  MethodName (CHAR[256])
                //   [260..263] ArgumentCount (ULONG) = 2
                //   [264..275] Arg0: Type(2), DataLen(2), Data(4), padding(4)
                //   [276..287] Arg1: Type(2), DataLen(2), Data(4), padding(4)
                const uint SigEx = 0x45584950; // ACPI_EVAL_INPUT_BUFFER_SIGNATURE_EX
                const ushort ArgTypeInteger = 0;

                byte[] inBuf = new byte[288];
                WriteUInt32(inBuf, 0, SigEx);
                WriteAsciiZ(inBuf, 4, methodName, 256);
                WriteUInt32(inBuf, 260, 2); // ArgumentCount

                // Arg0 = MethodId
                WriteUInt16(inBuf, 264, ArgTypeInteger);
                WriteUInt16(inBuf, 266, 4);
                WriteUInt32(inBuf, 268, methodId);

                // Arg1 = brightness/arg
                WriteUInt16(inBuf, 276, ArgTypeInteger);
                WriteUInt16(inBuf, 278, 4);
                WriteUInt32(inBuf, 280, arg);

                byte[] outBuf = new byte[256];
                bool ok = DeviceIoControl(hDev, IOCTL_ACPI_EVAL_METHOD_EX,
                    inBuf, (uint)inBuf.Length,
                    outBuf, (uint)outBuf.Length,
                    out uint bytesReturned, IntPtr.Zero);

                if (ok && bytesReturned >= 8)
                {
                    // ACPI_EVAL_OUTPUT_BUFFER: Signature(4), Length(4), Count(4), Argument[0]
                    result = ReadUInt32(outBuf, 16); // first return argument data
                }
                return ok;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[kb] TryEvalMethod({methodName}): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Fallback: open acpi.sys directly and call IOCTL_ACPI_EVAL_METHOD
        /// (non-EX) using just the short method name. This works when the
        /// device is accessible as \\.\ACPI but not via the PNP0C14 path.
        /// </summary>
        private static bool TryDirectAcpiPath(uint methodId, uint arg, out uint result)
        {
            result = 0;
            string[] directPaths =
            {
                @"\\.\ACPI",
                @"\\.\acpi",
            };

            foreach (string path in directPaths)
            {
                IntPtr hDev = CreateFile(path, GENERIC_READ_WRITE,
                    FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (hDev == INVALID_HANDLE_VALUE)
                    continue;
                try
                {
                    foreach (string methodName in AcpiMethodNames)
                    {
                        if (TryEvalMethod(hDev, methodName, methodId, arg, out result))
                            return true;
                    }
                }
                finally { CloseHandle(hDev); }
            }

            Console.Error.WriteLine(
                "[kb] Could not find the ACPI WMI device (PNP0C14) or invoke " +
                "any known method name (WMAB/WMAD/WMA0). " +
                "Run 'devmgmt.msc', expand 'System devices', and look for " +
                "'Microsoft Windows Management Interface for ACPI'. " +
                "If missing, this board may need a BIOS update or the Clevo " +
                "Windows keyboard driver package.");
            return false;
        }

        private static void WriteUInt32(byte[] buf, int offset, uint value)
        {
            buf[offset]     = (byte)(value);
            buf[offset + 1] = (byte)(value >> 8);
            buf[offset + 2] = (byte)(value >> 16);
            buf[offset + 3] = (byte)(value >> 24);
        }
        private static void WriteUInt16(byte[] buf, int offset, ushort value)
        {
            buf[offset]     = (byte)(value);
            buf[offset + 1] = (byte)(value >> 8);
        }
        private static uint ReadUInt32(byte[] buf, int offset) =>
            (uint)(buf[offset] | (buf[offset+1] << 8) | (buf[offset+2] << 16) | (buf[offset+3] << 24));

        private static void WriteAsciiZ(byte[] buf, int offset, string s, int maxLen)
        {
            int i = 0;
            foreach (char c in s)
            {
                if (i >= maxLen - 1) break;
                buf[offset + i++] = (byte)c;
            }
            buf[offset + i] = 0;
        }
    }
}
