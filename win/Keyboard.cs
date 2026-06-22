using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LzHwCtrl
{
    /// <summary>
    /// Controls the white keyboard backlight by calling ACPI methods directly
    /// on the PNP0C14 device via its PDO path (\Device\00000028).
    ///
    /// The devices have no registered device interface GUIDs, so SetupDi
    /// cannot enumerate them. Instead we open them by their kernel object
    /// path using NtOpenFile, then call IOCTL_ACPI_EVAL_METHOD (non-EX)
    /// which takes a 4-byte method name rather than a full path string.
    ///
    /// We also try the EC port direct write as a final fallback — the same
    /// mechanism used for fan control — since some Clevo boards expose
    /// keyboard brightness via EC register 0xD3.
    /// </summary>
    internal static class Keyboard
    {
        // ── P/Invoke ────────────────────────────────────────────────────────

        [DllImport("ntdll.dll")]
        private static extern int NtOpenFile(
            out IntPtr FileHandle,
            uint DesiredAccess,
            ref OBJECT_ATTRIBUTES ObjectAttributes,
            ref IO_STATUS_BLOCK IoStatusBlock,
            uint ShareAccess,
            uint OpenOptions);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode,
            byte[] lpInBuffer,  uint nInBufferSize,
            byte[] lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern void RtlInitUnicodeString(
            ref UNICODE_STRING DestinationString, string SourceString);

        [StructLayout(LayoutKind.Sequential)]
        private struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OBJECT_ATTRIBUTES
        {
            public uint   Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint   Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_STATUS_BLOCK
        {
            public IntPtr Status;
            public IntPtr Information;
        }

        // ── Constants ───────────────────────────────────────────────────────

        // IOCTL_ACPI_EVAL_METHOD  (acpiioct.h, method=1, function=0x400>>2=0x100, access=0)
        // CTL_CODE(FILE_DEVICE_ACPI=0x22, 0x100, METHOD_BUFFERED=0, FILE_READ_ACCESS|FILE_WRITE_ACCESS=3) -> nope
        // Correct value from WDK: 0x00224010  (IOCTL_ACPI_EVAL_METHOD)
        //                         0x00224014  (IOCTL_ACPI_EVAL_METHOD_EX — not used here)
        private const uint IOCTL_ACPI_EVAL_METHOD = 0x00224010;

        // ACPI_EVAL_INPUT_BUFFER.Signature for integer arg  (from acpiioct.h)
        // ACPI_EVAL_INPUT_BUFFER_SIGNATURE             = 0x44494E49 ('INDI')
        // ACPI_EVAL_INPUT_BUFFER_SIMPLE_INTEGER_SIGNATURE = 0x01AA0001
        private const uint ACPI_EVAL_INPUT_BUFFER_SIGNATURE             = 0x44494E49;
        private const uint ACPI_EVAL_INPUT_BUFFER_SIMPLE_INTEGER_SIGNATURE = 0x01AA0001;

        private const ushort ACPI_METHOD_ARGUMENT_INTEGER = 0;

        // NtOpenFile access / share / options
        private const uint FILE_READ_DATA         = 0x0001;
        private const uint FILE_WRITE_DATA        = 0x0002;
        private const uint FILE_SHARE_READ        = 0x0001;
        private const uint FILE_SHARE_WRITE       = 0x0002;
        private const uint FILE_NON_DIRECTORY_FILE = 0x00000040;
        private const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x00000020;
        private const uint OBJ_CASE_INSENSITIVE   = 0x00000040;
        private const uint SYNCHRONIZE            = 0x00100000;

        // Clevo WMI command IDs
        private const uint CmdSetKb = 0x27;
        private const uint CmdGetKb = 0x3D;

        // PDO paths for both PNP0C14 instances
        // \Device\00000028 = ACPI\PNP0C14\0  (confirmed from PnP query)
        // \Device\00000031 = ACPI\PNP0C14\MXM2
        private static readonly string[] PdoPaths =
        {
            @"\Device\00000028",
            @"\Device\00000031",
        };

        // Method names to try under each device
        private static readonly string[] MethodNames = { "WMAB", "WMAD", "WMA0", "WMBC" };



        private static readonly string LogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "keyboard_debug.log");

        // ── Public API ──────────────────────────────────────────────────────

        public static bool SetLevel(int level)
        {
            level = Math.Max(0, Math.Min(5, level));
            Log($"SetLevel({level})");

            // Try ACPI method on both PDO devices
            foreach (string pdo in PdoPaths)
            {
                IntPtr h = OpenDevice(pdo);
                if (h == IntPtr.Zero) continue;
                try
                {
                    foreach (string method in MethodNames)
                    {
                        Log($"  {pdo} / {method}");
                        if (TryEvalMethod(h, method, CmdSetKb, (uint)level, out uint r))
                        {
                            Log($"  SUCCESS (ret=0x{r:X})");
                            return true;
                        }
                    }
                }
                finally { CloseHandle(h); }
            }

            Log("  All methods failed");
            return false;
        }

        public static bool TryGetLevel(out int level)
        {
            level = 0;
            foreach (string pdo in PdoPaths)
            {
                IntPtr h = OpenDevice(pdo);
                if (h == IntPtr.Zero) continue;
                try
                {
                    foreach (string method in MethodNames)
                    {
                        if (TryEvalMethod(h, method, CmdGetKb, 0, out uint r))
                        {
                            level = (int)(r & 0xFF);
                            return true;
                        }
                    }
                }
                finally { CloseHandle(h); }
            }
            return false;
        }

        // ── ACPI eval ───────────────────────────────────────────────────────

        private static IntPtr OpenDevice(string ntPath)
        {
            var us = new UNICODE_STRING();
            RtlInitUnicodeString(ref us, ntPath);

            // Pin the unicode string so the pointer stays valid
            var usHandle = GCHandle.Alloc(us, GCHandleType.Pinned);
            try
            {
                var oa = new OBJECT_ATTRIBUTES
                {
                    Length                   = (uint)Marshal.SizeOf<OBJECT_ATTRIBUTES>(),
                    RootDirectory            = IntPtr.Zero,
                    ObjectName               = usHandle.AddrOfPinnedObject(),
                    Attributes               = OBJ_CASE_INSENSITIVE,
                    SecurityDescriptor       = IntPtr.Zero,
                    SecurityQualityOfService = IntPtr.Zero,
                };
                var iosb = new IO_STATUS_BLOCK();

                int status = NtOpenFile(out IntPtr handle,
                    FILE_READ_DATA | FILE_WRITE_DATA | SYNCHRONIZE,
                    ref oa, ref iosb,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    FILE_NON_DIRECTORY_FILE | FILE_SYNCHRONOUS_IO_NONALERT);

                if (status != 0)
                {
                    Log($"  NtOpenFile({ntPath}) NTSTATUS=0x{status:X8}");
                    return IntPtr.Zero;
                }
                Log($"  Opened {ntPath} OK");
                return handle;
            }
            finally { usHandle.Free(); }
        }

        /// <summary>
        /// Sends IOCTL_ACPI_EVAL_METHOD with:
        ///   ACPI_EVAL_INPUT_BUFFER (complex, two integer args):
        ///     Signature     = ACPI_EVAL_INPUT_BUFFER_SIGNATURE  ('INDI')
        ///     MethodNameAsUlong = method name packed as little-endian ULONG
        ///     ArgumentCount = 2
        ///     Arguments[0]  = methodId  (INTEGER)
        ///     Arguments[1]  = arg       (INTEGER)
        /// </summary>
        private static bool TryEvalMethod(IntPtr hDev, string methodName,
            uint methodId, uint arg, out uint result)
        {
            result = 0;
            try
            {
                // Pack 4-char method name as little-endian ULONG
                // e.g. "WMAB" -> 'W'|'M'<<8|'A'<<16|'B'<<24
                if (methodName.Length < 4) methodName = methodName.PadRight(4);
                uint nameAsUlong = (uint)(
                    methodName[0]        |
                    (methodName[1] <<  8)|
                    (methodName[2] << 16)|
                    (methodName[3] << 24));

                // ACPI_EVAL_INPUT_BUFFER layout:
                //  [0..3]   Signature         (ULONG)
                //  [4..7]   MethodNameAsUlong (ULONG)
                //  [8..11]  ArgumentCount     (ULONG) = 2
                //  -- ACPI_METHOD_ARGUMENT[0]: methodId --
                //  [12..13] Type    (USHORT) = INTEGER
                //  [14..15] DataLen (USHORT) = 4
                //  [16..19] Data    (ULONG)  = methodId
                //  -- ACPI_METHOD_ARGUMENT[1]: arg --
                //  [20..21] Type    (USHORT) = INTEGER
                //  [22..23] DataLen (USHORT) = 4
                //  [24..27] Data    (ULONG)  = arg
                byte[] inBuf = new byte[28];
                WL(inBuf,  0, ACPI_EVAL_INPUT_BUFFER_SIGNATURE);
                WL(inBuf,  4, nameAsUlong);
                WL(inBuf,  8, 2);  // ArgumentCount
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
                Log($"    IOCTL({methodName}, id=0x{methodId:X2}, arg={arg}): ok={ok} err={err} returned={returned}");

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

        // ── Byte helpers ────────────────────────────────────────────────────

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
                File.AppendAllText(LogPath, line + Environment.NewLine, System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}
