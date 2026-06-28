using System;
using System.Threading;

namespace LzHwCtrl
{
    internal sealed class ControlLoop
    {
        // Fan curve: (temp C, duty byte). Same values as FAN_CURVE in lzhwctrl.py.
        private static readonly (int temp, byte duty)[] FanCurve =
        {
            (0,  0x60),   // 38%
            (30, 0x80),   // 50%
            (40, 0x90),   // 56%
            (65, 0xB0),   // 69%
            (72, 0xD0),   // 82%
            (77, 0xFF),   // 100%
        };

        public const int PollIntervalMs = 3000;

        private readonly Sensors _sensors;
        private readonly Thread _thread;
        private volatile bool _stop;

        public byte? CpuOverride; // null = AUTO
        public byte? GpuOverride;

        public float CpuTemp { get; private set; }
        public float GpuTemp { get; private set; }
        public byte CpuDuty { get; private set; }
        public byte GpuDuty { get; private set; }

        public ControlLoop(Sensors sensors)
        {
            _sensors = sensors;
            _thread = new Thread(Run) { IsBackground = true };
        }

        public void Start() => _thread.Start();

        public void Stop()
        {
            _stop = true;
            _thread.Join(5000);
            EcPort.ResetToAuto(); // same as reset_to_ec_control() on exit
        }

        private static byte DutyForTemp(float? temp)
        {
            if (temp == null) return FanCurve[0].duty;
            byte duty = FanCurve[0].duty;
            foreach (var (threshold, d) in FanCurve)
                if (temp >= threshold) duty = d;
            return duty;
        }

        private void Run()
        {
            byte? lastCpu = null, lastGpu = null;

            while (!_stop)
            {
                var (cpuT, gpuT) = _sensors.GetTemps();
                CpuTemp = cpuT ?? 0f;
                GpuTemp = gpuT ?? 0f;

                byte cpuDuty = CpuOverride ?? DutyForTemp(cpuT);
                byte gpuDuty = GpuOverride ?? DutyForTemp(gpuT);

                CpuDuty = cpuDuty;
                GpuDuty = gpuDuty;

                try
                {
                    if (cpuDuty != lastCpu)
                    {
                        EcPort.SetFan(1, cpuDuty);
                        lastCpu = cpuDuty;
                    }
                    if (gpuDuty != lastGpu)
                    {
                        EcPort.SetFan(2, gpuDuty);
                        EcPort.SetFan(3, gpuDuty);
                        lastGpu = gpuDuty;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ec] write failed: {ex.Message}");
                }

                Thread.Sleep(PollIntervalMs);
            }
        }
    }
}
