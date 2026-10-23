using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TaskbarDock
{
    internal sealed class DockSettingsEventArgs : EventArgs
    {
        public DockSettings Settings { get; private set; }
        public DockSettingsEventArgs(DockSettings settings) { Settings = settings; }
    }

    internal sealed class SettingsForm : Form
    {
        private const int AutoIconValue = SettingsStore.MinimumIconSize - 1;
        private static readonly Color StatusColor = Color.FromArgb(135, 211, 162);
        private static readonly Color WarningColor = Color.FromArgb(240, 180, 90);
        private readonly DockSettings baseSettings;
        private readonly CheckBox enabled;
        private readonly CheckBox startup;
        private readonly CheckBox showLabels;
        private readonly CheckBox keepTray;
        private readonly CheckBox menuBar;
        private readonly CheckBox autoHideMenuBar;
        private readonly TrackBar iconSize;
        private readonly Label iconSizeValue;
        private readonly TrackBar magnification;
        private readonly Label magnificationValue;
        private readonly TrackBar reach;
        private readonly Label reachValue;
        private readonly TrackBar animationSpeed;
        private readonly Label animationSpeedValue;
        private readonly TrackBar cornerRadius;
        private readonly Label cornerRadiusValue;
        private readonly TrackBar padding;
        private readonly Label paddingValue;
        private readonly PreviewPanel preview;
        private readonly Label status;
        private readonly Timer applyTimer;
        private bool loading;
        private bool allowClose;

        public event EventHandler<DockSettingsEventArgs> SettingsApplied;
        public event EventHandler ExitRequested;

        public SettingsForm(DockSettings settings)
        {
            baseSettings = settings.Clone();
            Text = "TaskbarDock";
            ClientSize = new Size(680, 770);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(18, 18, 24);
            ForeColor = Color.FromArgb(235, 235, 242);
            Font = new Font("Segoe UI", 10F);

            var title = new Label
            {
                Text = "TaskbarDock",
                Font = new Font("Segoe UI Semibold", 22F),
                AutoSize = true,
                Location = new Point(30, 24)
            };
            var subtitle = new Label
            {
                Text = "Un dock al estilo macOS con los botones reales de tu barra de tareas.",
                ForeColor = Color.FromArgb(155, 155, 170),
                AutoSize = true,
                Location = new Point(33, 67)
            };

            preview = new PreviewPanel
            {
                Location = new Point(30, 102),
                Size = new Size(620, 140),
                BackColor = Color.FromArgb(28, 29, 38)
            };

            enabled = CreateCheckBox("Activar TaskbarDock", 30, 278, settings.Enabled);
            startup = CreateCheckBox("Iniciar con Windows", 30, 316, settings.RunAtStartup);
            showLabels = CreateCheckBox(
                "Mostrar nombre al pasar el cursor", 30, 354, settings.ShowLabels);
            keepTray = CreateCheckBox(
                "Reloj y notificaciones en otra cápsula", 30, 392, settings.KeepTrayVisible);
            menuBar = CreateCheckBox(
                "Barra superior estilo macOS", 30, 430, settings.ShowMenuBar);
            autoHideMenuBar = CreateCheckBox(
                "Ocultarla al maximizar una app", 52, 468, settings.AutoHideMenuBar);

            iconSize = CreateTrackBar(380, 270, AutoIconValue, 64,
                settings.IconSize == 0 ? AutoIconValue : settings.IconSize);
            iconSizeValue = CreateValueLabel(594, 278);
            magnification = CreateTrackBar(380, 334, 0, 150, settings.Magnification);
            magnificationValue = CreateValueLabel(594, 342);
            reach = CreateTrackBar(380, 398, 1, 5, settings.Reach);
            reachValue = CreateValueLabel(594, 406);
            animationSpeed = CreateTrackBar(380, 462, 2, 20, settings.AnimationSpeed);
            animationSpeedValue = CreateValueLabel(594, 470);
            cornerRadius = CreateTrackBar(380, 526, 0, 28, settings.CornerRadius);
            cornerRadiusValue = CreateValueLabel(594, 534);
            padding = CreateTrackBar(380, 590, 0, 24, settings.HorizontalPadding);
            paddingValue = CreateValueLabel(594, 598);

            Controls.Add(CreateSliderLabel("Tamaño del icono nativo", 380, 258));
            Controls.Add(CreateSliderLabel("Magnificación", 380, 322));
            Controls.Add(CreateSliderLabel("Alcance del efecto", 380, 386));
            Controls.Add(CreateSliderLabel("Velocidad de animación", 380, 450));
            Controls.Add(CreateSliderLabel("Radio de la cápsula", 380, 514));
            Controls.Add(CreateSliderLabel("Margen lateral", 380, 578));

            var reset = CreateButton("Restablecer", 30, 660, false);
            reset.Size = new Size(116, 38);
            reset.Click += delegate { LoadValues(DockSettings.CreateDefault(), true); };
            var close = CreateButton("Cerrar", 156, 660, true);
            close.Click += delegate { Hide(); };
            var exit = CreateButton("Salir", 548, 660, false);
            exit.Click += delegate
            {
                EventHandler handler = ExitRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };

            status = new Label
            {
                AutoSize = false,
                Size = new Size(616, 22),
                ForeColor = StatusColor,
                Location = new Point(32, 712),
                Text = "Los cambios se aplican al momento"
            };
            var note = new Label
            {
                AutoSize = false,
                Size = new Size(616, 22),
                ForeColor = Color.FromArgb(120, 120, 135),
                Location = new Point(32, 736),
                Text = "TaskbarDock sigue en el área de notificaciones al cerrar esta ventana."
            };

            Controls.AddRange(new Control[]
            {
                title, subtitle, preview, enabled, startup, showLabels, keepTray, menuBar, autoHideMenuBar,
                iconSize, iconSizeValue, magnification, magnificationValue,
                reach, reachValue, animationSpeed, animationSpeedValue,
                cornerRadius, cornerRadiusValue, padding, paddingValue,
                reset, close, exit, status, note
            });

            applyTimer = new Timer { Interval = 220 };
            applyTimer.Tick += delegate
            {
                applyTimer.Stop();
                RaiseSettingsApplied();
            };

            foreach (CheckBox box in new[] { enabled, startup, showLabels, keepTray, menuBar, autoHideMenuBar })
                box.CheckedChanged += delegate { OnValueChanged(); };
            foreach (TrackBar bar in new[] { iconSize, magnification, reach, animationSpeed, cornerRadius, padding })
                bar.ValueChanged += delegate { OnValueChanged(); };
            UpdatePreview();
        }

        private CheckBox CreateCheckBox(string text, int x, int y, bool value)
        {
            return new CheckBox
            {
                Text = text,
                Checked = value,
                AutoSize = true,
                Location = new Point(x, y),
                FlatStyle = FlatStyle.Flat,
                ForeColor = ForeColor,
                Cursor = Cursors.Hand
            };
        }

        private TrackBar CreateTrackBar(int x, int y, int min, int max, int value)
        {
            return new TrackBar
            {
                Location = new Point(x, y),
                Size = new Size(210, 38),
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, value)),
                TickStyle = TickStyle.None,
                BackColor = BackColor
            };
        }

        private Label CreateValueLabel(int x, int y)
        {
            return new Label
            {
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(175, 175, 190),
                Location = new Point(x, y),
                Size = new Size(56, 25)
            };
        }

        private Label CreateSliderLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = Color.FromArgb(150, 150, 165),
                Font = new Font("Segoe UI", 8.5F),
                Location = new Point(x, y)
            };
        }

        private Button CreateButton(string text, int x, int y, bool accent)
        {
            var button = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(100, 38),
                FlatStyle = FlatStyle.Flat,
                BackColor = accent ? Color.FromArgb(74, 110, 230) : Color.FromArgb(42, 43, 54),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }

        private void OnValueChanged()
        {
            UpdatePreview();
            if (loading)
                return;
            // Debounced so dragging a slider does not rewrite the settings file per pixel.
            applyTimer.Stop();
            applyTimer.Start();
        }

        /// <summary>
        /// Updates the controls. With apply false the change is not echoed back as new settings.
        /// </summary>
        public void LoadValues(DockSettings settings, bool apply)
        {
            loading = !apply;
            try
            {
                enabled.Checked = settings.Enabled;
                showLabels.Checked = settings.ShowLabels;
                keepTray.Checked = settings.KeepTrayVisible;
                menuBar.Checked = settings.ShowMenuBar;
                autoHideMenuBar.Checked = settings.AutoHideMenuBar;
                iconSize.Value = settings.IconSize == 0 ? AutoIconValue : settings.IconSize;
                magnification.Value = settings.Magnification;
                reach.Value = settings.Reach;
                animationSpeed.Value = settings.AnimationSpeed;
                cornerRadius.Value = settings.CornerRadius;
                padding.Value = settings.HorizontalPadding;
            }
            finally
            {
                loading = false;
            }
        }

        private void UpdatePreview()
        {
            iconSizeValue.Text = iconSize.Value == AutoIconValue ? "Auto" : iconSize.Value + " px";
            magnificationValue.Text = magnification.Value + "%";
            reachValue.Text = reach.Value == 1 ? "1 icono" : reach.Value + " iconos";
            animationSpeedValue.Text = animationSpeed.Value.ToString();
            cornerRadiusValue.Text = cornerRadius.Value + " px";
            paddingValue.Text = padding.Value + " px";
            preview.EnabledEffect = enabled.Checked;
            preview.IconSize = iconSize.Value == AutoIconValue ? 0 : iconSize.Value;
            preview.Magnification = magnification.Value;
            preview.Reach = reach.Value;
            preview.CornerRadius = cornerRadius.Value;
            preview.PaddingSize = padding.Value;
            preview.ShowTray = keepTray.Checked;
            preview.Invalidate();
        }

        private void RaiseSettingsApplied()
        {
            DockSettings settings = baseSettings.Clone();
            settings.Enabled = enabled.Checked;
            settings.RunAtStartup = startup.Checked;
            settings.ShowLabels = showLabels.Checked;
            settings.IconSize = iconSize.Value == AutoIconValue ? 0 : iconSize.Value;
            settings.Magnification = magnification.Value;
            settings.Reach = reach.Value;
            settings.AnimationSpeed = animationSpeed.Value;
            settings.CornerRadius = cornerRadius.Value;
            settings.HorizontalPadding = padding.Value;
            settings.KeepTrayVisible = keepTray.Checked;
            settings.ShowMenuBar = menuBar.Checked;
            settings.AutoHideMenuBar = autoHideMenuBar.Checked;
            EventHandler<DockSettingsEventArgs> handler = SettingsApplied;
            if (handler != null)
                handler(this, new DockSettingsEventArgs(settings));
        }

        public void ShowStatus(string message)
        {
            status.ForeColor = StatusColor;
            status.Text = message;
        }

        public void ShowWarning(string message)
        {
            status.ForeColor = WarningColor;
            status.Text = message;
        }

        public void AllowClose()
        {
            allowClose = true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                applyTimer.Dispose();
            base.Dispose(disposing);
        }

        private sealed class PreviewPanel : Panel
        {
            public int IconSize { get; set; }
            public int Magnification { get; set; }
            public int Reach { get; set; }
            public bool EnabledEffect { get; set; }
            public int CornerRadius { get; set; }
            public int PaddingSize { get; set; }
            public bool ShowTray { get; set; }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var desktop = new SolidBrush(Color.FromArgb(35, 37, 48)))
                    e.Graphics.FillRectangle(desktop, ClientRectangle);

                int inset = Math.Max(0, Math.Min(18, PaddingSize));
                var taskbar = new Rectangle(55 - inset, 76, 430 + inset * 2, 52);
                using (GraphicsPath path = RoundedRectangle(taskbar, Math.Min(25, CornerRadius)))
                using (var background = new SolidBrush(Color.FromArgb(31, 33, 43)))
                using (var border = new Pen(Color.FromArgb(82, 86, 105)))
                {
                    e.Graphics.FillPath(background, path);
                    e.Graphics.DrawPath(border, path);
                }

                if (ShowTray)
                {
                    var tray = new Rectangle(505, 76, 92, 52);
                    using (GraphicsPath path = RoundedRectangle(
                        tray, Math.Min(25, CornerRadius)))
                    using (var background = new SolidBrush(Color.FromArgb(31, 33, 43)))
                    using (var border = new Pen(Color.FromArgb(82, 86, 105)))
                    {
                        e.Graphics.FillPath(background, path);
                        e.Graphics.DrawPath(border, path);
                    }
                    using (var textBrush = new SolidBrush(Color.FromArgb(220, 220, 230)))
                        e.Graphics.DrawString("19:32", Font, textBrush, 531, 94);
                }

                int baseSize = IconSize == 0 ? 27 : Math.Max(18, IconSize * 27 / 32);
                const int spacing = 48;
                float radius = spacing * Math.Max(1, Reach);
                float cursorX = 92 + 4 * spacing;
                for (int index = 0; index < 8; index++)
                {
                    int centerX = 92 + index * spacing;
                    float distance = Math.Abs(centerX - cursorX);
                    float influence = !EnabledEffect || distance >= radius ? 0f :
                        (1f + (float)Math.Cos(Math.PI * distance / radius)) / 2f;
                    int size = baseSize + (int)(baseSize * Magnification / 100f * influence);
                    int bottom = 117;
                    using (var brush = new SolidBrush(Color.FromArgb(
                        75 + index * 13, 115 + index * 7, 225 - index * 10)))
                    using (GraphicsPath icon = RoundedRectangle(
                        new Rectangle(centerX - size / 2, bottom - size, size, size), size / 5))
                        e.Graphics.FillPath(brush, icon);
                }
            }

            private static GraphicsPath RoundedRectangle(Rectangle rect, int radius)
            {
                var path = new GraphicsPath();
                if (radius <= 0)
                {
                    path.AddRectangle(rect);
                    return path;
                }
                int diameter = radius * 2;
                path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
                path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
                path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                return path;
            }
        }
    }
}
