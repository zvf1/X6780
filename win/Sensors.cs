using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace LzHwCtrl
{
    /// <summary>
    /// Replaces find_coretemp_package_input()/find_nouveau_temp_input() +
    /// get_temps() from lzhwctrl.py. LibreHardwareMonitorLib enumerates
    /// hardware once; we re-read sensor values on each poll.
    /// </summary>
    internal sealed class Sensors
    {
        private readonly Computer _computer;

        public Sensors()
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
            };
            _computer.Open();
        }

        public (float? cpuTemp, float? gpuTemp) GetTemps()
        {
            float? cpu = null, gpu = null;

            foreach (var hw in _computer.Hardware)
            {
                hw.Update();

                if (hw.HardwareType == HardwareType.Cpu)
                {
                    // Prefer a "Package" temp sensor (matches coretemp's
                    // "Package id 0" label that the Linux version looked for).
                    var pkg = hw.Sensors.FirstOrDefault(s =>
                        s.SensorType == SensorType.Temperature &&
                        s.Name.Contains("Package"));
                    var any = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
                    cpu = (pkg ?? any)?.Value;
                }
                else if (hw.HardwareType == HardwareType.GpuNvidia ||
                         hw.HardwareType == HardwareType.GpuAmd ||
                         hw.HardwareType == HardwareType.GpuIntel)
                {
                    var core = hw.Sensors.FirstOrDefault(s =>
                        s.SensorType == SensorType.Temperature &&
                        s.Name.Contains("Core"));
                    var any = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
                    gpu = (core ?? any)?.Value;
                }
            }

            return (cpu, gpu);
        }

        public void Close() => _computer.Close();
    }
}
