namespace LzHwCtrl
{
    /// <summary>
    /// Keyboard backlight control is not available on Windows.
    ///
    /// On Linux, tuxedo-drivers' clevo_acpi kernel module calls acpi_evaluate_dsm()
    /// on the CLV0001 ACPI device (UUID 93f224e4-fbdc-4bbf-add6-db71bdc0afad)
    /// to set brightness. This requires a kernel driver — there is no user-mode
    /// API on Windows that can reach this ACPI method without one.
    ///
    /// The TUXEDO Control Center for Windows installs tuxedo_io.sys which provides
    /// this capability, but redistributing or depending on that driver is out of
    /// scope for this tool.
    /// </summary>
    internal static class Keyboard
    {
        public static bool IsSupported => false;

        public static bool SetLevel(int level) => false;

        public static bool TryGetLevel(out int level) { level = 0; return false; }
    }
}
