using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LzHwCtrl
{
    /// <summary>
    /// Controls the white keyboard backlight via the ACPI WMI bridge (PNP0C14).
    ///
    /// The BIOS on this board does NOT register GUID ABBC0F6D in the WMI
    /// class layer (so root\WMI never gets a class for it), but the underlying
    /// ACPI method WMAB does exist in the DSDT and can be called directly
    /// via SetupDi + IOCTL_ACPI_EVAL_METHOD_EX on the PNP0C14\0 device.
    /// </summary>
    internal static class Keyboard
    {
        // ── P/Invoke ────────────────────────────────────────────────────────

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
            ref Guid InterfaceClassGuid, uint MemberIndex,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
            IntPtr DeviceInterfaceDetailData,
            uint DeviceInterfaceDetailDataSize,
            out uint RequiredSize,
            IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

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

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        // ── Constants ───────────────────────────────────────────────────────

        // Device interface GUID for ACPI WMI devices (PNP0C14)
        // {AD2E0F5E-7B0D-4F78-A7EF-2F2AFC5ACfb2}  (uppercase F intentional — same GUID)
        private static readonly Guid ACPI_WMI_DEVICE_GUID =
            new Guid("AD2E0F5E-7B0D-4F78-A7EF-2F2AFC5ACfb2");

        private const uint DIGCF_PRESENT           = 0x00000002;
        private const uint DIGCF_DEVICEINTERFACE   = 0x00000010;
        private const uint GENERIC_READ_WRITE       = 0xC0000000;
        private const uint FILE_SHARE_READ_WRITE    = 0x00000003;
        private const uint OPEN_EXISTING            = 3;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // IOCTL_ACPI_EVAL_METHOD_EX  (from acpiioct.h)
        private const uint IOCTL_ACPI_EVAL_METHOD_EX = 0x00224018;

        // ACPI_EVAL_INPUT_BUFFER_COMPLEX_EX.Signature
        private const uint ACPI_EVAL_INPUT_BUFFER_SIGNATURE_EX = 0x45584950; // 'PIXE'

        // ACPI_METHOD_ARGUMENT.Type
        private const ushort ACPI_METHOD_ARGUMENT_INTEGER = 0x0;

        // Clevo WMI method IDs (clevo_wmi.c)
        private const uint CmdSetKbWhiteLeds = 0x27;
        private const uint CmdGetKbWhiteLeds = 0x3D;

        // ACPI method names to try (WMI bridge puts methods here under PNP0C14)
        private static readonly string[] MethodNames = { "WMAB", "WMAD", "WMA0", "WMBC" };

        // Log file next to the exe for diagnostics
        private static readonly string LogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "keyboard_debug.log");

        // ── Public API ──────────────────────────────────────────────────────

        public static bool SetLevel(int level)
        {
            level = Math.Max(0, Math.Min(5, level));
            Log($"SetLevel({level}) -> cmd=0x{CmdSetKbWhiteLeds:X2} arg={level}");
            bool ok = Invoke(CmdSetKbWhiteLeds, (uint)level, out uint result);
            Log($"SetLevel result: ok={ok} returnVal=0x{result:X}");
            return ok;
        }

        public static bool TryGetLevel(out int level)
        {
            level = 0;
            if (!Invoke(CmdGetKbWhiteLeds, 0, out uint r)) return false;
            level = (int)(r & 0xFF);
            return true;
        }

        // ── Core ────────────────────────────────────────────────────────────

        private static bool Invoke(uint methodId, uint arg, out uint result)
        {
            result = 0;

            // Enumerate all PNP0C14 device interfaces via SetupDi
            var guid = ACPI_WMI_DEVICE_GUID;
            IntPtr devInfo = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

            if (devInfo == INVALID_HANDLE_VALUE)
            {
                Log($"SetupDiGetClassDevs failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            try
            {
                uint idx = 0;
                while (true)
                {
                    var ifData = new SP_DEVICE_INTERFACE_DATA();
                    ifData.cbSize = (uint)Marshal.SizeOf(ifData);

                    if (!SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero,
                            ref guid, idx++, ref ifData))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == 259 /* ERROR_NO_MORE_ITEMS */) break;
                        Log($"SetupDiEnumDeviceInterfaces error: {err}");
                        break;
                    }

                    // Get the device path
                    SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData,
                        IntPtr.Zero, 0, out uint needed, IntPtr.Zero);

                    IntPtr detailBuf = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA:
                        // 4+1 on 32-bit, 4+4 (aligned) on 64-bit -> use 8 for x64
                        Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);

                        if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData,
                                detailBuf, needed, out _, IntPtr.Zero))
                        {
                            Log($"SetupDiGetDeviceInterfaceDetail failed: {Marshal.GetLastWin32Error()}");
                            continue;
                        }

                        // Device path starts at offset 4
                        string devPath = Marshal.PtrToStringAuto(detailBuf + 4);
                        Log($"Trying device: {devPath}");

                        IntPtr hDev = CreateFile(devPath, GENERIC_READ_WRITE,
                            FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                        if (hDev == INVALID_HANDLE_VALUE)
                        {
                            Log($"  CreateFile failed: {Marshal.GetLastWin32Error()}");
                            continue;
                        }

                        try
                        {
                            foreach (string name in MethodNames)
                            {
                                Log($"  Trying method: {name}");
                                if (TryEval(hDev, name, methodId, arg, out result))
                                {
                                    Log($"  SUCCESS with method {name}");
                                    return true;
                                }
                            }
                        }
                        finally { CloseHandle(hDev); }
                    }
                    finally { Marshal.FreeHGlobal(detailBuf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(devInfo); }

            Log("No device/method combination worked.");
            return false;
        }

        private static bool TryEval(IntPtr hDev, string methodName,
            uint methodId, uint arg, out uint result)
        {
            result = 0;
            try
            {
                // ACPI_EVAL_INPUT_BUFFER_EX (manually packed, little-endian):
                //   ULONG  Signature      [0..3]
                //   CHAR   MethodName[256][4..259]
                //   ULONG  ArgumentCount  [260..263]  = 2
                //   -- ACPI_METHOD_ARGUMENT[0] (Arg0 = MethodId) --
                //   USHORT Type           [264..265]
                //   USHORT DataLength     [266..267]  = 4
                //   ULONG  Data           [268..271]
                //   -- ACPI_METHOD_ARGUMENT[1] (Arg1 = value) --
                //   USHORT Type           [272..273]
                //   USHORT DataLength     [274..275]  = 4
                //   ULONG  Data           [276..279]
                byte[] inBuf = new byte[280];
                WL(inBuf, 0,   ACPI_EVAL_INPUT_BUFFER_SIGNATURE_EX);
                WriteAsciiZ(inBuf, 4, methodName);
                WL(inBuf, 260, 2);                              // ArgumentCount
                WS(inBuf, 264, ACPI_METHOD_ARGUMENT_INTEGER);  // Arg0.Type
                WS(inBuf, 266, 4);                             // Arg0.DataLength
                WL(inBuf, 268, methodId);                      // Arg0.Data = MethodId
                WS(inBuf, 272, ACPI_METHOD_ARGUMENT_INTEGER);  // Arg1.Type
                WS(inBuf, 274, 4);                             // Arg1.DataLength
                WL(inBuf, 276, arg);                           // Arg1.Data = brightness

                byte[] outBuf = new byte[256];
                bool ok = DeviceIoControl(hDev, IOCTL_ACPI_EVAL_METHOD_EX,
                    inBuf, (uint)inBuf.Length,
                    outBuf, (uint)outBuf.Length,
                    out uint returned, IntPtr.Zero);

                if (!ok)
                {
                    Log($"    DeviceIoControl failed: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                // ACPI_EVAL_OUTPUT_BUFFER:
                //   ULONG Signature  [0..3]
                //   ULONG Length     [4..7]
                //   ULONG Count      [8..11]
                //   ACPI_METHOD_ARGUMENT[0]:
                //     USHORT Type    [12..13]
                //     USHORT DataLen [14..15]
                //     ULONG  Data    [16..19]
                if (returned >= 20)
                    result = RL(outBuf, 16);

                return true;
            }
            catch (Exception ex)
            {
                Log($"    Exception in TryEval: {ex.Message}");
                return false;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static void WL(byte[] b, int o, uint v)
        {
            b[o]=( byte)v; b[o+1]=(byte)(v>>8); b[o+2]=(byte)(v>>16); b[o+3]=(byte)(v>>24);
        }
        private static void WS(byte[] b, int o, ushort v)
        {
            b[o]=(byte)v; b[o+1]=(byte)(v>>8);
        }
        private static uint RL(byte[] b, int o) =>
            (uint)(b[o]|(b[o+1]<<8)|(b[o+2]<<16)|(b[o+3]<<24));

        private static void WriteAsciiZ(byte[] buf, int offset, string s)
        {
            int i = 0;
            foreach (char c in s) { if (i >= 255) break; buf[offset + i++] = (byte)c; }
            buf[offset + i] = 0;
        }

        private static void Log(string msg)
        {
            try
            {
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
                Console.Error.WriteLine(line);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
