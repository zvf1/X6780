using System;
using System.Management;

namespace LzHwCtrl
{
    /// <summary>
    /// Replaces set_kb_level() from lzhwctrl.py.
    ///
    /// Good news from reading tuxedo-drivers' clevo_leds.h / clevo_wmi.c:
    /// the white-only keyboard backlight on this board is NOT an EC port
    /// write like the fans are. It's an ACPI WMI control-method call:
    ///
    ///   clevo_wmi_evaluate(CLEVO_CMD_SET_KB_WHITE_LEDS /* 0x27 */, brightness)
    ///   -> wmi_evaluate_method(CLEVO_WMI_METHOD_GUID, 0, 0x27, &arg, &out)
    ///
    /// where CLEVO_WMI_METHOD_GUID = ABBC0F6D-8EA1-11D1-00A0-C90629100000,
    /// a long-standing Clevo/Wistron WMI GUID used across many of their
    /// laptops. Windows' own ACPI/WMI mapper (wmiacpi.sys) reads the exact
    /// same firmware _WDG table and generates a WMI class for this GUID
    /// under root\WMI -- this is the same mechanism, not an emulation of
    /// it, so no kernel driver or admin rights are needed for this part.
    ///
    /// The one thing that's genuinely BIOS/vendor-specific (and not in the
    /// driver source, hence not guessable) is what Windows *names* the
    /// generated class and its methods. So instead of hardcoding a class
    /// name I might get wrong, this finds it at runtime by matching the
    /// "guid" qualifier -- exactly like clevo_wmi.c's own probe routine
    /// matches wmi_has_guid(CLEVO_WMI_METHOD_GUID) before trusting it.
    ///
    /// Run discover-clevo-wmi.ps1 first to see the discovered class/method
    /// names on your actual hardware and sanity-check this against it.
    /// </summary>
    internal static class Keyboard
    {
        private const string ClevoWmiMethodGuid = "{ABBC0F6D-8EA1-11D1-00A0-C90629100000}";
        private const byte CmdSetKbWhiteLeds = 0x27;
        private const byte CmdGetKbWhiteLeds = 0x3D;

        // Same 0-5 UI scale as the Linux script's button row, scaled down to
        // the white-only keyboard's actual range. clevo_leds.h shows white-only
        // boards are either 3-step (CLEVO_KBD_BRIGHTNESS_WHITE_MAX = 0x02:
        // off/half/full) or 6-step on older "<=7th gen" boards
        // (CLEVO_KBD_BRIGHTNESS_WHITE_MAX_5 = 0x05). The i7-6700HQ in this
        // laptop is 6th gen, so it's almost certainly the 6-step variant --
        // which conveniently maps 1:1 onto the existing 0-5 buttons with no
        // scaling needed. If brightness looks wrong (e.g. only 3 useful
        // steps), this board is the 3-step variant -- divide level by 2.5ish.
        public static bool SetLevel(int level)
        {
            level = Math.Max(0, Math.Min(5, level));
            return InvokeClevoMethod(CmdSetKbWhiteLeds, (uint)level, out _);
        }

        public static bool TryGetLevel(out int level)
        {
            level = 0;
            if (!InvokeClevoMethod(CmdGetKbWhiteLeds, 0, out uint result))
                return false;
            level = (int)result;
            return true;
        }

        /// <summary>
        /// Finds the WMI class Windows generated for CLEVO_WMI_METHOD_GUID,
        /// then finds the method whose WmiMethodId qualifier matches `cmd`
        /// (the ACPI/WMI mapper preserves the firmware's method IDs exactly,
        /// so this is the same 0x27/0x3D values clevo_wmi.c uses -- not a
        /// re-numbering), and invokes it.
        /// </summary>
        private static bool InvokeClevoMethod(byte cmd, uint arg, out uint result)
        {
            result = 0;
            try
            {
                using var scope = new ManagementScope(@"root\WMI");
                scope.Connect();

                var classQuery = new ManagementClass(scope, new ManagementPath("meta_class"), null);
                ManagementClass targetClass = null;

                foreach (ManagementBaseObject c in classQuery.GetSubclasses())
                {
                    using var mc = (ManagementClass)c;
                    var guidQualifier = TryGetQualifier(mc.Qualifiers, "guid");
                    if (string.Equals(guidQualifier, ClevoWmiMethodGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        targetClass = mc;
                        break;
                    }
                }

                if (targetClass == null)
                {
                    Console.Error.WriteLine(
                        "[kb] No WMI class found for Clevo method GUID. " +
                        "Run discover-clevo-wmi.ps1 to check whether this " +
                        "board exposes it under a different GUID.");
                    return false;
                }

                string targetMethodName = null;
                foreach (MethodData method in targetClass.Methods)
                {
                    var idQualifier = TryGetQualifier(method.Qualifiers, "WmiMethodId");
                    if (idQualifier != null && byte.TryParse(idQualifier, out byte methodId) && methodId == cmd)
                    {
                        targetMethodName = method.Name;
                        break;
                    }
                }

                if (targetMethodName == null)
                {
                    Console.Error.WriteLine(
                        $"[kb] Found the Clevo WMI class ({targetClass.ClassPath.ClassName}) " +
                        $"but no method with WmiMethodId 0x{cmd:X2}. Run discover-clevo-wmi.ps1 " +
                        "to see what methods actually exist on this board.");
                    return false;
                }

                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT * FROM {targetClass.ClassPath.ClassName}"));
                using var instanceEnum = searcher.Get().GetEnumerator();
                if (!instanceEnum.MoveNext())
                {
                    Console.Error.WriteLine("[kb] Clevo WMI class has no instances.");
                    return false;
                }

                using var instance = (ManagementObject)instanceEnum.Current;
                var inParams = targetClass.GetMethodParameters(targetMethodName);

                // Most BIOS-generated classes following the Microsoft sample
                // pattern name the single in-param "Data" (uint32). If this
                // board's BIOS named it differently, fall back to whatever
                // the single declared in-param actually is.
                string inParamName = "Data";
                foreach (PropertyData p in inParams.Properties)
                {
                    inParamName = p.Name;
                    break; // there's normally exactly one
                }
                inParams[inParamName] = arg;

                var outParams = instance.InvokeMethod(targetMethodName, inParams, null);

                if (outParams != null)
                {
                    foreach (PropertyData p in outParams.Properties)
                    {
                        if (p.Value != null)
                        {
                            result = Convert.ToUInt32(p.Value);
                            break;
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[kb] WMI call failed: {ex.Message}");
                return false;
            }
        }

        private static string TryGetQualifier(QualifierDataCollection qualifiers, string name)
        {
            try { return qualifiers[name]?.Value?.ToString(); }
            catch { return null; }
        }
    }
}
