using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace TaskbarDock
{
    /// <summary>
    /// A macOS style popover that drops from the menu bar: a rounded, translucent dark panel
    /// made of simple widgets stacked vertically. Closes when it loses focus.
    /// </summary>
    internal sealed class PopoverForm : Form
    {
        public const int LogicalWidth = 320;
        private readonly LayeredCanvas canvas = new LayeredCanvas();
        private readonly List<PopoverWidget> widgets = new List<PopoverWidget>();
        private readonly Timer fadeTimer;
        private readonly Timer animationTimer;
        private readonly float scale;
        private Point anchor;
        private PopoverWidget hovered;
        private PopoverWidget captured;
        private readonly System.Diagnostics.Stopwatch fadeClock = new System.Diagnostics.Stopwatch();
        private float progress;
        private float closingFrom;
        private bool closing;

        public PopoverForm(float scale)
        {
            this.scale = scale;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            KeyPreview = true;
            Fonts = new PopoverFonts(scale);
            fadeTimer = new Timer { Interval = 15 };
            fadeTimer.Tick += delegate { StepFade(); };
            animationTimer = new Timer { Interval = 15 };
            animationTimer.Tick += delegate
            {
                bool changed = false;
                foreach (PopoverWidget widget in widgets.ToArray())
                    changed |= widget.Animate();
                if (changed)
                    Invalidate();
            };
        }

        public PopoverFonts Fonts { get; private set; }

        /// <summary>Width in logical pixels; menus are narrower than panels.</summary>
        public int PanelWidth = LogicalWidth;

        public float Scale
        {
            get { return scale; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_TOPMOST = 0x00000008;
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return parameters;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        public T Add<T>(T widget) where T : PopoverWidget
        {
            widget.Owner = this;
            widgets.Add(widget);
            return widget;
        }

        public void Remove(PopoverWidget widget)
        {
            widgets.Remove(widget);
            if (hovered == widget)
                hovered = null;
        }

        public void Clear()
        {
            widgets.Clear();
            hovered = null;
            captured = null;
        }

        /// <summary>Shows the popover with its top edge at <paramref name="top"/>, centred
        /// under <paramref name="x"/> but kept inside the screen.</summary>
        public void ShowAt(int x, int top)
        {
            anchor = new Point(x, top);
            progress = 0f;
            fadeClock.Restart();
            Invalidate();
            Show();
            Activate();
            fadeTimer.Start();
            animationTimer.Start();
        }

        public new void Invalidate()
        {
            if (IsDisposed)
                return;
            float padding = 14 * scale;
            float width = PanelWidth * scale;
            float y = padding;
            using (Graphics measure = Graphics.FromHwnd(IntPtr.Zero))
            {
                foreach (PopoverWidget widget in widgets)
                {
                    float height = widget.Measure(measure, width - padding * 2);
                    widget.Bounds = new RectangleF(padding, y, width - padding * 2, height);
                    y += height + widget.SpacingAfter * scale;
                }
            }
            if (widgets.Count > 0)
                y -= widgets[widgets.Count - 1].SpacingAfter * scale;
            int totalHeight = (int)Math.Ceiling(y + padding);
            int totalWidth = (int)Math.Ceiling(width);

            Rectangle screen = Screen.FromPoint(anchor).WorkingArea;
            int left = Math.Max(screen.Left + 8, Math.Min(screen.Right - totalWidth - 8, anchor.X - totalWidth / 2));
            Location = new Point(left, anchor.Y);
            Size = new Size(totalWidth, totalHeight);

            Graphics graphics = canvas.Begin(totalWidth, totalHeight);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var panel = new RectangleF(0.5f, 0.5f, totalWidth - 1, totalHeight - 1);
            using (GraphicsPath path = Rounded(panel, 12 * scale))
            {
                using (var background = new SolidBrush(Color.FromArgb(244, 34, 34, 38)))
                    graphics.FillPath(background, path);
                using (var border = new Pen(Color.FromArgb(60, 255, 255, 255)))
                    graphics.DrawPath(border, path);
            }
            foreach (PopoverWidget widget in widgets)
                widget.Paint(graphics, widget == hovered);
            PresentAnimated();
        }

        /// <summary>
        /// Opening slides the panel down a few pixels while it fades in, with an ease-out
        /// curve; closing fades it out. Both take a fraction of a second.
        /// </summary>
        private void StepFade()
        {
            float duration = closing ? 0.12f : 0.18f;
            float t = Math.Min(1f, (float)fadeClock.Elapsed.TotalSeconds / duration);
            float eased = 1f - (float)Math.Pow(1f - t, 3);
            progress = closing ? closingFrom * (1f - eased) : eased;
            PresentAnimated();
            if (t < 1f)
                return;
            fadeTimer.Stop();
            if (closing)
                Close();
        }

        private void PresentAnimated()
        {
            int offset = (int)Math.Round((1f - progress) * -8 * scale);
            canvas.Present(Handle, new Point(Location.X, Location.Y + offset), (byte)Math.Round(255 * progress));
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            CloseSoon();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                CloseSoon();
                return;
            }
            foreach (PopoverWidget widget in widgets)
                if (widget.HandleKeyDown(e))
                    return;
            base.OnKeyDown(e);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            foreach (PopoverWidget widget in widgets)
                if (widget.HandleKeyPress(e.KeyChar))
                    return;
            base.OnKeyPress(e);
        }

        public void CloseSoon()
        {
            if (closing || IsDisposed)
                return;
            closing = true;
            closingFrom = progress;
            fadeClock.Restart();
            fadeTimer.Start();
        }

        private PopoverWidget WidgetAt(Point point)
        {
            foreach (PopoverWidget widget in widgets)
                if (widget.Bounds.Contains(point))
                    return widget;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (captured != null)
            {
                captured.OnDrag(e.Location);
                return;
            }
            PopoverWidget widget = WidgetAt(e.Location);
            bool changed = widget != hovered;
            if (widget != null && widget.OnHover(e.Location))
                changed = true;
            hovered = widget;
            if (changed)
                Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hovered != null)
            {
                hovered.OnHover(new Point(-1, -1));
                hovered = null;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
                return;
            PopoverWidget widget = WidgetAt(e.Location);
            if (widget == null)
                return;
            if (widget.OnPress(e.Location))
                captured = widget;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (captured != null)
            {
                captured.OnRelease();
                captured = null;
                return;
            }
            if (e.Button != MouseButtons.Left)
                return;
            PopoverWidget widget = WidgetAt(e.Location);
            if (widget != null)
                widget.OnClick(e.Location);
        }

        internal static GraphicsPath Rounded(RectangleF rect, float radius)
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
                fadeTimer.Dispose();
                animationTimer.Stop();
                animationTimer.Dispose();
                foreach (PopoverWidget widget in widgets)
                    widget.Close();
                canvas.Dispose();
                Fonts.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class PopoverFonts : IDisposable
    {
        public readonly Font Title;
        public readonly Font Body;
        public readonly Font BodyBold;
        public readonly Font Small;
        public readonly Font Glyph;
        public readonly Font SmallGlyph;

        public PopoverFonts(float scale)
        {
            Title = new Font("Segoe UI Semibold", 15f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Body = new Font("Segoe UI", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            BodyBold = new Font("Segoe UI Semibold", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Small = new Font("Segoe UI", 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Glyph = new Font(MenuBarGlyphs.FontFamily, 15f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            SmallGlyph = new Font(MenuBarGlyphs.FontFamily, 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public void Dispose()
        {
            Title.Dispose();
            Body.Dispose();
            BodyBold.Dispose();
            Small.Dispose();
            Glyph.Dispose();
            SmallGlyph.Dispose();
        }
    }

    internal static class MenuBarGlyphs
    {
        private static string family;

        public static string FontFamily
        {
            get
            {
                if (family == null)
                {
                    using (var fonts = new InstalledFontCollection())
                    {
                        family = "Segoe MDL2 Assets";
                        foreach (FontFamily installed in fonts.Families)
                            if (installed.Name == "Segoe Fluent Icons")
                                family = installed.Name;
                    }
                }
                return family;
            }
        }
    }

    // ------------------------------------------------------------------ Widgets

    internal abstract class PopoverWidget
    {
        protected static readonly Color Text = Color.FromArgb(242, 242, 245);
        protected static readonly Color Secondary = Color.FromArgb(160, 160, 168);
        protected static readonly Color Accent = Color.FromArgb(10, 132, 255);
        protected static readonly Color Fill = Color.FromArgb(255, 255, 255);

        public PopoverForm Owner;
        public RectangleF Bounds;
        public float SpacingAfter = 8;

        protected float S
        {
            get { return Owner.Scale; }
        }

        protected PopoverFonts Fonts
        {
            get { return Owner.Fonts; }
        }

        public abstract float Measure(Graphics graphics, float width);
        public abstract void Paint(Graphics graphics, bool hovered);
        public virtual bool OnHover(Point point) { return false; }
        public virtual bool OnPress(Point point) { return false; }
        public virtual void OnDrag(Point point) { }
        public virtual void OnRelease() { }
        public virtual void OnClick(Point point) { }
        public virtual bool HandleKeyDown(KeyEventArgs e) { return false; }
        public virtual bool HandleKeyPress(char c) { return false; }
        /// <summary>Advances any animation; returns true when it needs a repaint.</summary>
        public virtual bool Animate() { return false; }
        public virtual void Close() { }

        protected static void DrawText(Graphics graphics, string text, Font font, Color color,
            RectangleF bounds, StringAlignment horizontal = StringAlignment.Near)
        {
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.Alignment = horizontal;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags |= StringFormatFlags.NoWrap;
                graphics.DrawString(text, font, brush, bounds, format);
            }
        }
    }

    /// <summary>A heading or a line of text, optionally with a secondary line under it.</summary>
    internal sealed class TextWidget : PopoverWidget
    {
        public string Title;
        public string Detail;
        public bool Heading;
        public bool Dim;

        private float titleHeight;

        public override float Measure(Graphics graphics, float width)
        {
            titleHeight = (Heading ? 22 : 18) * S;
            if (Dim && !string.IsNullOrEmpty(Title))
            {
                // Explanations may need several lines.
                SizeF size = graphics.MeasureString(Title, Fonts.Small, (int)width);
                titleHeight = Math.Max(titleHeight, size.Height + 2 * S);
            }
            float height = titleHeight;
            if (!string.IsNullOrEmpty(Detail))
                height += 16 * S;
            return height;
        }

        public override void Paint(Graphics graphics, bool hovered)
        {
            var titleBounds = new RectangleF(Bounds.Left, Bounds.Top, Bounds.Width, titleHeight);
            if (Dim)
            {
                using (var brush = new SolidBrush(Secondary))
                    graphics.DrawString(Title, Fonts.Small, brush, titleBounds);
            }
            else
            {
                DrawText(graphics, Title, Heading ? Fonts.Title : Fonts.BodyBold, Text, titleBounds);
            }
            if (!string.IsNullOrEmpty(Detail))
                DrawText(graphics, Detail, Fonts.Small, Secondary,
                    new RectangleF(Bounds.Left, Bounds.Top + titleHeight, Bounds.Width, 16 * S));
        }
    }

    internal sealed class SeparatorWidget : PopoverWidget
    {
        public override float Measure(Graphics graphics, float width) { return 1 * S; }

        public override void Paint(Graphics graphics, bool hovered)
        {
            using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255)))
                graphics.DrawLine(pen, Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Top);
        }
    }

    /// <summary>A clickable row: optional glyph, a label, and an optional value on the right.</summary>
    internal sealed class RowWidget : PopoverWidget
    {
        public string Glyph;
        public string Label;
        public string Value;
        public bool Selected;
        public Image Icon;
        public Action Click;

        public RowWidget()
        {
            SpacingAfter = 2;
        }

        public override float Measure(Graphics graphics, float width) { return 30 * S; }

        public override void Paint(Graphics graphics, bool hovered)
        {
            if (Click != null && (hovered || Selected))
            {
                using (GraphicsPath path = PopoverForm.Rounded(RectangleF.Inflate(Bounds, 4 * S, 0), 6 * S))
                using (var brush = new SolidBrush(Selected ? Color.FromArgb(220, Accent) : Color.FromArgb(34, 255, 255, 255)))
                    graphics.FillPath(brush, path);
            }
            float x = Bounds.Left + 4 * S;
            if (Icon != null)
            {
                float size = 20 * S;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(Icon, x, Bounds.Top + (Bounds.Height - size) / 2f, size, size);
                x += size + 10 * S;
            }
            else if (!string.IsNullOrEmpty(Glyph))
            {
                DrawText(graphics, Glyph, Fonts.Glyph, Text, new RectangleF(x, Bounds.Top, 22 * S, Bounds.Height));
                x += 30 * S;
            }
            float valueWidth = string.IsNullOrEmpty(Value) ? 0 : 110 * S;
            DrawText(graphics, Label ?? string.Empty, Fonts.Body, Text,
                new RectangleF(x, Bounds.Top, Bounds.Right - x - valueWidth - 4 * S, Bounds.Height));
            if (valueWidth > 0)
                DrawText(graphics, Value, Fonts.Small, Selected ? Text : Secondary,
                    new RectangleF(Bounds.Right - valueWidth - 4 * S, Bounds.Top, valueWidth, Bounds.Height),
                    StringAlignment.Far);
        }

        public override void OnClick(Point point)
        {
            if (Click != null)
                Click();
        }
    }

    /// <summary>A rounded tile as in the macOS Control Centre.</summary>
    internal sealed class TilesWidget : PopoverWidget
    {
        public sealed class Tile
        {
            public string Glyph;
            public string Title;
            public string Detail;
            public bool On;
            /// <summary>Runs when the round button is pressed; receives the new state.</summary>
            public Action<bool> Toggle;
            /// <summary>Runs when the rest of the tile is pressed.</summary>
            public Action Click;
            public RectangleF Bounds;
            public RectangleF Circle;
        }

        public readonly List<Tile> Tiles = new List<Tile>();
        private Tile hoveredTile;

        public override float Measure(Graphics graphics, float width) { return 58 * S; }

        public override void Paint(Graphics graphics, bool hovered)
        {
            float gap = 8 * S;
            float width = (Bounds.Width - gap * (Tiles.Count - 1)) / Math.Max(1, Tiles.Count);
            for (int index = 0; index < Tiles.Count; index++)
            {
                Tile tile = Tiles[index];
                tile.Bounds = new RectangleF(Bounds.Left + index * (width + gap), Bounds.Top, width, Bounds.Height);
                using (GraphicsPath path = PopoverForm.Rounded(tile.Bounds, 12 * S))
                using (var brush = new SolidBrush(Color.FromArgb(tile == hoveredTile && hovered ? 52 : 34, 255, 255, 255)))
                    graphics.FillPath(brush, path);
                var circle = new RectangleF(tile.Bounds.Left + 10 * S, tile.Bounds.Top + (tile.Bounds.Height - 30 * S) / 2f, 30 * S, 30 * S);
                tile.Circle = circle;
                using (var brush = new SolidBrush(tile.On ? Accent : Color.FromArgb(60, 255, 255, 255)))
                    graphics.FillEllipse(brush, circle);
                DrawText(graphics, tile.Glyph, Fonts.Glyph, Text, circle, StringAlignment.Center);
                float textLeft = circle.Right + 8 * S;
                var textArea = new RectangleF(textLeft, tile.Bounds.Top + 10 * S, tile.Bounds.Right - textLeft - 6 * S, 18 * S);
                DrawText(graphics, tile.Title, Fonts.BodyBold, Text, textArea);
                textArea.Offset(0, 18 * S);
                DrawText(graphics, tile.Detail ?? string.Empty, Fonts.Small, Secondary, textArea);
            }
        }

        public override bool OnHover(Point point)
        {
            Tile tile = Tiles.Find(candidate => candidate.Bounds.Contains(point));
            bool changed = tile != hoveredTile;
            hoveredTile = tile;
            return changed;
        }

        public override void OnClick(Point point)
        {
            Tile tile = Tiles.Find(candidate => candidate.Bounds.Contains(point));
            if (tile == null)
                return;
            bool onCircle = RectangleF.Inflate(tile.Circle, 4 * S, 4 * S).Contains(point);
            if (tile.Toggle != null && (onCircle || tile.Click == null))
            {
                // Show the new state at once; the change itself may take a moment.
                tile.On = !tile.On;
                bool state = tile.On;
                Action<bool> toggle = tile.Toggle;
                ThreadPool.QueueUserWorkItem(delegate { toggle(state); });
                Owner.Invalidate();
            }
            else if (tile.Click != null)
            {
                tile.Click();
            }
        }
    }

    /// <summary>
    /// A labelled slider, like the Sound and Display modules of the Control Centre. The fill
    /// glides to its value, follows changes made elsewhere (keyboard volume keys) and applies
    /// changes on a background thread so slow devices (DDC/CI monitors) never stall it.
    /// </summary>
    internal sealed class SliderWidget : PopoverWidget
    {
        public string Label;
        public string Glyph;
        public float Value;
        public Action<float> Changed;
        public Func<float?> Source;
        public Func<float, string> GlyphFor;
        public Action GlyphClick;
        private RectangleF track;
        private RectangleF glyphArea;
        private float shown = -1f;
        private bool dragging;
        private int pollCountdown;
        private volatile bool polling;
        private float? polled;
        private float pending = -1f;
        private volatile bool applying;

        public override float Measure(Graphics graphics, float width)
        {
            return (string.IsNullOrEmpty(Label) ? 30 : 50) * S;
        }

        public override void Paint(Graphics graphics, bool hovered)
        {
            if (shown < 0)
                shown = Value;
            float top = Bounds.Top;
            if (!string.IsNullOrEmpty(Label))
            {
                DrawText(graphics, Label, Fonts.BodyBold, Text, new RectangleF(Bounds.Left, top, Bounds.Width, 18 * S));
                top += 22 * S;
            }
            track = new RectangleF(Bounds.Left, top + 2 * S, Bounds.Width, 24 * S);
            using (GraphicsPath path = PopoverForm.Rounded(track, 12 * S))
            using (var brush = new SolidBrush(Color.FromArgb(46, 255, 255, 255)))
                graphics.FillPath(brush, path);
            float filled = Math.Max(track.Height, track.Width * shown);
            using (GraphicsPath path = PopoverForm.Rounded(new RectangleF(track.Left, track.Top, filled, track.Height), 12 * S))
            using (var brush = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                graphics.FillPath(brush, path);
            glyphArea = new RectangleF(track.Left + 4 * S, track.Top, 22 * S, track.Height);
            string glyph = GlyphFor != null ? GlyphFor(Value) : Glyph;
            if (!string.IsNullOrEmpty(glyph))
                DrawText(graphics, glyph, Fonts.SmallGlyph, Color.FromArgb(200, 30, 30, 34), glyphArea, StringAlignment.Center);
        }

        public override bool Animate()
        {
            // Pick up changes made outside the popover, a few times per second.
            if (Source != null && !dragging && --pollCountdown <= 0 && !polling)
            {
                pollCountdown = 20;
                polling = true;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    polled = Source();
                    polling = false;
                });
            }
            if (polled.HasValue && !dragging && pending < 0 && !applying)
            {
                Value = polled.Value;
                polled = null;
            }
            if (shown < 0)
                return false;
            float before = shown;
            shown += (Value - shown) * (dragging ? 0.55f : 0.22f);
            if (Math.Abs(Value - shown) < 0.002f)
                shown = Value;
            return Math.Abs(before - shown) > 0.0005f;
        }

        public override bool OnPress(Point point)
        {
            if (GlyphClick != null && glyphArea.Contains(point))
            {
                GlyphClick();
                return false;
            }
            if (!track.Contains(point))
                return false;
            dragging = true;
            OnDrag(point);
            return true;
        }

        public override void OnDrag(Point point)
        {
            Value = Math.Max(0f, Math.Min(1f, (point.X - track.Left) / track.Width));
            Apply(Value);
        }

        public override void OnRelease()
        {
            dragging = false;
        }

        /// <summary>Applies the newest value on a worker, dropping intermediate ones.</summary>
        private void Apply(float value)
        {
            if (Changed == null)
                return;
            pending = value;
            if (applying)
                return;
            applying = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                while (pending >= 0)
                {
                    float next = pending;
                    pending = -1f;
                    try { Changed(next); } catch (Exception) { }
                }
                applying = false;
            });
        }
    }

    /// <summary>Now Playing: artwork, title, artist and transport buttons.</summary>
    internal sealed class MediaWidget : PopoverWidget
    {
        private NowPlaying playing;
        private int refreshCountdown;
        private volatile bool loading;
        private NowPlaying loaded;
        private volatile bool hasLoaded;
        private RectangleF previous;
        private RectangleF playPause;
        private RectangleF next;

        public override float Measure(Graphics graphics, float width) { return 64 * S; }

        public override void Paint(Graphics graphics, bool hovered)
        {
            using (GraphicsPath path = PopoverForm.Rounded(Bounds, 12 * S))
            using (var brush = new SolidBrush(Color.FromArgb(34, 255, 255, 255)))
                graphics.FillPath(brush, path);
            var art = new RectangleF(Bounds.Left + 10 * S, Bounds.Top + 10 * S, 44 * S, 44 * S);
            using (GraphicsPath path = PopoverForm.Rounded(art, 7 * S))
            {
                if (playing != null && playing.Artwork != null)
                {
                    GraphicsState state = graphics.Save();
                    graphics.SetClip(path);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(playing.Artwork, art);
                    graphics.Restore(state);
                }
                else
                {
                    using (var brush = new SolidBrush(Color.FromArgb(50, 255, 255, 255)))
                        graphics.FillPath(brush, path);
                    DrawText(graphics, "", Fonts.Glyph, Secondary, art, StringAlignment.Center);
                }
            }
            float textLeft = art.Right + 10 * S;
            float buttonsWidth = 92 * S;
            var title = new RectangleF(textLeft, Bounds.Top + 13 * S, Bounds.Right - textLeft - buttonsWidth, 18 * S);
            DrawText(graphics, playing != null ? playing.Title : "No se reproduce nada", Fonts.BodyBold, Text, title);
            title.Offset(0, 19 * S);
            DrawText(graphics, playing != null ? playing.Artist ?? string.Empty : string.Empty, Fonts.Small, Secondary, title);

            float x = Bounds.Right - buttonsWidth;
            float y = Bounds.Top + (Bounds.Height - 28 * S) / 2f;
            previous = new RectangleF(x, y, 28 * S, 28 * S);
            playPause = new RectangleF(x + 30 * S, y, 28 * S, 28 * S);
            next = new RectangleF(x + 60 * S, y, 28 * S, 28 * S);
            Color enabled = playing != null ? Text : Color.FromArgb(90, 255, 255, 255);
            DrawText(graphics, "", Fonts.Glyph, enabled, previous, StringAlignment.Center);
            DrawText(graphics, playing != null && playing.Playing ? "" : "", Fonts.Glyph, enabled, playPause, StringAlignment.Center);
            DrawText(graphics, "", Fonts.Glyph, enabled, next, StringAlignment.Center);
        }

        public override bool Animate()
        {
            if (--refreshCountdown <= 0 && !loading)
            {
                refreshCountdown = 60;
                loading = true;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    loaded = MediaSession.Read();
                    hasLoaded = true;
                    loading = false;
                });
            }
            if (!hasLoaded)
                return false;
            hasLoaded = false;
            NowPlaying old = playing;
            playing = loaded;
            if (old != null && old.Artwork != null)
                old.Artwork.Dispose();
            return true;
        }

        public override void OnClick(Point point)
        {
            Action action = previous.Contains(point) ? MediaSession.Previous
                : playPause.Contains(point) ? MediaSession.PlayPause
                : next.Contains(point) ? (Action)MediaSession.Next
                : null;
            if (action == null)
                return;
            if (playPause.Contains(point) && playing != null)
                playing.Playing = !playing.Playing;
            refreshCountdown = 25;
            ThreadPool.QueueUserWorkItem(delegate { action(); });
            Owner.Invalidate();
        }

        public override void Close()
        {
            if (playing != null && playing.Artwork != null)
                playing.Artwork.Dispose();
        }
    }

    /// <summary>A usage meter: label, percentage and a thin bar, with a detail line.</summary>
    internal sealed class MeterWidget : PopoverWidget
    {
        public string Label;
        public string Detail;
        public float? Fraction;

        public override float Measure(Graphics graphics, float width) { return 44 * S; }

        public override void Paint(Graphics graphics, bool hovered)
        {
            var top = new RectangleF(Bounds.Left, Bounds.Top, Bounds.Width, 18 * S);
            DrawText(graphics, Label, Fonts.Body, Text, top);
            DrawText(graphics, Fraction.HasValue ? Math.Round(Fraction.Value * 100) + " %" : "—",
                Fonts.BodyBold, Text, top, StringAlignment.Far);
            var bar = new RectangleF(Bounds.Left, Bounds.Top + 22 * S, Bounds.Width, 6 * S);
            using (GraphicsPath path = PopoverForm.Rounded(bar, 3 * S))
            using (var brush = new SolidBrush(Color.FromArgb(46, 255, 255, 255)))
                graphics.FillPath(brush, path);
            if (Fraction.HasValue && Fraction.Value > 0)
            {
                float fraction = Math.Min(1f, Fraction.Value);
                Color color = fraction >= 0.9f ? Color.FromArgb(255, 69, 58)
                    : fraction >= 0.7f ? Color.FromArgb(255, 159, 10)
                    : Accent;
                var filled = new RectangleF(bar.Left, bar.Top, Math.Max(bar.Height, bar.Width * fraction), bar.Height);
                using (GraphicsPath path = PopoverForm.Rounded(filled, 3 * S))
                using (var brush = new SolidBrush(color))
                    graphics.FillPath(brush, path);
            }
            DrawText(graphics, Detail ?? string.Empty, Fonts.Small, Secondary,
                new RectangleF(Bounds.Left, bar.Bottom + 2 * S, Bounds.Width, 16 * S));
        }
    }
}
