using System;
using System.Drawing;
using System.Windows.Forms;

namespace LzHwCtrl
{
    internal sealed class MainForm : Form
    {
        private readonly Sensors _sensors = new();
        private readonly ControlLoop _loop;
        private readonly NotifyIcon _tray;
        private readonly Label _cpuTempLbl = new() { AutoSize = true };
        private readonly Label _gpuTempLbl = new() { AutoSize = true };
        private readonly Label _statusLbl = new() { AutoSize = true, Dock = DockStyle.Bottom };

        // (label, duty) -- same scale as FAN_BUTTONS in lzhwctrl.py
        private static readonly (string label, byte? duty)[] FanButtons =
        {
            ("AUTO", null), ("38%", 0x60), ("50%", 0x80),
            ("56%", 0x90), ("69%", 0xB0), ("82%", 0xD0), ("100%", 0xFF),
        };

        // (label, % of nominal max) -- replaces CPU_FREQ_BUTTONS' kHz values,
        // see CpuFreq.cs for why this is a percentage instead of kHz.
        private static readonly (string label, int? percent)[] FreqButtons =
        {
            ("AUTO", null), ("60%", 60), ("70%", 70), ("80%", 80), ("90%", 90),
        };

        public MainForm()
        {
            EcPort.EnsureDriverLoaded();

            _loop = new ControlLoop(_sensors);
            _loop.Start();

            Text = "lzhwctrl";
            Width = 600;
            Height = 480;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            AutoScroll = true; // safety net if a future row ever doesn't fit

            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, // rows stack vertically, never spill into a 2nd column
                Padding = new Padding(10),
            };

            layout.Controls.Add(new Label { Text = "CPU temp:", AutoSize = true });
            layout.Controls.Add(_cpuTempLbl);
            layout.Controls.Add(new Label { Text = "GPU temp:", AutoSize = true });
            layout.Controls.Add(_gpuTempLbl);

            layout.Controls.Add(BuildButtonRow("CPU FAN", FanButtons, duty => _loop.CpuOverride = duty));
            layout.Controls.Add(BuildButtonRow("GPU FAN", FanButtons, duty => _loop.GpuOverride = duty));
            layout.Controls.Add(BuildButtonRow("CPU FREQ", FreqButtons, pct => CpuFreq.SetMaxPercent(pct)));
            layout.Controls.Add(BuildKbRow());

            Controls.Add(layout);
            Controls.Add(_statusLbl);

            // lzhwctrl.ico is embedded into the exe via <ApplicationIcon> in the
            // csproj, so pull it back out at runtime instead of hardcoding a
            // separate file path -- one less thing for install.ps1 to stage.
            var appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            Icon = appIcon;

            _tray = new NotifyIcon
            {
                Icon = appIcon,
                Visible = true,
                Text = "lzhwctrl",
                ContextMenuStrip = BuildTrayMenu(),
            };
            _tray.DoubleClick += (_, __) => ShowFromTray();

            FormClosing += (_, e) =>
            {
                // Mirror the Python tray's "Exit" item: minimize-to-tray on the
                // X button, only truly exit via the tray menu, so the reset-to-
                // auto sequence in ControlLoop.Stop() doesn't fire on a stray click.
                if (e.CloseReason == CloseReason.UserClosing && !_exiting)
                {
                    e.Cancel = true;
                    Hide();
                }
            };

            var pollTimer = new Timer { Interval = ControlLoop.PollIntervalMs };
            pollTimer.Tick += (_, __) => RefreshLabels();
            pollTimer.Start();
        }

        private bool _exiting;

        private ContextMenuStrip BuildTrayMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show", null, (_, __) => ShowFromTray());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, __) =>
            {
                _exiting = true;
                _loop.Stop(); // resets EC to auto control before quitting
                _tray.Visible = false;
                Close();
            });
            return menu;
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private FlowLayoutPanel BuildButtonRow<T>(string title, (string label, T value)[] options, Action<T> onClick)
        {
            var panel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false, // keep all buttons in this row on one line
            };
            panel.Controls.Add(new Label { Text = title, AutoSize = true, Width = 80 });
            foreach (var (label, value) in options)
            {
                var btn = new Button { Text = label, AutoSize = true };
                btn.Click += (_, __) => onClick(value);
                panel.Controls.Add(btn);
            }
            return panel;
        }

        private FlowLayoutPanel BuildKbRow()
        {
            var panel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
            };
            panel.Controls.Add(new Label { Text = "KEYBOARD", AutoSize = true, Width = 80 });
            string[] labels = { "Off", "1", "2", "3", "4", "5" };
            for (int i = 0; i < labels.Length; i++)
            {
                int level = i;
                var btn = new Button { Text = labels[i], AutoSize = true };
                btn.Click += (_, __) => Keyboard.SetLevel(level);
                panel.Controls.Add(btn);
            }
            return panel;
        }

        private void RefreshLabels()
        {
            _cpuTempLbl.Text = $"{_loop.CpuTemp:0}C";
            _gpuTempLbl.Text = $"{_loop.GpuTemp:0}C";
            _statusLbl.Text = $"cpu duty 0x{_loop.CpuDuty:X2}  gpu duty 0x{_loop.GpuDuty:X2}";
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
