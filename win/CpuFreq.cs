using System.Diagnostics;

namespace LzHwCtrl
{
    /// <summary>
    /// Replaces the "cpufreq" branch of ec_helper_main() in lzhwctrl.py,
    /// which called `cpupower frequency-set -u`. Windows has no direct
    /// kHz cap; the nearest equivalent is the PROCTHROTTLEMAX power
    /// setting (max processor state, as a percentage of nominal max),
    /// set via powercfg and applied to both AC and DC power plans so it
    /// behaves the same regardless of whether the laptop is plugged in.
    ///
    /// SUB_PROCESSOR / PROCTHROTTLEMAX are the well-known GUIDs for the
    /// "Processor power management -> Maximum processor state" setting.
    /// </summary>
    internal static class CpuFreq
    {
        private const string SUB_PROCESSOR = "54533251-82be-4824-96c1-47b60b740d00";
        private const string PROCTHROTTLEMAX = "bc5038f7-23e0-4960-96da-33abaf5935ec";

        /// <summary>
        /// percent: 5-100 (% of nominal max frequency), or null for AUTO (100%).
        /// CPU_FREQ_BUTTONS in lzhwctrl.py used kHz caps against a ~3.5GHz
        /// turbo max; pick whatever mapping suits your chip when wiring up
        /// the UI buttons (e.g. 1.8GHz cap ~= 51% on a 3.5GHz max turbo).
        /// </summary>
        public static void SetMaxPercent(int? percent)
        {
            int pct = percent ?? 100;

            RunPowercfg($"/setacvalueindex SCHEME_CURRENT {SUB_PROCESSOR} {PROCTHROTTLEMAX} {pct}");
            RunPowercfg($"/setdcvalueindex SCHEME_CURRENT {SUB_PROCESSOR} {PROCTHROTTLEMAX} {pct}");
            RunPowercfg("/setactive SCHEME_CURRENT");
        }

        private static void RunPowercfg(string args)
        {
            var psi = new ProcessStartInfo("powercfg.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p.WaitForExit();
        }
    }
}
