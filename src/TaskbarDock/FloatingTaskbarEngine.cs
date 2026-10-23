using System;
using System.Drawing;

namespace TaskbarDock
{
    internal sealed class FloatingTaskbarEngine : IDisposable
    {
        private readonly EdgeSmootherForm edges = new EdgeSmootherForm();
        private DockSettings settings;
        private TaskbarSnapshot lastSnapshot;
        private string appliedSignature;
        private bool paused;
        private bool fullWidth;
        private readonly System.Windows.Forms.Timer stateTimer;
        private readonly uint ownProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        private bool disposed;

        public FloatingTaskbarEngine(DockSettings initialSettings)
        {
            settings = initialSettings.Clone();
            // Maximizing an app should give it a full-width taskbar straight away.
            stateTimer = new System.Windows.Forms.Timer { Interval = 250 };
            stateTimer.Tick += delegate
            {
                bool expand = ShouldUseFullWidth();
                if (expand != fullWidth)
                {
                    fullWidth = expand;
                    Refresh();
                }
            };
            stateTimer.Start();
        }

        /// <summary>
        /// True while the active app is maximized or full screen on the primary monitor; the
        /// taskbar then goes back to its native full width, as the app expects.
        /// </summary>
        private bool ShouldUseFullWidth()
        {
            IntPtr window = NativeMethods.GetForegroundWindow();
            if (window == IntPtr.Zero)
                return false;
            uint processId;
            NativeMethods.GetWindowThreadProcessId(window, out processId);
            if (processId == ownProcessId)
                return fullWidth; // Our own panels keep the current shape.
            if (!System.Windows.Forms.Screen.FromHandle(window).Primary)
                return false;
            return NativeMethods.IsZoomed(window) || NativeMethods.IsFullScreenAppRunning();
        }

        public void Apply(DockSettings newSettings)
        {
            settings = newSettings.Clone();
            paused = false;
            appliedSignature = null;
            Refresh();
        }

        public void Update(TaskbarSnapshot snapshot)
        {
            lastSnapshot = snapshot;
            Refresh();
        }

        public void Restore()
        {
            paused = true;
            edges.Clear();
            RestoreNativeShape();
            appliedSignature = null;
        }

        /// <summary>
        /// Gives the taskbar back its native rectangular shape. Safe to call at any time,
        /// including from crash handlers.
        /// </summary>
        public static void RestoreNativeShape()
        {
            IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
                NativeMethods.SetWindowRgn(taskbar, IntPtr.Zero, true);
        }

