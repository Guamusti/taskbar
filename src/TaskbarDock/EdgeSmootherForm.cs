using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace TaskbarDock
{
    /// <summary>
    /// A rounded shape of the clipped taskbar, in screen coordinates.
    /// </summary>
    internal struct CapsuleShape
    {
        public Rectangle Bounds;
        public int Radius;
    }

    /// <summary>
    /// Window regions are all-or-nothing per pixel, so the rounded ends of the taskbar
    /// capsules look jagged. The engine clips the taskbar one pixel inside the ideal shape and
    /// this click-through layered window paints the anti-aliased rim in the bar's own colour.
    /// </summary>
    internal sealed class EdgeSmootherForm : Form
    {
        private readonly Timer visibilityTimer;
        private List<CapsuleShape> shapes = new List<CapsuleShape>();
        private IntPtr taskbar;
        private Color rimColor = Color.Empty;
        private Rectangle paintedBounds;
        private bool shown;
        private bool wanted;

        public EdgeSmootherForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            CreateHandle();
            visibilityTimer = new Timer { Interval = 250 };
            visibilityTimer.Tick += delegate { UpdateVisibility(true); };
            visibilityTimer.Start();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;
                const int WS_EX_TOPMOST = 0x00000008;
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT |
                                      WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                return parameters;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        public void SetShapes(IntPtr taskbarWindow, List<CapsuleShape> newShapes)
        {
            taskbar = taskbarWindow;
            shapes = newShapes ?? new List<CapsuleShape>();
            wanted = shapes.Count > 0;
            rimColor = Color.Empty;
            UpdateVisibility(false);
            Repaint();
        }

        public void Clear()
        {
            wanted = false;
            shapes = new List<CapsuleShape>();
            UpdateVisibility(false);
        }

        private void UpdateVisibility(bool resampleColor)
        {
            bool visible = wanted && !IsCovered();
            if (visible && resampleColor && SampleRimColor())
                Repaint();
            if (visible && !shown)
            {
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
                shown = true;
            }
            else if (!visible && shown)
            {
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
                shown = false;
            }
            if (shown)
            {
                // The taskbar is topmost too and rises above us when clicked.
                NativeMethods.SetWindowPos(
                    Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
        }

        /// <summary>
        /// A full-screen video or a window over the taskbar must not get a rim drawn on it.
        /// </summary>
        private bool IsCovered()
        {
            if (taskbar == IntPtr.Zero || !NativeMethods.IsWindowVisible(taskbar) ||
                NativeMethods.IsFullScreenAppRunning())
                return true;
            foreach (CapsuleShape shape in shapes)
            {
                var center = new NativeMethods.POINT
                {
                    X = shape.Bounds.Left + shape.Bounds.Width / 2,
                    Y = shape.Bounds.Top + shape.Bounds.Height / 2
                };
                IntPtr hit = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(center), NativeMethods.GA_ROOT);
                if (hit == taskbar)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Reads the colour of the bar just inside the left end of the first capsule.
        /// Returns true when it changed noticeably.
        /// </summary>
        private bool SampleRimColor()
        {
            if (shapes.Count == 0)
                return false;
            Rectangle bounds = shapes[0].Bounds;
            var point = new Point(bounds.Left + 2, bounds.Top + bounds.Height / 2);
            Color sampled;
            try
            {
                using (var pixel = new Bitmap(1, 1, PixelFormat.Format32bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(pixel))
                        graphics.CopyFromScreen(point, Point.Empty, new Size(1, 1));
                    sampled = pixel.GetPixel(0, 0);
                }
            }
            catch (Exception)
            {
                return false;
            }
            bool changed = rimColor.IsEmpty ||
                           Math.Abs(rimColor.R - sampled.R) + Math.Abs(rimColor.G - sampled.G) +
                           Math.Abs(rimColor.B - sampled.B) > 6;
            if (changed)
                rimColor = Color.FromArgb(255, sampled.R, sampled.G, sampled.B);
            return changed;
        }

        private void Repaint()
        {
            if (shapes.Count == 0)
                return;
            if (rimColor.IsEmpty && !SampleRimColor())
                rimColor = Color.FromArgb(255, 32, 32, 36);

            Rectangle area = shapes[0].Bounds;
            foreach (CapsuleShape shape in shapes)
                area = Rectangle.Union(area, shape.Bounds);
            area.Inflate(2, 2);
            paintedBounds = area;

            using (var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                using (var brush = new SolidBrush(rimColor))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    foreach (CapsuleShape shape in shapes)
                    {
                        var ideal = new RectangleF(
                            shape.Bounds.Left - area.Left, shape.Bounds.Top - area.Top,
                            shape.Bounds.Width, shape.Bounds.Height);
                        using (GraphicsPath path = RoundedRectangle(ideal, shape.Radius))
                        using (var clip = new Region(new RectangleF(0, 0, area.Width, area.Height)))
                        {
                            // Paint only outside what the clipped taskbar already shows; the
                            // anti-aliased path then supplies the soft outer edge.
                            IntPtr inner = FloatingTaskbarEngine.CreateInsetRegion(
                                shape.Bounds.Left - area.Left, shape.Bounds.Top - area.Top,
                                shape.Bounds.Right - area.Left, shape.Bounds.Bottom - area.Top,
                                shape.Radius);
                            try
                            {
                                using (Region inside = Region.FromHrgn(inner))
                                    clip.Exclude(inside);
                            }
                            finally
                            {
                                NativeMethods.DeleteObject(inner);
                            }
                            graphics.SetClip(clip, CombineMode.Replace);
                            graphics.FillPath(brush, path);
                            graphics.ResetClip();
                        }
                    }
                }
                Present(bitmap, area.Location);
            }
        }

        private void Present(Bitmap bitmap, Point location)
        {
            IntPtr screenDc = IntPtr.Zero;
            IntPtr memoryDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
            IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr previous = NativeMethods.SelectObject(memoryDc, hBitmap);
            try
            {
                var destination = new NativeMethods.POINT { X = location.X, Y = location.Y };
                var size = new NativeMethods.SIZE { Width = bitmap.Width, Height = bitmap.Height };
                var source = new NativeMethods.POINT();
                var blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = NativeMethods.AC_SRC_OVER,
                    SourceConstantAlpha = 255,
                    AlphaFormat = NativeMethods.AC_SRC_ALPHA
                };
                NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size,
                    memoryDc, ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
            }
            finally
            {
                NativeMethods.SelectObject(memoryDc, previous);
                NativeMethods.DeleteObject(hBitmap);
                NativeMethods.DeleteDC(memoryDc);
            }
        }

        private static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            if (diameter <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                visibilityTimer.Stop();
                visibilityTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
