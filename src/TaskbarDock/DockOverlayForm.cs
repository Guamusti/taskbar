using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarDock
{
    /// <summary>
    /// Per-pixel-alpha overlay that draws the magnified copies of the native taskbar icons.
    /// It only covers the area of the icons being magnified and is invisible otherwise.
    /// </summary>
    internal sealed class DockOverlayForm : Form
    {
        private const int NativeIconLogicalSize = 24;
        private const float IconMismatchThreshold = 50f;
        private readonly System.Windows.Forms.Timer animationTimer;
        private readonly Thread frameThread;
        private int framePending;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<MagnifiedButton> buttons = new List<MagnifiedButton>();
        private readonly LayeredCanvas canvas = new LayeredCanvas();
        private DockSettings settings;
        private TaskbarSnapshot snapshot;
        private bool engineVisible;
        private volatile bool disposed;
        private volatile bool active;
        private volatile bool windowShown;
        private bool clickThrough;
        private bool suppressed;
        private bool needsRender;
        private double lastFrame;
        private double lastSuppressionCheck = -1;
        private float labelOpacity;
        private int hoveredIndex = -1;
        private int labelIndex = -1;
        private Font labelFont;
        private int labelFontDpi;
        private Bitmap backgroundCapture;
        private Rectangle backgroundBounds;
        private readonly Dictionary<string, Rectangle> capturedRects = new Dictionary<string, Rectangle>();
        private int backgroundVersion;
        private double lastBackgroundCapture = -10;

        public DockOverlayForm(DockSettings initialSettings)
        {
            settings = initialSettings.Clone();
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;

            // The timer only watches for the cursor reaching the dock. While animating, frames
            // come from a thread paced by the display refresh (DwmFlush), which is far steadier
            // than the low-priority WM_TIMER.
            animationTimer = new System.Windows.Forms.Timer { Interval = 15 };
            animationTimer.Tick += delegate
            {
                if (!Animating)
                    AnimateFrame(null, EventArgs.Empty);
            };
            animationTimer.Start();
            frameThread = new Thread(FrameLoop) { IsBackground = true, Name = "TaskbarDock frames" };

            CreateHandle();
            SetClickThrough(true);
            frameThread.Start();
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
                parameters.ExStyle |= NativeMethods.WS_EX_LAYERED | WS_EX_TOOLWINDOW |
                                      WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                return parameters;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE = 0x0021;
            const int MA_NOACTIVATE = 3;
            const int WM_LBUTTONUP = 0x0202;
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(MA_NOACTIVATE);
                return;
            }
            if (m.Msg == WM_LBUTTONUP)
                HandleClick();
            base.WndProc(ref m);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        public void Apply(DockSettings newSettings)
        {
            settings = newSettings.Clone();
            foreach (MagnifiedButton button in buttons)
                button.ReleaseIcon();
            needsRender = true;
        }

        public void SetEngineVisible(bool visible)
        {
            engineVisible = visible;
        }

        public void UpdateSnapshot(TaskbarSnapshot newSnapshot)
        {
            if (disposed || newSnapshot == null)
                return;
            snapshot = newSnapshot;

            // Several buttons can share an AppId when "never combine" is enabled,
            // so the key also includes the occurrence number.
            var previous = new Dictionary<string, MagnifiedButton>();
            foreach (MagnifiedButton button in buttons)
                previous[button.Key] = button;

            var occurrences = new Dictionary<string, int>();
            var next = new List<MagnifiedButton>();
            foreach (TaskbarButtonInfo info in newSnapshot.Buttons)
            {
                int occurrence;
                occurrences.TryGetValue(info.Id, out occurrence);
                occurrences[info.Id] = occurrence + 1;
                string key = info.Id + "#" + occurrence;

                MagnifiedButton button;
                if (previous.TryGetValue(key, out button))
                    previous.Remove(key);
                else
                    button = new MagnifiedButton { Key = key, Scale = 1f };
                button.Info = info;
                next.Add(button);
            }

            foreach (MagnifiedButton removed in previous.Values)
                removed.Dispose();
            buttons.Clear();
            buttons.AddRange(next);
            if (hoveredIndex >= buttons.Count)
                hoveredIndex = -1;
            if (labelIndex >= buttons.Count)
                labelIndex = -1;
            needsRender = windowShown;
        }

        private int BaseIconSize
        {
            get
            {
                if (settings.IconSize > 0)
                    return settings.IconSize;
                int dpi = snapshot != null ? snapshot.Dpi : 96;
                return (int)Math.Round(NativeIconLogicalSize * dpi / 96f);
            }
        }

        private float DpiScale
        {
            get { return snapshot != null ? snapshot.Dpi / 96f : 1f; }
        }

        private bool Animating
        {
            get { return windowShown || active; }
        }

        private void FrameLoop()
        {
            while (!disposed)
            {
                if (!Animating)
                {
                    Thread.Sleep(20);
                    continue;
                }
                if (NativeMethods.DwmFlush() != 0)
                    Thread.Sleep(8);
                if (Interlocked.Exchange(ref framePending, 1) != 0)
                    continue;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        framePending = 0;
                        AnimateFrame(null, EventArgs.Empty);
                    });
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }
        }

        private void AnimateFrame(object sender, EventArgs args)
        {
            if (disposed)
                return;
            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.05, Math.Max(0.001, now - lastFrame));
            lastFrame = now;

            if (now - lastSuppressionCheck > 0.5)
            {
                lastSuppressionCheck = now;
                suppressed = NativeMethods.IsFullScreenAppRunning() ||
                             snapshot == null || !NativeMethods.IsWindowVisible(snapshot.Taskbar) ||
                             IsTaskbarCovered();
                if (suppressed)
                    DiscardBackground();
            }

            bool allowed = engineVisible && settings.Enabled && !suppressed && buttons.Count > 0;
            Point cursor = Cursor.Position;
            active = allowed && IsInDockZone(cursor);
            hoveredIndex = active ? FindHovered(cursor.X) : -1;

            // Exponential easing based on elapsed time keeps the speed independent of the
            // timer resolution. Higher settings mean a shorter time constant.
            float timeConstant = 0.6f / Math.Max(2, settings.AnimationSpeed);
            float response = 1f - (float)Math.Exp(-dt / timeConstant);
            bool changed = false;
            for (int index = 0; index < buttons.Count; index++)
            {
                MagnifiedButton button = buttons[index];
                float target = active ? CalculateTargetScale(button, cursor.X) : 1f;
                float oldScale = button.Scale;
                button.Scale += (target - button.Scale) * response;
                if (Math.Abs(button.Scale - target) < 0.002f)
                    button.Scale = target;
                changed |= Math.Abs(oldScale - button.Scale) > 0.0005f;
            }

            bool wantLabel = settings.ShowLabels && hoveredIndex >= 0;
            if (wantLabel && labelIndex != hoveredIndex)
            {
                labelIndex = hoveredIndex;
                labelOpacity = Math.Min(labelOpacity, 0.35f);
                changed = true;
            }
            float labelTarget = wantLabel ? 1f : 0f;
            float oldOpacity = labelOpacity;
            labelOpacity += (labelTarget - labelOpacity) * Math.Min(1f, response * 1.4f);
            if (Math.Abs(labelOpacity - labelTarget) < 0.01f)
                labelOpacity = labelTarget;
            changed |= Math.Abs(oldOpacity - labelOpacity) > 0.001f;
            if (labelOpacity <= 0f)
                labelIndex = -1;

            UpdateClickThrough(cursor);
            // Keep the background fresh, since whatever is behind a transparent taskbar can
            // change. Never while the overlay is visible: screen copies include it, and the
            // enlarged icons would feed back into their own background.
            if (allowed && !windowShown && now - lastBackgroundCapture > 0.5)
                CaptureBackground(cursor, now);
            if (changed || needsRender)
            {
                needsRender = false;
                Render();
            }
        }

        /// <summary>
        /// Remembers how the taskbar looks behind the buttons.
        /// The native icon is still painted there, so each enlarged icon is drawn over a
        /// copy of its button with the native icon erased. Without it, the small original
        /// shows through the transparent parts of the large one.
        /// </summary>
        private bool CaptureBackground(Point cursor, double now)
        {
            lastBackgroundCapture = now;
            if (snapshot == null || buttons.Count == 0)
                return false;
            RectangleF first = buttons[0].Info.ScreenBounds;
            RectangleF last = buttons[buttons.Count - 1].Info.ScreenBounds;
            Rectangle area = Rectangle.Round(RectangleF.FromLTRB(
                first.Left, Math.Min(first.Top, last.Top), last.Right, Math.Max(first.Bottom, last.Bottom)));
            // One extra column at each end gives the outer buttons a neighbour to blend from.
            area.Inflate(2, 0);
            // The hover highlight inside a button does not matter: patches are rebuilt from the
            // columns between buttons.
            if (area.Width <= 0 || area.Height <= 0)
                return false;
            // Only capture while the taskbar itself is what is on screen there, not a window,
            // menu or full-screen app covering it.
            if (IsTaskbarCovered())
            {
                DiscardBackground();
                return false;
            }

            var capture = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(capture))
                    graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
            }
            catch (Exception)
            {
                capture.Dispose();
                return false;
            }
            if (backgroundCapture != null)
                backgroundCapture.Dispose();
            backgroundCapture = capture;
            backgroundBounds = area;
            backgroundVersion++;
            capturedRects.Clear();
            foreach (MagnifiedButton button in buttons)
                capturedRects[button.Key] = Rectangle.Round(button.Info.ScreenBounds);
            return true;
        }

        /// <summary>
        /// True when something else (a full-screen video, a menu, a window) is on screen where
        /// the taskbar buttons are. Magnifying then would draw icons over that content.
        /// </summary>
        private bool IsTaskbarCovered()
        {
            if (snapshot == null)
                return true;
            foreach (MagnifiedButton button in buttons)
            {
                RectangleF bounds = button.Info.ScreenBounds;
                var center = new NativeMethods.POINT
                {
                    X = (int)(bounds.Left + bounds.Width / 2),
                    Y = (int)(bounds.Top + bounds.Height / 2)
                };
                IntPtr hit = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(center), NativeMethods.GA_ROOT);
                if (hit != snapshot.Taskbar && hit != Handle)
                    return true;
            }
            return false;
        }

        private void DiscardBackground()
        {
            if (backgroundCapture == null)
                return;
            backgroundCapture.Dispose();
            backgroundCapture = null;
            capturedRects.Clear();
            backgroundVersion++;
        }

        private Bitmap GetCleanPatch(MagnifiedButton button)
        {
            if (backgroundCapture == null)
                return null;
            // The bounds Windows reports can shift a pixel or two while a button is hovered,
            // so match the capture by button rather than requiring an identical layout.
            Rectangle native;
            if (!capturedRects.TryGetValue(button.Key, out native))
                return null;
            Rectangle current = Rectangle.Round(button.Info.ScreenBounds);
            if (Math.Abs(current.Left - native.Left) > 3 || Math.Abs(current.Width - native.Width) > 3)
                return null;
            if (button.Patch != null && button.PatchVersion == backgroundVersion)
                return button.Patch;

            // Read the button plus the column just outside each side of it.
            var sourceRect = new Rectangle(native.Left - 1, native.Top, native.Width + 2, native.Height);
            if (!backgroundBounds.Contains(sourceRect) || native.Width < 8 || native.Height < 8)
                return null;
            int width = native.Width;
            int height = native.Height;
            int sourceWidth = sourceRect.Width;
            var original = new int[sourceWidth * height];
            BitmapData source = backgroundCapture.LockBits(
                new Rectangle(sourceRect.Left - backgroundBounds.Left, sourceRect.Top - backgroundBounds.Top,
                    sourceWidth, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int row = 0; row < height; row++)
                    System.Runtime.InteropServices.Marshal.Copy(
                        source.Scan0 + row * source.Stride, original, row * sourceWidth, sourceWidth);
            }
            finally
            {
                backgroundCapture.UnlockBits(source);
            }

            // Rebuild the whole button from the background just outside it. That removes the
            // native icon and also the rounded highlight Windows paints behind the active or
            // hovered button, which otherwise frames the enlarged icon.
            var pixels = new int[width * height];
            for (int y = 0; y < height; y++)
            {
                int left = original[y * sourceWidth];
                int right = original[y * sourceWidth + sourceWidth - 1];
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = Lerp(left, right, (x + 1) / (float)(width + 1));
            }

            int iconSize = (int)Math.Round(NativeIconLogicalSize * DpiScale);
            button.ReleaseBadge();
            button.Badge = ExtractBadge(button, original, sourceWidth, pixels, width, height, iconSize);

            // Keep the running indicator: below the icon, copy back only what clearly stands
            // out from the rebuilt background, so the faint highlight around it stays erased.
            int indicatorTop = Math.Min(height - 1, (height + iconSize) / 2 + (int)Math.Ceiling(2 * DpiScale));
            for (int y = indicatorTop; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int rebuilt = pixels[y * width + x];
                    int captured = original[y * sourceWidth + x + 1];
                    float weight = Math.Max(0f, Math.Min(1f, (ColorDistance(rebuilt, captured) - 18) / 40f));
                    if (weight > 0f)
                        pixels[y * width + x] = Lerp(rebuilt, captured, weight);
                }
            }
            for (int index = 0; index < pixels.Length; index++)
                pixels[index] |= unchecked((int)0xFF000000);

            var patch = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData target = patch.LockBits(
                new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int row = 0; row < height; row++)
                    System.Runtime.InteropServices.Marshal.Copy(
                        pixels, row * width, target.Scan0 + row * target.Stride, width);
            }
            finally
            {
                patch.UnlockBits(target);
            }
            button.ReleasePatch();
            button.Patch = patch;
            button.PatchRect = native;
            button.PatchVersion = backgroundVersion;
            return patch;
        }

        /// <summary>
        /// Windows paints notification badges (the WhatsApp counter, for example) on top of the
        /// native icon. Recover them by comparing the capture with what the button would look
        /// like with only our copy of the icon, so they can be drawn on the enlarged icon.
        /// </summary>
        private Bitmap ExtractBadge(MagnifiedButton button, int[] original, int sourceWidth,
            int[] background, int width, int height, int iconSize)
        {
            Bitmap icon = button.GetIcon(128);
            if (icon == null)
                return null;

            int iconLeft = (width - iconSize) / 2;
            int iconTop = (height - iconSize) / 2;
            var expected = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var expectedPixels = new int[width * height];
            try
            {
                var opaque = new int[background.Length];
                for (int index = 0; index < opaque.Length; index++)
                    opaque[index] = background[index] | unchecked((int)0xFF000000);
                SetPixels(expected, opaque);
                using (Graphics graphics = Graphics.FromImage(expected))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(icon, new Rectangle(iconLeft, iconTop, iconSize, iconSize));
                }
                GetPixels(expected, expectedPixels);
            }
            finally
            {
                expected.Dispose();
            }

            // Make sure our icon really is the one Windows shows. Some apps (those hosted by
            // explorer.exe, for example) resolve to another app's icon; then the native icon
            // itself is lifted from the capture and enlarged instead. The top right quadrant is
            // skipped because a badge may be there.
            long totalDistance = 0;
            int samples = 0;
            for (int y = iconTop; y < iconTop + iconSize; y++)
            {
                for (int x = iconLeft; x < iconLeft + iconSize; x++)
                {
                    if (x >= width / 2 && y < height / 2)
                        continue;
                    totalDistance += ColorDistance(original[y * sourceWidth + x + 1], expectedPixels[y * width + x]);
                    samples++;
                }
            }
            button.IconMismatch = samples == 0 ? 0f : totalDistance / (float)samples;
            button.ReleaseNativeIcon();
            if (button.IconMismatch > IconMismatchThreshold)
            {
                button.NativeIcon = LiftNativeIcon(original, sourceWidth, background, width, height, iconSize);
                return null;
            }

            // Badges sit on the top right corner of the icon.
            var badge = new int[width * height];
            int strongPixels = 0;
            int minX = width / 2;
            int maxY = height / 2 + iconSize / 8;
            for (int y = 0; y < maxY; y++)
            {
                for (int x = minX; x < width; x++)
                {
                    int captured = original[y * sourceWidth + x + 1];
                    int distance = ColorDistance(captured, expectedPixels[y * width + x]);
                    float weight = Math.Max(0f, Math.Min(1f, (distance - 45) / 45f));
                    if (weight <= 0f)
                        continue;
                    badge[y * width + x] = ((int)(weight * 255) << 24) | (captured & 0xFFFFFF);
                    if (weight >= 1f)
                        strongPixels++;
                }
            }
            // A badge is a solid blob; a few stray pixels are only anti-aliasing differences.
            int minimumArea = Math.Max(12, iconSize * iconSize / 12);
            if (strongPixels < minimumArea)
                return null;
            var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            SetPixels(result, badge);
            return result;
        }

        /// <summary>
        /// Cuts the native icon out of the capture, using its difference from the rebuilt
        /// background as transparency. Returns a square bitmap covering the native icon.
        /// </summary>
        private static Bitmap LiftNativeIcon(int[] original, int sourceWidth, int[] background,
            int width, int height, int iconSize)
        {
            int margin = Math.Max(1, iconSize / 12);
            int side = iconSize + margin * 2;
            int left = (width - iconSize) / 2 - margin;
            int top = (height - iconSize) / 2 - margin;
            var pixels = new int[side * side];
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    int sx = left + x;
                    int sy = top + y;
                    if (sx < 0 || sy < 0 || sx >= width || sy >= height)
                        continue;
                    int captured = original[sy * sourceWidth + sx + 1];
                    int distance = ColorDistance(captured, background[sy * width + sx]);
                    float alpha = Math.Max(0f, Math.Min(1f, (distance - 14) / 28f));
                    if (alpha > 0f)
                        pixels[y * side + x] = ((int)(alpha * 255) << 24) | (captured & 0xFFFFFF);
                }
            }
            var result = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            SetPixels(result, pixels);
            return result;
        }

        private static void SetPixels(Bitmap bitmap, int[] pixels)
        {
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int row = 0; row < bitmap.Height; row++)
                    System.Runtime.InteropServices.Marshal.Copy(
                        pixels, row * bitmap.Width, data.Scan0 + row * data.Stride, bitmap.Width);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private static void GetPixels(Bitmap bitmap, int[] pixels)
        {
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int row = 0; row < bitmap.Height; row++)
                    System.Runtime.InteropServices.Marshal.Copy(
                        data.Scan0 + row * data.Stride, pixels, row * bitmap.Width, bitmap.Width);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private static int ColorDistance(int a, int b)
        {
            return Math.Max(Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF)),
                   Math.Max(Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)),
                            Math.Abs((a & 0xFF) - (b & 0xFF))));
        }

        private static int Lerp(int from, int to, float t)
        {
            int r = (int)(((from >> 16) & 0xFF) + (((to >> 16) & 0xFF) - ((from >> 16) & 0xFF)) * t);
            int g = (int)(((from >> 8) & 0xFF) + (((to >> 8) & 0xFF) - ((from >> 8) & 0xFF)) * t);
            int b = (int)((from & 0xFF) + ((to & 0xFF) - (from & 0xFF)) * t);
            return (r << 16) | (g << 8) | b;
        }

        private bool IsInDockZone(Point cursor)
        {
            RectangleF first = buttons[0].Info.ScreenBounds;
            RectangleF last = buttons[buttons.Count - 1].Info.ScreenBounds;
            float top = first.Top;
            if (active)
            {
                // While magnified, the zone grows upwards to include the enlarged icons,
                // so moving onto the top of a big icon does not collapse the dock.
                float maxScale = 1f;
                foreach (MagnifiedButton button in buttons)
                    maxScale = Math.Max(maxScale, button.Scale);
                float iconBottom = first.Top + first.Height / 2f + BaseIconSize / 2f;
                top = Math.Min(top, iconBottom - BaseIconSize * maxScale - 6 * DpiScale);
            }
            float bottom = snapshot.TaskbarBounds.Bottom;
            return cursor.X >= first.Left && cursor.X < last.Right &&
                   cursor.Y >= top && cursor.Y < bottom;
        }

        private int FindHovered(int x)
        {
            for (int index = 0; index < buttons.Count; index++)
            {
                RectangleF bounds = buttons[index].Info.ScreenBounds;
                if (x >= bounds.Left && x < bounds.Right)
                    return index;
            }
            return -1;
        }

        private float CalculateTargetScale(MagnifiedButton button, int cursorX)
        {
            RectangleF bounds = button.Info.ScreenBounds;
            float spacing = Math.Max(1f, bounds.Width);
            float radius = spacing * Math.Max(1, settings.Reach);
            float distance = Math.Abs(cursorX - (bounds.Left + bounds.Width / 2f));
            if (distance >= radius)
                return 1f;
            // Raised cosine falloff: smooth like the macOS Dock, no steps between neighbours.
            float influence = (1f + (float)Math.Cos(Math.PI * distance / radius)) / 2f;
            return 1f + settings.Magnification / 100f * influence;
        }

        private void UpdateClickThrough(Point cursor)
        {
            // Over the native taskbar every click, right click, drag and hover preview goes to
            // Windows. Above it, the enlarged part of an icon is clickable here.
            bool overNativeBar = snapshot == null || cursor.Y >= snapshot.TaskbarBounds.Top;
            bool wanted = !active || overNativeBar;
            if (wanted != clickThrough)
                SetClickThrough(wanted);
        }

        private void SetClickThrough(bool enabled)
        {
            clickThrough = enabled;
            long style = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            style = enabled
                ? style | NativeMethods.WS_EX_TRANSPARENT
                : style & ~(long)NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
        }

        private void HandleClick()
        {
            if (hoveredIndex < 0 || hoveredIndex >= buttons.Count)
                return;
            TaskbarButtonInfo info = buttons[hoveredIndex].Info;
            // Invoke can take a moment while Explorer reacts; keep the animation fluid.
            ThreadPool.QueueUserWorkItem(delegate { TaskbarButtonSource.Invoke(info); });
        }

        private void Render()
        {
            if (snapshot == null)
                return;
            int baseSize = BaseIconSize;
            float dpiScale = DpiScale;

            var drawn = new List<MagnifiedButton>();
            RectangleF dirty = RectangleF.Empty;
            foreach (MagnifiedButton button in buttons)
            {
                button.DrawBounds = RectangleF.Empty;
                if (button.Scale <= 1.004f)
                    continue;
                RectangleF native = button.Info.ScreenBounds;
                float size = baseSize * button.Scale;
                float bottom = native.Top + native.Height / 2f + baseSize / 2f;
                float centerX = native.Left + native.Width / 2f;
                button.DrawBounds = new RectangleF(centerX - size / 2f, bottom - size, size, size);
                drawn.Add(button);
                RectangleF area = RectangleF.Union(button.DrawBounds, native);
                area.Inflate(size * 0.12f, size * 0.12f);
                dirty = dirty.IsEmpty ? area : RectangleF.Union(dirty, area);
            }

            LabelLayout label = null;
            if (labelIndex >= 0 && labelIndex < buttons.Count && labelOpacity > 0f)
            {
                label = LayoutLabel(buttons[labelIndex], baseSize, dpiScale);
                if (label != null)
                {
                    RectangleF area = label.Bounds;
                    area.Inflate(2, 2);
                    area.Height += label.PointerSize;
                    dirty = dirty.IsEmpty ? area : RectangleF.Union(dirty, area);
                }
            }

            if (dirty.IsEmpty)
            {
                HideOverlay();
                return;
            }

            // Present a fixed area around the whole dock instead of a box that follows the icons.
            // Resizing the layered window every frame makes DWM show the new pixels in the old
            // size for a moment, which clipped the growing side of the icon and caused stutter.
            RectangleF first = buttons[0].Info.ScreenBounds;
            RectangleF last = buttons[buttons.Count - 1].Info.ScreenBounds;
            float maximumIcon = baseSize * (1 + settings.Magnification / 100f);
            float sideRoom = Math.Max(maximumIcon, 170 * dpiScale);
            RectangleF fixedArea = RectangleF.FromLTRB(
                first.Left - sideRoom,
                snapshot.TaskbarBounds.Top - maximumIcon - 48 * dpiScale,
                last.Right + sideRoom,
                snapshot.TaskbarBounds.Bottom);
            Rectangle region = Rectangle.Intersect(
                Rectangle.Round(RectangleF.Union(fixedArea, dirty)),
                Screen.FromRectangle(snapshot.TaskbarBounds).Bounds);
            if (region.Width <= 0 || region.Height <= 0)
            {
                HideOverlay();
                return;
            }

            drawn.Sort((a, b) => a.Scale.CompareTo(b.Scale));
            Graphics graphics = canvas.Begin(region.Width, region.Height);
            graphics.TranslateTransform(-region.Left, -region.Top);
            foreach (MagnifiedButton button in drawn)
                DrawPatch(graphics, button);
            foreach (MagnifiedButton button in drawn)
            {
                DrawIcon(graphics, button, baseSize);
                DrawBadge(graphics, button);
            }
            if (label != null)
                DrawLabel(graphics, label);
            graphics.ResetTransform();
            canvas.Present(Handle, region.Location);

            if (!windowShown)
            {
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
                NativeMethods.SetWindowPos(
                    Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                windowShown = true;
            }
        }

        private void HideOverlay()
        {
            if (!windowShown)
                return;
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
            windowShown = false;
        }

        private void DrawPatch(Graphics graphics, MagnifiedButton button)
        {
            Bitmap patch = GetCleanPatch(button);
            if (patch == null)
                return;
            // Fade the patch in over the first few percent of growth so the swap is invisible.
            float opacity = Math.Min(1f, (button.Scale - 1f) / 0.04f);
            InterpolationMode previousInterpolation = graphics.InterpolationMode;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            using (var attributes = new ImageAttributes())
            {
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
                graphics.DrawImage(patch, button.PatchRect, 0, 0, patch.Width, patch.Height,
                    GraphicsUnit.Pixel, attributes);
            }
            graphics.InterpolationMode = previousInterpolation;
        }

        private void DrawBadge(Graphics graphics, MagnifiedButton button)
        {
            Bitmap badge = button.Badge;
            if (badge == null || button.Patch == null || button.PatchVersion != backgroundVersion)
                return;
            // Map the native icon square of the badge layer onto the enlarged icon.
            int iconSize = (int)Math.Round(NativeIconLogicalSize * DpiScale);
            float scale = button.DrawBounds.Width / iconSize;
            float iconLeft = (badge.Width - iconSize) / 2;
            float iconTop = (badge.Height - iconSize) / 2;
            graphics.DrawImage(badge, new RectangleF(
                button.DrawBounds.Left - iconLeft * scale,
                button.DrawBounds.Top - iconTop * scale,
                badge.Width * scale,
                badge.Height * scale));
        }

        private void DrawIcon(Graphics graphics, MagnifiedButton button, int baseSize)
        {
            RectangleF bounds = button.DrawBounds;
            int maximumSize = (int)Math.Ceiling(baseSize * (1 + settings.Magnification / 100f));
            Bitmap icon = button.GetIcon(maximumSize > 128 ? 256 : 128);
            RectangleF target = bounds;
            if (button.NativeIcon != null && backgroundCapture != null && button.PatchVersion == backgroundVersion)
            {
                // The lifted native icon has a margin around the icon square; scale it too.
                icon = button.NativeIcon;
                int iconSize = (int)Math.Round(NativeIconLogicalSize * DpiScale);
                float margin = (icon.Width - iconSize) / 2f * bounds.Width / iconSize;
                target = RectangleF.Inflate(bounds, margin, margin);
            }
            if (icon == null)
                return;

            // Soft contact shadow under the icon.
            var shadow = new RectangleF(
                bounds.Left + bounds.Width * 0.14f,
                bounds.Bottom - bounds.Height * 0.07f,
                bounds.Width * 0.72f,
                bounds.Height * 0.16f);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(shadow);
                using (var brush = new PathGradientBrush(path))
                {
                    brush.CenterColor = Color.FromArgb(70, 0, 0, 0);
                    brush.SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) };
                    graphics.FillPath(brush, path);
                }
            }

            using (var attributes = new ImageAttributes())
            {
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                graphics.DrawImage(
                    icon,
                    new[]
                    {
                        new PointF(target.Left, target.Top),
                        new PointF(target.Right, target.Top),
                        new PointF(target.Left, target.Bottom)
                    },
                    new RectangleF(0, 0, icon.Width, icon.Height),
                    GraphicsUnit.Pixel,
                    attributes);
            }
        }

        private sealed class LabelLayout
        {
            public string Text;
            public RectangleF Bounds;
            public float PointerX;
            public float PointerSize;
            public float Radius;
        }

        private LabelLayout LayoutLabel(MagnifiedButton button, int baseSize, float dpiScale)
        {
            string text = button.Info.Name;
            if (string.IsNullOrEmpty(text))
                return null;
            if (text.Length > 48)
                text = text.Substring(0, 45).TrimEnd() + "…";

            Font font = GetLabelFont();
            SizeF textSize;
            using (var measure = Graphics.FromHwnd(IntPtr.Zero))
                textSize = measure.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);

            RectangleF native = button.Info.ScreenBounds;
            float iconBottom = native.Top + native.Height / 2f + baseSize / 2f;
            float iconTop = button.DrawBounds.IsEmpty
                ? iconBottom - baseSize
                : button.DrawBounds.Top;
            float paddingX = 11 * dpiScale;
            float paddingY = 5 * dpiScale;
            float pointer = 5 * dpiScale;
            float width = textSize.Width + paddingX * 2;
            float height = textSize.Height + paddingY * 2;
            float centerX = native.Left + native.Width / 2f;
            Rectangle screen = Screen.FromRectangle(snapshot.TaskbarBounds).Bounds;
            float left = Math.Max(screen.Left + 6, Math.Min(screen.Right - width - 6, centerX - width / 2f));
            float top = iconTop - 9 * dpiScale - pointer - height;
            return new LabelLayout
            {
                Text = text,
                Bounds = new RectangleF(left, top, width, height),
                PointerX = centerX,
                PointerSize = pointer,
                Radius = 7 * dpiScale
            };
        }

        private Font GetLabelFont()
        {
            int dpi = snapshot != null ? snapshot.Dpi : 96;
            if (labelFont == null || labelFontDpi != dpi)
            {
                if (labelFont != null)
                    labelFont.Dispose();
                labelFont = new Font("Segoe UI", 13f * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
                labelFontDpi = dpi;
            }
            return labelFont;
        }

        private void DrawLabel(Graphics graphics, LabelLayout label)
        {
            int alpha = (int)(255 * labelOpacity);
            RectangleF bounds = label.Bounds;
            using (GraphicsPath path = RoundedRectangle(bounds, label.Radius))
            {
                float pointerX = Math.Max(bounds.Left + label.Radius + label.PointerSize,
                    Math.Min(bounds.Right - label.Radius - label.PointerSize, label.PointerX));
                path.AddPolygon(new[]
                {
                    new PointF(pointerX - label.PointerSize, bounds.Bottom - 0.5f),
                    new PointF(pointerX + label.PointerSize, bounds.Bottom - 0.5f),
                    new PointF(pointerX, bounds.Bottom + label.PointerSize)
                });
                path.FillMode = FillMode.Winding;
                using (var background = new SolidBrush(Color.FromArgb(alpha * 232 / 255, 36, 36, 40)))
                    graphics.FillPath(background, path);
            }
            using (GraphicsPath outline = RoundedRectangle(bounds, label.Radius))
            using (var border = new Pen(Color.FromArgb(alpha * 46 / 255, 255, 255, 255), 1f))
                graphics.DrawPath(border, outline);

            // ClearType cannot be composited on a transparent surface; grayscale AA can.
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var textBrush = new SolidBrush(Color.FromArgb(alpha, 245, 245, 247)))
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags |= StringFormatFlags.NoWrap;
                graphics.DrawString(label.Text, GetLabelFont(), textBrush, bounds, format);
            }
        }

        private static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            disposed = true;
            if (disposing)
            {
                animationTimer.Stop();
                animationTimer.Dispose();
                foreach (MagnifiedButton button in buttons)
                    button.Dispose();
                buttons.Clear();
                canvas.Dispose();
                if (backgroundCapture != null)
                    backgroundCapture.Dispose();
                if (labelFont != null)
                    labelFont.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class MagnifiedButton : IDisposable
        {
            public string Key;
            public TaskbarButtonInfo Info;
            public float Scale;
            public RectangleF DrawBounds;
            public Bitmap Patch;
            public Rectangle PatchRect;
            public Bitmap Badge;
            public Bitmap NativeIcon;
            public float IconMismatch;
            public int PatchVersion;
            private Bitmap icon;
            private int iconSize;
            private bool loadFailed;

            public Bitmap GetIcon(int size)
            {
                if ((icon == null || iconSize < size) && !loadFailed)
                {
                    ReleaseIcon();
                    try
                    {
                        icon = ShellIconLoader.Load(Info.Id, Info.Name, size);
                        iconSize = size;
                    }
                    catch (Exception exception)
                    {
                        AppTrace.Log("Icon load failed for " + Info.Id + ": " + exception.Message);
                        loadFailed = true;
                    }
                }
                return icon;
            }

            public void ReleaseIcon()
            {
                if (icon != null)
                {
                    icon.Dispose();
                    icon = null;
                }
                iconSize = 0;
            }

            public void ReleasePatch()
            {
                if (Patch != null)
                {
                    Patch.Dispose();
                    Patch = null;
                }
            }

            public void ReleaseBadge()
            {
                if (Badge != null)
                {
                    Badge.Dispose();
                    Badge = null;
                }
            }

            public void ReleaseNativeIcon()
            {
                if (NativeIcon != null)
                {
                    NativeIcon.Dispose();
                    NativeIcon = null;
                }
            }

            public void Dispose()
            {
                ReleaseIcon();
                ReleasePatch();
                ReleaseBadge();
                ReleaseNativeIcon();
            }
        }
    }
}
