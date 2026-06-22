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
        private readonly Label _statusLbl  = new() { AutoSize = true, Dock = DockStyle.Bottom };

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

        // Highlight colour used for the active button in each row.
        private static readonly Color ActiveColor   = Color.FromArgb(0, 120, 215); // Windows-blue
        private static readonly Color InactiveColor = SystemColors.Control;
        private static readonly Color ActiveText    = Color.White;
        private static readonly Color InactiveText  = SystemColors.ControlText;

        public MainForm()
        {
            EcPort.EnsureDriverLoaded();

            _loop = new ControlLoop(_sensors);
            _loop.Start();

            Text            = "lzhwctrl";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            AutoScroll      = false;
            // 7 fan buttons × 62 px + 78 px label + 24 px padding = ~560 px client width.
            // Height is driven by row count; 220 px fits 4 rows + temp + status comfortably.
            ClientSize      = new Size(560, 220);

            // ── Outer layout ────────────────────────────────────────────────
            // TableLayoutPanel fills the form so rows always have room to
            // render all their buttons without clipping.
            var table = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 1,
                Padding     = new Padding(12, 10, 12, 8),
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // Temp labels
            var tempPanel = new FlowLayoutPanel
            {
                AutoSize      = true,
                AutoSizeMode  = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                Margin        = new Padding(0, 0, 0, 6),
            };
            tempPanel.Controls.Add(new Label { Text = "CPU temp:", AutoSize = true, Margin = new Padding(0,3,4,0) });
            tempPanel.Controls.Add(_cpuTempLbl);
            tempPanel.Controls.Add(new Label { Text = "GPU temp:", AutoSize = true, Margin = new Padding(12,3,4,0) });
            tempPanel.Controls.Add(_gpuTempLbl);
            table.Controls.Add(tempPanel);

            // Button rows — each returns a panel; index 0 in each group is
            // highlighted immediately so the user sees the current mode.
            table.Controls.Add(BuildButtonRow("CPU FAN",  FanButtons,  duty => _loop.CpuOverride = duty,  initialIndex: 0));
            table.Controls.Add(BuildButtonRow("GPU FAN",  FanButtons,  duty => _loop.GpuOverride = duty,  initialIndex: 0));
            table.Controls.Add(BuildButtonRow("CPU FREQ", FreqButtons, pct  => CpuFreq.SetMaxPercent(pct), initialIndex: 0));
            table.Controls.Add(BuildKbRow(initialIndex: 0));

            Controls.Add(table);
            Controls.Add(_statusLbl);

            var appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            Icon = appIcon;

            _tray = new NotifyIcon
            {
                Icon             = appIcon,
                Visible          = true,
                Text             = "lzhwctrl",
                ContextMenuStrip = BuildTrayMenu(),
            };
            _tray.DoubleClick += (_, __) => ShowFromTray();

            FormClosing += (_, e) =>
            {
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
                _loop.Stop();
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

        /// <summary>
        /// Builds a labelled row of buttons where exactly one button is
        /// highlighted at a time (the active selection).
        /// </summary>
        private FlowLayoutPanel BuildButtonRow<T>(
            string title,
            (string label, T value)[] options,
            Action<T> onClick,
            int initialIndex = 0)
        {
            var panel = new FlowLayoutPanel
            {
                AutoSize      = true,
                AutoSizeMode  = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                Margin        = new Padding(0, 2, 0, 2),
            };

            // Fixed-width label so all button groups line up.
            panel.Controls.Add(new Label
            {
                Text      = title,
                AutoSize  = false,
                Width     = 72,
                TextAlign = ContentAlignment.MiddleRight,
                Margin    = new Padding(0, 6, 6, 0),
            });

            var buttons = new Button[options.Length];

            void SetActive(int activeIdx)
            {
                for (int j = 0; j < buttons.Length; j++)
                {
                    buttons[j].BackColor = (j == activeIdx) ? ActiveColor   : InactiveColor;
                    buttons[j].ForeColor = (j == activeIdx) ? ActiveText    : InactiveText;
                    buttons[j].FlatStyle = FlatStyle.Flat;
                    buttons[j].FlatAppearance.BorderColor =
                        (j == activeIdx) ? Color.FromArgb(0, 84, 153) : SystemColors.ControlDark;
                }
            }

            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                var (label, value) = options[i];

                var btn = new Button
                {
                    Text      = label,
                    AutoSize  = false,
                    Width     = 58,
                    Height    = 28,
                    FlatStyle = FlatStyle.Flat,
                    Margin    = new Padding(2),
                };
                btn.Click += (_, __) =>
                {
                    onClick(value);
                    SetActive(idx);
                };
                buttons[i] = btn;
                panel.Controls.Add(btn);
            }

            SetActive(initialIndex);
            return panel;
        }

        /// <summary>
        /// Keyboard backlight row — Off + levels 1–5.
        /// </summary>
        private FlowLayoutPanel BuildKbRow(int initialIndex = 0)
        {
            var panel = new FlowLayoutPanel
            {
                AutoSize      = true,
                AutoSizeMode  = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                Margin        = new Padding(0, 2, 0, 2),
            };

            panel.Controls.Add(new Label
            {
                Text      = "KEYBOARD",
                AutoSize  = false,
                Width     = 72,
                TextAlign = ContentAlignment.MiddleRight,
                Margin    = new Padding(0, 6, 6, 0),
            });

            string[] labels = { "Off", "1", "2", "3", "4", "5" };
            var buttons     = new Button[labels.Length];

            void SetActive(int activeIdx)
            {
                for (int j = 0; j < buttons.Length; j++)
                {
                    buttons[j].BackColor = (j == activeIdx) ? ActiveColor   : InactiveColor;
                    buttons[j].ForeColor = (j == activeIdx) ? ActiveText    : InactiveText;
                    buttons[j].FlatStyle = FlatStyle.Flat;
                    buttons[j].FlatAppearance.BorderColor =
                        (j == activeIdx) ? Color.FromArgb(0, 84, 153) : SystemColors.ControlDark;
                }
            }

            for (int i = 0; i < labels.Length; i++)
            {
                int level = i;
                var btn = new Button
                {
                    Text      = labels[i],
                    AutoSize  = false,
                    Width     = 58,
                    Height    = 28,
                    FlatStyle = FlatStyle.Flat,
                    Margin    = new Padding(2),
                };
                btn.Click += (_, __) =>
                {
                    bool ok = Keyboard.SetLevel(level);
                    if (ok) SetActive(level);
                    // If the WMI call fails, don't move the highlight so the
                    // user can see that the selection didn't take effect.
                };
                buttons[i] = btn;
                panel.Controls.Add(btn);
            }

            // Try to read the current keyboard level so the initial highlight
            // reflects the real hardware state. Wrapped in try/catch so any
            // failure here never prevents the window from opening.
            try
            {
                if (Keyboard.TryGetLevel(out int currentLevel))
                    SetActive(currentLevel);
                else
                    SetActive(initialIndex);
            }
            catch { SetActive(initialIndex); }

            return panel;
        }

        private void RefreshLabels()
        {
            _cpuTempLbl.Text = $"{_loop.CpuTemp:0}C";
            _gpuTempLbl.Text = $"{_loop.GpuTemp:0}C";
            _statusLbl.Text  = $"cpu duty 0x{_loop.CpuDuty:X2}  gpu duty 0x{_loop.GpuDuty:X2}";
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
