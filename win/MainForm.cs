using System;
using System.Drawing;
using System.Runtime.InteropServices;
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

        // ------------------------------------------------------------------ colours
        // Dark palette inspired by VS Code / Windows 11 dark mode.
        private static readonly Color Bg          = Color.FromArgb(28,  28,  28);   // form / panel bg
        private static readonly Color BgRow       = Color.FromArgb(36,  36,  36);   // row panels
        private static readonly Color Fg          = Color.FromArgb(220, 220, 220);   // normal text
        private static readonly Color FgDim       = Color.FromArgb(140, 140, 140);   // status / dim
        private static readonly Color AccentBg    = Color.FromArgb(0,   120, 215);   // active button
        private static readonly Color AccentBd    = Color.FromArgb(0,    84, 153);   // active border
        private static readonly Color BtnBg       = Color.FromArgb(50,  50,  50);   // inactive button
        private static readonly Color BtnBd       = Color.FromArgb(70,  70,  70);   // inactive border
        private static readonly Color BtnHover    = Color.FromArgb(65,  65,  65);   // hover
        private static readonly Color AccentText  = Color.White;
        private static readonly Color BtnText     = Color.FromArgb(210, 210, 210);

        // (label, duty) -- same scale as FAN_BUTTONS in lzhwctrl.py
        private static readonly (string label, byte? duty)[] FanButtons =
        {
            ("AUTO", null), ("38%", 0x60), ("50%", 0x80),
            ("56%", 0x90), ("69%", 0xB0), ("82%", 0xD0), ("100%", 0xFF),
        };

        private static readonly (string label, int? percent)[] FreqButtons =
        {
            ("AUTO", null), ("60%", 60), ("70%", 70), ("80%", 80), ("90%", 90),
        };

        private static readonly (string label, int level)[] KbButtons =
        {
            ("Off", 0), ("1", 1), ("2", 2), ("3", 3), ("4", 4), ("5", 5),
        };

        // ------------------------------------------------------------------ Win32 dark title bar
        // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 11 / 10 21H1+)
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void ApplyDarkTitleBar()
        {
            int dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        }

        // ------------------------------------------------------------------ constructor
        public MainForm()
        {
            EcPort.EnsureDriverLoaded();

            _loop = new ControlLoop(_sensors);
            _loop.Start();

            Text            = "lzhwctrl";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            AutoScroll      = false;
            ClientSize      = new Size(560, 220);
            BackColor       = Bg;
            ForeColor       = Fg;

            // Dark title bar (Windows 10 21H1+ / Windows 11)
            HandleCreated += (_, __) => ApplyDarkTitleBar();

            // ---- Outer layout -------------------------------------------
            var table = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 1,
                Padding     = new Padding(12, 10, 12, 8),
                BackColor   = Bg,
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
                BackColor     = Bg,
            };
            void AddTempLabel(string prefix, Label valueLabel)
            {
                tempPanel.Controls.Add(new Label
                {
                    Text      = prefix,
                    AutoSize  = true,
                    Margin    = new Padding(0, 3, 4, 0),
                    ForeColor = FgDim,
                    BackColor = Bg,
                });
                valueLabel.ForeColor = Fg;
                valueLabel.BackColor = Bg;
                tempPanel.Controls.Add(valueLabel);
            }
            AddTempLabel("CPU temp:", _cpuTempLbl);
            var gpuPrefix = new Label
            {
                Text      = "GPU temp:",
                AutoSize  = true,
                Margin    = new Padding(12, 3, 4, 0),
                ForeColor = FgDim,
                BackColor = Bg,
            };
            tempPanel.Controls.Add(gpuPrefix);
            _gpuTempLbl.ForeColor = Fg;
            _gpuTempLbl.BackColor = Bg;
            tempPanel.Controls.Add(_gpuTempLbl);
            table.Controls.Add(tempPanel);

            // Button rows
            table.Controls.Add(BuildButtonRow("CPU FAN",  FanButtons,  duty => _loop.CpuOverride = duty,   initialIndex: 0));
            table.Controls.Add(BuildButtonRow("GPU FAN",  FanButtons,  duty => _loop.GpuOverride = duty,   initialIndex: 0));
            table.Controls.Add(BuildButtonRow("CPU FREQ", FreqButtons, pct  => CpuFreq.SetMaxPercent(pct), initialIndex: 0));
            table.Controls.Add(BuildButtonRow("KEYBOARD", KbButtons,   lvl  => Keyboard.SetLevel(lvl),     initialIndex: 0));

            Controls.Add(table);

            _statusLbl.ForeColor = FgDim;
            _statusLbl.BackColor = Bg;
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

            // Start minimised to tray
            ShowInTaskbar = false;
            WindowState   = FormWindowState.Minimized;
            Load += (_, __) => Hide();

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

        // ------------------------------------------------------------------ tray menu
        private ContextMenuStrip BuildTrayMenu()
        {
            var menu = new ContextMenuStrip();
            // Dark tray menu
            menu.BackColor = Color.FromArgb(40, 40, 40);
            menu.ForeColor = Fg;
            menu.Renderer  = new DarkMenuRenderer();

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

        // ------------------------------------------------------------------ button rows
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
                BackColor     = Bg,
            };

            panel.Controls.Add(new Label
            {
                Text      = title,
                AutoSize  = false,
                Width     = 72,
                TextAlign = ContentAlignment.MiddleRight,
                Margin    = new Padding(0, 6, 6, 0),
                ForeColor = FgDim,
                BackColor = Bg,
            });

            var buttons = new Button[options.Length];

            void SetActive(int activeIdx)
            {
                for (int j = 0; j < buttons.Length; j++)
                {
                    bool active = (j == activeIdx);
                    buttons[j].BackColor                       = active ? AccentBg : BtnBg;
                    buttons[j].ForeColor                       = active ? AccentText : BtnText;
                    buttons[j].FlatAppearance.BorderColor      = active ? AccentBd : BtnBd;
                    buttons[j].FlatAppearance.MouseOverBackColor =
                        active ? Color.FromArgb(0, 137, 240) : BtnHover;
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
                    BackColor = BtnBg,
                    ForeColor = BtnText,
                    Cursor    = Cursors.Hand,
                };
                btn.FlatAppearance.BorderSize = 1;
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

        // ------------------------------------------------------------------ labels
        private void RefreshLabels()
        {
            _cpuTempLbl.Text = $"{_loop.CpuTemp:0}C";
            _gpuTempLbl.Text = $"{_loop.GpuTemp:0}C";
            _statusLbl.Text  = $"cpu duty 0x{_loop.CpuDuty:X2}  gpu duty 0x{_loop.GpuDuty:X2}";
        }
    }

    // ------------------------------------------------------------------ dark tray menu renderer
    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color MenuBg     = Color.FromArgb(40,  40,  40);
        private static readonly Color MenuHover  = Color.FromArgb(0,  120, 215);
        private static readonly Color MenuBorder = Color.FromArgb(65,  65,  65);
        private static readonly Color MenuFg     = Color.FromArgb(220, 220, 220);
        private static readonly Color SepColor   = Color.FromArgb(65,  65,  65);

        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
            using var brush = new SolidBrush(e.Item.Selected ? MenuHover : MenuBg);
            e.Graphics.FillRectangle(brush, rect);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(MenuBorder);
            e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(SepColor);
            e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = MenuFg;
            base.OnRenderItemText(e);
        }
    }

    internal sealed class DarkColorTable : ProfessionalColorTable
    {
        private static readonly Color MenuBg = Color.FromArgb(40, 40, 40);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(0, 120, 215);
        public override Color MenuItemSelectedGradientEnd   => Color.FromArgb(0, 120, 215);
        public override Color MenuBorder                    => Color.FromArgb(65, 65, 65);
        public override Color ToolStripDropDownBackground   => MenuBg;
        public override Color ImageMarginGradientBegin      => MenuBg;
        public override Color ImageMarginGradientMiddle     => MenuBg;
        public override Color ImageMarginGradientEnd        => MenuBg;
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
