using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskbarDock
{
    internal sealed class DockApplicationContext : ApplicationContext
    {
        private const string ConflictWarning =
            "TaskbarXI está en ejecución y también cambia la forma de la barra. Ciérralo para evitar conflictos.";
        private readonly SettingsStore store;
        private readonly FloatingTaskbarEngine floatingEngine;
        private readonly DockOverlayForm overlay;
        private readonly TaskbarMonitor monitor;
        private readonly SettingsForm form;
        private readonly MenuBarForm menuBar;
        private readonly NotifyIcon trayIcon;
        private readonly ToolStripMenuItem enabledItem;
        private readonly Icon trayIconImage;
        private DockSettings settings;
        private bool conflictNotified;
        private bool exiting;

        public DockApplicationContext()
        {
            AppTrace.Log("Loading settings.");
            store = new SettingsStore();
            settings = store.Load();
            AppTrace.Log("Creating floating taskbar region.");
            floatingEngine = new FloatingTaskbarEngine(settings);
            AppTrace.Log("Creating native taskbar magnifier.");
            overlay = new DockOverlayForm(settings);
            overlay.SetEngineVisible(settings.Enabled);
            monitor = new TaskbarMonitor(overlay);
            monitor.SnapshotAvailable += delegate(TaskbarSnapshot snapshot)
            {
                floatingEngine.Update(snapshot);
                overlay.UpdateSnapshot(snapshot);
            };
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            AppTrace.Log("Creating menu bar.");
            menuBar = new MenuBarForm { AutoHideWhenMaximized = settings.AutoHideMenuBar };
            menuBar.Enable(settings.ShowMenuBar);

            AppTrace.Log("Creating settings form.");
            form = new SettingsForm(settings);
            MainForm = form;
            form.SettingsApplied += delegate(object sender, DockSettingsEventArgs args)
            {
                ApplySettings(args.Settings);
            };
            form.ExitRequested += delegate { ExitApplication(); };
            form.VisibleChanged += delegate
            {
                if (form.Visible)
                    CheckForConflicts();
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Ajustes…", null, delegate { ShowSettings(); });
            enabledItem = new ToolStripMenuItem("Activado") { Checked = settings.Enabled };
            enabledItem.Click += delegate { SetEnabled(!settings.Enabled); };
            menu.Items.Add(enabledItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Salir", null, delegate { ExitApplication(); });

            trayIconImage = CreateTrayIcon();
            trayIcon = new NotifyIcon
            {
                Text = "TaskbarDock",
                Icon = trayIconImage,
                ContextMenuStrip = menu,
                Visible = true
            };
            trayIcon.MouseClick += delegate(object sender, MouseEventArgs args)
            {
                if (args.Button == MouseButtons.Left)
                    ShowSettings();
            };

            string[] commandLine = Environment.GetCommandLineArgs();
            if (Array.IndexOf(commandLine, "--background") < 0)
            {
                AppTrace.Log("Showing settings form.");
                ShowSettings();
            }
            else
            {
                CheckForConflicts();
            }
            AppTrace.Log("Application context created.");
        }

        private void ApplySettings(DockSettings newSettings)
        {
            settings = newSettings.Clone();
            floatingEngine.Apply(settings);
            overlay.Apply(settings);
            overlay.SetEngineVisible(settings.Enabled);
            menuBar.AutoHideWhenMaximized = settings.AutoHideMenuBar;
            menuBar.Enable(settings.ShowMenuBar);
            enabledItem.Checked = settings.Enabled;
            monitor.RequestRefresh();
            try
            {
                store.Save(settings);
                if (!CheckForConflicts())
                    form.ShowStatus(settings.Enabled ? "Cambios aplicados" : "TaskbarDock desactivado");
            }
            catch (Exception exception)
            {
                AppTrace.Log("Saving settings failed: " + exception);
                form.ShowWarning("No se pudieron guardar los ajustes: " + exception.Message);
            }
        }

        private void SetEnabled(bool enabled)
        {
            DockSettings updated = settings.Clone();
            updated.Enabled = enabled;
            form.LoadValues(updated, false);
            ApplySettings(updated);
        }

        private bool CheckForConflicts()
        {
            Process[] conflicting = Process.GetProcessesByName("TaskbarXI");
            bool found = conflicting.Length > 0;
            foreach (Process process in conflicting)
                process.Dispose();
            if (!found)
                return false;

            form.ShowWarning(ConflictWarning);
            if (!conflictNotified && !form.Visible)
            {
                conflictNotified = true;
                trayIcon.ShowBalloonTip(6000, "TaskbarDock", ConflictWarning, ToolTipIcon.Warning);
            }
            return true;
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs args)
        {
            monitor.RequestRefresh();
        }

        private void ShowSettings()
        {
            if (!form.Visible)
                form.Show();
            if (form.WindowState == FormWindowState.Minimized)
                form.WindowState = FormWindowState.Normal;
            form.TopMost = true;
            form.BringToFront();
            form.Activate();
            form.BeginInvoke((MethodInvoker)delegate { form.TopMost = false; });
        }

        private static Icon CreateTrayIcon()
        {
            // A small dock: a rounded shelf with three app tiles, the middle one magnified.
            using (var bitmap = new Bitmap(32, 32))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);
                    using (GraphicsPath shelf = RoundedRectangle(new RectangleF(1, 19, 30, 11), 5))
                    using (var shelfBrush = new SolidBrush(Color.FromArgb(220, 235, 235, 245)))
                        graphics.FillPath(shelfBrush, shelf);
                    FillTile(graphics, new RectangleF(4, 15, 8, 8), Color.FromArgb(255, 90, 140, 245));
                    FillTile(graphics, new RectangleF(20, 15, 8, 8), Color.FromArgb(255, 245, 160, 70));
                    FillTile(graphics, new RectangleF(10, 5, 12, 12), Color.FromArgb(255, 110, 210, 140));
                }
                IntPtr handle = bitmap.GetHicon();
                using (Icon temporary = Icon.FromHandle(handle))
                {
                    Icon copy = (Icon)temporary.Clone();
                    NativeMethods.DestroyIcon(handle);
                    return copy;
                }
            }
        }

        private static void FillTile(Graphics graphics, RectangleF bounds, Color color)
        {
            using (GraphicsPath tile = RoundedRectangle(bounds, bounds.Width / 4.5f))
            using (var brush = new SolidBrush(color))
                graphics.FillPath(brush, tile);
        }

        private static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float diameter = radius * 2;
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void ExitApplication()
        {
            if (exiting)
                return;
            exiting = true;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            monitor.Dispose();
            menuBar.Enable(false);
            menuBar.Dispose();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayIconImage.Dispose();
            overlay.Hide();
            overlay.Dispose();
            floatingEngine.Dispose();
            form.AllowClose();
            form.Close();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !exiting)
                ExitApplication();
            base.Dispose(disposing);
        }
    }
}