        private void Refresh()
        {
            if (disposed || lastSnapshot == null)
                return;
            IntPtr taskbar = lastSnapshot.Taskbar;
            if (!NativeMethods.IsWindow(taskbar))
                return;

            if (paused || !settings.Enabled || fullWidth)
            {
                string restored = "restored:" + taskbar;
                if (appliedSignature != restored)
                {
                    edges.Clear();
                    NativeMethods.SetWindowRgn(taskbar, IntPtr.Zero, true);
                    appliedSignature = restored;
                }
                return;
            }
            if (!lastSnapshot.HasContent)
                return;

            Rectangle taskbarBounds = lastSnapshot.TaskbarBounds;
            RectangleF contentBounds = lastSnapshot.ContentBounds;
            int taskbarWidth = taskbarBounds.Width;
            int taskbarHeight = taskbarBounds.Height;
            if (taskbarWidth <= 0 || taskbarHeight <= 0)
                return;

            float scale = lastSnapshot.Dpi / 96f;
            int padding = (int)Math.Round(settings.HorizontalPadding * scale);
            int left = Math.Max(0, (int)Math.Floor(contentBounds.Left - taskbarBounds.Left) - padding);
            int right = Math.Min(
                taskbarWidth,
                (int)Math.Ceiling(contentBounds.Right - taskbarBounds.Left) + padding);
            int verticalInset = Math.Min(5, Math.Max(1, taskbarHeight / 10));
            int radius = Math.Min(
                (int)Math.Round(settings.CornerRadius * scale),
                (taskbarHeight - verticalInset * 2) / 2);

            NativeMethods.RECT trayBounds = new NativeMethods.RECT();
            bool hasTray = settings.KeepTrayVisible &&
                           TryGetTrayBounds(taskbar, taskbarBounds, out trayBounds);
            // The window handle is part of the signature so the shape is reapplied
            // automatically after Explorer restarts and recreates the taskbar.
            string signature = taskbar + ":" + left + ":" + right + ":" + taskbarWidth + ":" +
                               taskbarHeight + ":" + radius + ":" + padding + ":" +
                               (hasTray ? trayBounds.Left + ":" + trayBounds.Right : "no-tray");
            if (signature == appliedSignature)
                return;

            int top = verticalInset;
            int bottom = taskbarHeight - verticalInset + 1;
            var shapes = new System.Collections.Generic.List<CapsuleShape>
            {
                new CapsuleShape
                {
                    Bounds = Rectangle.FromLTRB(
                        taskbarBounds.Left + left, taskbarBounds.Top + top,
                        taskbarBounds.Left + right + 1, taskbarBounds.Top + bottom),
                    Radius = radius
                }
            };
            IntPtr dockRegion = CreateInsetRegion(left, top, right + 1, bottom, radius);
            IntPtr finalRegion = dockRegion;

            if (hasTray)
            {
                int trayLeft = Math.Max(0, trayBounds.Left - padding);
                int trayRight = Math.Min(taskbarWidth, trayBounds.Right + padding);
                IntPtr trayRegion = CreateInsetRegion(trayLeft, top, trayRight + 1, bottom, radius);
                shapes.Add(new CapsuleShape
                {
                    Bounds = Rectangle.FromLTRB(
                        taskbarBounds.Left + trayLeft, taskbarBounds.Top + top,
                        taskbarBounds.Left + trayRight + 1, taskbarBounds.Top + bottom),
                    Radius = radius
                });
                IntPtr combined = NativeMethods.CreateRoundRectRgn(0, 0, 1, 1, 0, 0);
                NativeMethods.CombineRgn(
                    combined, dockRegion, trayRegion, NativeMethods.RGN_OR);
                NativeMethods.DeleteObject(dockRegion);
                NativeMethods.DeleteObject(trayRegion);
                finalRegion = combined;
            }

            if (NativeMethods.SetWindowRgn(taskbar, finalRegion, true) == 0)
                NativeMethods.DeleteObject(finalRegion);
            else
            {
                appliedSignature = signature;
                edges.SetShapes(taskbar, shapes);
            }
        }

        /// <summary>
        /// The hard-edged region the taskbar is clipped to: one pixel inside the ideal capsule
        /// (left, top, right, bottom exclusive), so the anti-aliased rim drawn by
        /// <see cref="EdgeSmootherForm"/> can supply the soft outer edge.
        /// </summary>
        internal static IntPtr CreateInsetRegion(int left, int top, int right, int bottom, int radius)
        {
            int diameter = Math.Max(0, radius * 2 - 2);
            return NativeMethods.CreateRoundRectRgn(left + 1, top + 1, right, bottom, diameter, diameter);
        }

        private static bool TryGetTrayBounds(
            IntPtr taskbar,
            Rectangle taskbarBounds,
            out NativeMethods.RECT localBounds)
        {
            IntPtr tray = NativeMethods.FindWindowEx(
                taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            NativeMethods.RECT screenBounds;
            if (tray != IntPtr.Zero && NativeMethods.GetWindowRect(tray, out screenBounds))
            {
                localBounds = new NativeMethods.RECT
                {
                    Left = screenBounds.Left - taskbarBounds.Left,
                    Top = screenBounds.Top - taskbarBounds.Top,
                    Right = screenBounds.Right - taskbarBounds.Left,
                    Bottom = screenBounds.Bottom - taskbarBounds.Top
                };
                return localBounds.Right > localBounds.Left;
            }
            localBounds = new NativeMethods.RECT();
            return false;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            stateTimer.Stop();
            stateTimer.Dispose();
            Restore();
            edges.Dispose();
        }
    }
}
