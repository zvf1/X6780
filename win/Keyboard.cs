using System;
using System.Management;

namespace LzHwCtrl
{
    /// <summary>
    /// Controls the white keyboard backlight via the CLEVO_GET WMI class,
    /// which is registered by SvThANSP.sys when it is loaded.
    ///
    /// Uses SetWhiteLedKB(Data: UInt16) and GetWhiteLedKB() directly --
    /// confirmed working on P65/P67RGRERA via WMI introspection.
    ///
    /// If SvThANSP.sys is not loaded (CLEVO_GET class absent), all calls
    /// silently return false and the keyboard row shows a status message.
    /// </summary>
    internal static class Keyboard
    {
        private const string WmiNamespace = "root\\wmi";
        private const string WmiClass     = "CLEVO_GET";

        // Cached WMI object -- fetched once, reused on each call.
        private static ManagementObject? _wmiObj;
        private static bool _checked;

        private static ManagementObject? GetWmiObject()
        {
            if (_checked) return _wmiObj;
            _checked = true;
            try
            {
                var searcher = new ManagementObjectSearcher(
                    WmiNamespace,
                    $"SELECT * FROM {WmiClass}");
                foreach (ManagementObject obj in searcher.Get())
                {
                    _wmiObj = obj;
                    break;
                }
            }
            catch { /* CLEVO_GET not registered; SvThANSP.sys not loaded */ }
            return _wmiObj;
        }

        public static bool IsDriverPresent => GetWmiObject() != null;

        /// <summary>Set brightness level 0 (off) through 5 (max).</summary>
        public static bool SetLevel(int level)
        {
            level = Math.Max(0, Math.Min(5, level));
            var obj = GetWmiObject();
            if (obj == null) return false;
            try
            {
                var inParams = obj.GetMethodParameters("SetWhiteLedKB");
                inParams["Data"] = (ushort)level;
                obj.InvokeMethod("SetWhiteLedKB", inParams, null);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Read the current brightness level (0-5).</summary>
        public static bool TryGetLevel(out int level)
        {
            level = 0;
            var obj = GetWmiObject();
            if (obj == null) return false;
            try
            {
                var result = obj.InvokeMethod("GetWhiteLedKB", null, null);
                // GetWhiteLedKB returns a ManagementBaseObject; value is in its first property.
                if (result != null)
                {
                    foreach (PropertyData prop in ((ManagementBaseObject)result).Properties)
                    {
                        level = Convert.ToInt32(prop.Value);
                        break;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Force a re-check of the WMI class (e.g. after the driver is loaded
        /// at runtime). Call this if IsDriverPresent was false on startup but
        /// SvThANSP.sys has since been started.
        /// </summary>
        public static void ResetCache()
        {
            _wmiObj?.Dispose();
            _wmiObj  = null;
            _checked = false;
        }
    }
}
