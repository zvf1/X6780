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
    /// Run discover-clevo-wmi.ps1 first to see the discovered class/method
    /// names on your actual hardware and sanity-check this against it.
    /// </summary>
    internal static class Keyboard
    {
        // Stored both with and without braces because WMI returns the GUID
        // qualifier in either form depending on the BIOS/Windows version.
        private const string ClevoWmiMethodGuidNoBraces = "ABBC0F6D-8EA1-11D1-00A0-C90629100000";
        private const string ClevoWmiMethodGuidBraces   = "{ABBC0F6D-8EA1-11D1-00A0-C90629100000}";

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
        /// Returns true if <paramref name="raw"/> matches the Clevo WMI method
        /// GUID, regardless of whether WMI returned it with or without braces
        /// and regardless of case.
        /// </summary>
        private static bool IsClevoGuid(string raw)
        {
            if (raw == null) return false;
            var s = raw.Trim();
            return string.Equals(s, ClevoWmiMethodGuidNoBraces, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, ClevoWmiMethodGuidBraces,   StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Finds the WMI class Windows generated for CLEVO_WMI_METHOD_GUID,
        /// then finds the method whose WmiMethodId qualifier matches `cmd`
        /// (the ACPI/WMI mapper preserves the firmware's method IDs exactly,
        /// so this is the same 0x27/0x3D values clevo_wmi.c uses -- not a
        /// re-numbering), and invokes it.
        ///
        /// FIX: The original code compared the GUID string including literal
        /// curly braces against the WMI qualifier value, which on many boards
        /// is returned WITHOUT braces -- causing every keyboard button click
        /// to silently do nothing. IsClevoGuid() now accepts both forms.
        ///
        /// FIX: WmiMethodId is stored as an integer qualifier by wmiacpi.sys,
        /// not a string, so byte.TryParse() always failed. Convert.ToByte()
        /// handles both int and string qualifier values.
        /// </summary>
        private static bool InvokeClevoMethod(byte cmd, uint arg, out uint result)
        {
            result = 0;
            try
            {
                var scope = new ManagementScope(@"root\WMI");
                scope.Connect();

                var classQuery = new ManagementClass(scope, new ManagementPath("meta_class"), null);
                ManagementClass targetClass = null;

                foreach (ManagementBaseObject c in classQuery.GetSubclasses())
                {
                    using var mc = (ManagementClass)c;
                    var guidVal = TryGetQualifierValue(mc.Qualifiers, "guid");
                    if (IsClevoGuid(guidVal?.ToString()))
                    {
                        // Don't dispose mc yet -- we need it as targetClass.
                        // Re-open a fresh instance so the using block above
                        // doesn't close it out from under us.
                        targetClass = new ManagementClass(scope,
                            new ManagementPath(mc.ClassPath.ClassName), null);
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

                using (targetClass)
                {
                    string targetMethodName = null;
                    foreach (MethodData method in targetClass.Methods)
                    {
                        // WmiMethodId is registered as an integer qualifier by
                        // wmiacpi.sys. Convert.ToByte handles int, uint, string, etc.
                        var idVal = TryGetQualifierValue(method.Qualifiers, "WmiMethodId");
                        if (idVal == null) continue;
                        try
                        {
                            byte methodId = Convert.ToByte(idVal);
                            if (methodId == cmd)
                            {
                                targetMethodName = method.Name;
                                break;
                            }
                        }
                        catch { /* qualifier exists but isn't a numeric type -- skip */ }
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
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[kb] WMI call failed: {ex.Message}");
                return false;
            }
        }

        private static object TryGetQualifierValue(QualifierDataCollection qualifiers, string name)
        {
            try { return qualifiers[name]?.Value; }
            catch { return null; }
        }
    }
}
