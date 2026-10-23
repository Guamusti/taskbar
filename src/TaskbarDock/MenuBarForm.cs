using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace TaskbarDock
{
    /// <summary>
    /// A macOS style menu bar along the top of the primary screen. It reserves its space as an
    /// AppBar, shows the active app with its real menus (read through UI Automation) and a
    /// row of status items with the clock on the right.
    /// </summary>
    internal sealed class MenuBarForm : Form
    {
        private const int LogicalHeight = 26;
        private static readonly CultureInfo Spanish = new CultureInfo("es-ES");
        private readonly LayeredCanvas canvas = new LayeredCanvas();
        private readonly System.Windows.Forms.Timer tickTimer;
        private readonly List<BarItem> items = new List<BarItem>();
        private readonly uint appBarMessage;
        private readonly NativeMethods.WinEventProc foregroundProc;
        private readonly int ownProcessId = Process.GetCurrentProcess().Id;
        private IntPtr foregroundHook;
        private IntPtr targetWindow;
        private string appName = "Escritorio";
        private List<AppMenu> appMenus = new List<AppMenu>();
        private int menuRequest;
        private BarItem hovered;
        private BarItem opened;
        private bool appBarRegistered;
        private bool fullScreenHidden;
        private bool shown;
        private string lastClock;
        private string lastStatus;
        private Rectangle barBounds;
        private Font textFont;
        private Font boldFont;
        private Font glyphFont;
        private float scale = 1f;
        private int tickCount;
        private readonly System.Windows.Forms.Timer slideTimer;
        private bool visibleTarget;
        private bool collapsed;
        private bool revealed;
        private float slide;
        private float? usageFraction;
        private PopoverForm popover;
        private BarItem popoverItem;
        private DateTime popoverClosedAt;

        private sealed class BarItem
        {
            public string Text;
            public bool Glyph;
            public bool Bold;
            public bool Right;
            public bool Dim;
            public RectangleF Bounds;
            public Action<BarItem> Click;
        }

        private sealed class AppMenu
        {
            public string Name;
            public AutomationElement Element;
        }

        public MenuBarForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            appBarMessage = NativeMethods.RegisterWindowMessage("TaskbarDock.MenuBar");
            CreateHandle();

            foregroundProc = OnForegroundChanged;
            foregroundHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, foregroundProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);

            tickTimer = new System.Windows.Forms.Timer { Interval = 300 };
            tickTimer.Tick += delegate { OnTick(); };
            slideTimer = new System.Windows.Forms.Timer { Interval = 15 };
            slideTimer.Tick += delegate { SlideStep(); };
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

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        /// <summary>Hide the bar while the active app is maximized, like macOS does for
        /// full-screen apps; it slides back in when the cursor touches the top edge.</summary>
        public bool AutoHideWhenMaximized { get; set; }

        public void Enable(bool enable)
        {
            if (enable)
            {
                if (!appBarRegistered && !collapsed)
                    RegisterAppBar();
                tickTimer.Start();
                OnForegroundChanged(IntPtr.Zero, 0, NativeMethods.GetForegroundWindow(), 0, 0, 0, 0);
                ShowBar();
            }
            else
            {
                tickTimer.Stop();
                slideTimer.Stop();
                visibleTarget = false;
                collapsed = false;
                slide = 0f;
                if (shown)
                {
                    NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
                    shown = false;
                }
                UnregisterAppBar();
            }
        }

        private bool ForegroundIsMaximized()
        {
            IntPtr window = NativeMethods.GetForegroundWindow();
            if (window == IntPtr.Zero || !NativeMethods.IsZoomed(window))
                return false;
            uint processId;
            NativeMethods.GetWindowThreadProcessId(window, out processId);
            if (processId == ownProcessId)
                return collapsed; // Our popovers keep the current state.
            Screen screen = Screen.FromHandle(window);
            return screen.Primary;
        }

        /// <summary>Gives the space back to the maximized app and hides the bar.</summary>
        private void Collapse()
        {
            collapsed = true;
            revealed = false;
            UnregisterAppBar();
            slideTimer.Start();
        }

        /// <summary>Reserves the space again and slides the bar in.</summary>
        private void Expand()
        {
            collapsed = false;
            revealed = false;
            RegisterAppBar();
            slideTimer.Start();
        }

        private void SlideStep()
        {
            if (collapsed)
            {
                Point cursor = Cursor.Position;
                bool onBar = cursor.X >= barBounds.Left && cursor.X < barBounds.Right;
                bool popoverOpen = popover != null && !popover.IsDisposed;
                if (!revealed && onBar && cursor.Y <= barBounds.Top + 1)
                    revealed = true;
                else if (revealed && !popoverOpen && (cursor.Y > barBounds.Bottom + 8 * scale || !onBar))
                    revealed = false;
            }

            float target = visibleTarget && (!collapsed || revealed) ? 1f : 0f;
            slide += (target - slide) * 0.32f;
            if (Math.Abs(target - slide) < 0.02f)
                slide = target;

            if (slide > 0f && !shown)
            {
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
                NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                shown = true;
            }
            PresentBar();
            if (slide <= 0f && shown)
            {
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
                shown = false;
            }
            if (!collapsed && slide == target)
                slideTimer.Stop();
        }

        private void PresentBar()
        {
            if (barBounds.Width <= 0)
                return;
            int offset = (int)Math.Round((1f - slide) * barBounds.Height);
            canvas.Present(Handle, new Point(barBounds.Left, barBounds.Top - offset),
                (byte)Math.Round(255 * Math.Min(1f, 0.25f + slide)));
        }

        // ---------------------------------------------------------------- AppBar

        private void RegisterAppBar()
        {
            var data = NewAppBarData();
            data.uCallbackMessage = appBarMessage;
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data);
            appBarRegistered = true;
            PositionAppBar();
        }

        private void UnregisterAppBar()
        {
            if (!appBarRegistered)
                return;
            var data = NewAppBarData();
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
            appBarRegistered = false;
        }

        private NativeMethods.APPBARDATA NewAppBarData()
        {
            var data = new NativeMethods.APPBARDATA();
            data.cbSize = Marshal.SizeOf(typeof(NativeMethods.APPBARDATA));
            data.hWnd = Handle;
            data.uEdge = NativeMethods.ABE_TOP;
            return data;
        }

        private void PositionAppBar()
        {
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            scale = NativeMethods.GetDpiForWindow(Handle) / 96f;
            int height = (int)Math.Round(LogicalHeight * scale);
            var data = NewAppBarData();
            data.rc = new NativeMethods.RECT
            {
                Left = screen.Left,
                Top = screen.Top,
                Right = screen.Right,
                Bottom = screen.Top + height
            };
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref data);
            data.rc.Bottom = data.rc.Top + height;
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref data);
            barBounds = Rectangle.FromLTRB(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
            CreateFonts();
            Render();
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE = 0x0021;
            const int MA_NOACTIVATE = 3;
            const int WM_DISPLAYCHANGE = 0x007E;
            const int WM_DPICHANGED = 0x02E0;
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(MA_NOACTIVATE);
                return;
            }
            if (appBarRegistered && m.Msg == (int)appBarMessage)
            {
                switch (m.WParam.ToInt32())
                {
                    case NativeMethods.ABN_POSCHANGED:
                        PositionAppBar();
                        break;
                    case NativeMethods.ABN_FULLSCREENAPP:
                        fullScreenHidden = m.LParam != IntPtr.Zero;
                        if (fullScreenHidden)
                            HideBar();
                        else
                            ShowBar();
                        break;
                }
                return;
            }
            if ((m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_DPICHANGED) && appBarRegistered)
                BeginInvoke((MethodInvoker)PositionAppBar);
            base.WndProc(ref m);
        }

        private void ShowBar()
        {
            if (visibleTarget || fullScreenHidden || NativeMethods.IsFullScreenAppRunning())
                return;
            visibleTarget = true;
            Render();
            slideTimer.Start();
        }

        private void HideBar()
        {
            if (!visibleTarget)
                return;
            visibleTarget = false;
            slideTimer.Start();
        }

        // ---------------------------------------------------------------- Active app

        private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr window,
            int objectId, int childId, uint thread, uint time)
        {
            if (window == IntPtr.Zero || window == Handle)
                return;
            uint processId;
            NativeMethods.GetWindowThreadProcessId(window, out processId);
            if (processId == ownProcessId)
                return; // Our own menus and settings window.

            string windowClass = NativeMethods.WindowClass(window);
            if (windowClass == "Shell_TrayWnd" || windowClass == "Shell_SecondaryTrayWnd" ||
                windowClass == "TopLevelWindowForOverflowXamlIsland" ||
                windowClass == "Windows.UI.Core.CoreWindow" && targetWindow != IntPtr.Zero)
                return; // Start, search and flyouts keep the previous app, like macOS.

            targetWindow = window;
            if (windowClass == "Progman" || windowClass == "WorkerW")
            {
                appName = "Escritorio";
                appMenus = new List<AppMenu>();
                RebuildItems();
                return;
            }
            appName = ResolveAppName(window, processId);
            appMenus = new List<AppMenu>();
            RebuildItems();
            LoadMenusAsync(window);
        }

        private static string ResolveAppName(IntPtr window, uint processId)
        {
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    if (string.Equals(process.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                    {
                        string title = NativeMethods.WindowText(window);
                        return string.IsNullOrEmpty(title) ? "Aplicación" : title;
                    }
                    string description = null;
                    try
                    {
                        description = process.MainModule.FileVersionInfo.FileDescription;
                    }
                    catch (Exception)
                    {
                        // Elevated or protected processes cannot be inspected.
                    }
                    if (string.IsNullOrWhiteSpace(description) ||
                        description.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) >= 0)
                        description = process.ProcessName;
                    if (description.Length > 0)
                        description = char.ToUpper(description[0]) + description.Substring(1);
                    return description.Length > 40 ? description.Substring(0, 40) : description;
                }
            }
            catch (Exception)
            {
                return "Aplicación";
            }
        }

        /// <summary>
        /// Reads the top level entries of the window's menu bar, when it has one. Classic
        /// Win32 menus and most frameworks expose it through UI Automation.
        /// </summary>
        private void LoadMenusAsync(IntPtr window)
        {
            int request = Interlocked.Increment(ref menuRequest);
            ThreadPool.QueueUserWorkItem(delegate
            {
                var menus = new List<AppMenu>();
                try
                {
                    AutomationElement root = AutomationElement.FromHandle(window);
                    var menuBarCondition = new PropertyCondition(
                        AutomationElement.ControlTypeProperty, ControlType.MenuBar);
                    // Classic menus are direct children; some frameworks nest them deeper.
                    AutomationElement bar = FindAppMenuBar(root.FindAll(TreeScope.Children, menuBarCondition)) ??
                                            FindAppMenuBar(root.FindAll(TreeScope.Descendants, menuBarCondition));
                    if (bar != null)
                        menus.AddRange(ReadMenuItems(bar));
                }
                catch (Exception)
                {
                    // The window may have closed, or does not support UI Automation.
                }
                if (request != menuRequest || IsDisposed)
                    return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (request != menuRequest)
                            return;
                        appMenus = menus;
                        RebuildItems();
                    });
                }
                catch (InvalidOperationException)
                {
                }
            });
        }

        private static AutomationElement FindAppMenuBar(AutomationElementCollection bars)
        {
            foreach (AutomationElement bar in bars)
            {
                // The window's system menu (the icon in the title bar) is also a menu bar.
                if ((bar.Current.AutomationId ?? string.Empty) != "SystemMenuBar")
                    return bar;
            }
            return null;
        }

        private static IEnumerable<AppMenu> ReadMenuItems(AutomationElement bar)
        {
            var result = new List<AppMenu>();
            AutomationElementCollection children = bar.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
            foreach (AutomationElement child in children)
            {
                string name = (child.Current.Name ?? string.Empty).Replace("&", string.Empty).Trim();
                if (name.Length == 0)
                    continue;
                result.Add(new AppMenu { Name = name, Element = child });
                if (result.Count >= 12)
                    break;
            }
            return result;
        }

        private void OpenAppMenu(AppMenu menu)
        {
            IntPtr window = targetWindow;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    NativeMethods.SetForegroundWindow(window);
                    object pattern;
                    if (menu.Element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
                        ((ExpandCollapsePattern)pattern).Expand();
                    else if (menu.Element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                        ((InvokePattern)pattern).Invoke();
                }
                catch (Exception exception)
                {
                    AppTrace.Log("Opening app menu failed: " + exception.Message);
                }
            });
        }

        // ---------------------------------------------------------------- Items

        private void RebuildItems()
        {
            items.Clear();
            items.Add(new BarItem { Text = "logo", Click = ShowSystemMenu });
            items.Add(new BarItem { Text = appName, Bold = true, Click = ShowAppMenu });
            foreach (AppMenu menu in appMenus)
            {
                AppMenu captured = menu;
                items.Add(new BarItem { Text = menu.Name, Click = delegate { OpenAppMenu(captured); } });
            }

            // Right side, listed from the right edge inwards.
            items.Add(new BarItem
            {
                Text = ClockText(),
                Right = true,
                Click = item => TogglePopover(item, panel => SystemPanels.BuildCalendar(panel, delegate { SendWinKey(Keys.N); }))
            });
            items.Add(new BarItem { Text = "", Glyph = true, Right = true, Click = item => TogglePopover(item, SystemPanels.BuildControlCenter) });
            items.Add(new BarItem { Text = "", Glyph = true, Right = true, Click = item => TogglePopover(item, SystemPanels.BuildSearch) });
            items.Add(new BarItem { Text = "usage", Right = true, Click = item => TogglePopover(item, SystemPanels.BuildUsage) });
            items.Add(new BarItem { Text = CurrentVolumeGlyph(), Glyph = true, Right = true, Click = item => TogglePopover(item, SystemPanels.BuildVolume) });
            items.Add(new BarItem
            {
                Text = NetworkGlyph(),
                Glyph = true,
                Right = true,
                Click = item => TogglePopover(item, SystemPanels.BuildWifi)
            });
            PowerStatus power = SystemInformation.PowerStatus;
            if (power.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery &&
                power.BatteryChargeStatus != BatteryChargeStatus.Unknown)
            {
                int percent = (int)Math.Round(power.BatteryLifePercent * 100);
                items.Add(new BarItem
                {
                    Text = BatteryGlyph(percent, power.PowerLineStatus == PowerLineStatus.Online),
                    Glyph = true,
                    Right = true,
                    Click = delegate { Launch("ms-settings:batterysaver"); }
                });
                items.Add(new BarItem
                {
                    Text = percent + " %",
                    Right = true,
                    Click = delegate { Launch("ms-settings:batterysaver"); }
                });
            }
            lastStatus = StatusSignature();
            if (hovered != null && !items.Any(item => item.Text == hovered.Text))
                hovered = null;
            Render();
        }

        private static string ClockText()
        {
            DateTime now = DateTime.Now;
            string day = now.ToString("dddd", Spanish).Substring(0, 3);
            string month = now.ToString("MMM", Spanish).TrimEnd('.');
            return day + " " + now.Day + " " + month + "  " + now.ToString("H:mm", Spanish);
        }

        private static string CurrentVolumeGlyph()
        {
            float level = AudioEndpoint.GetVolume();
            return SystemPanels.VolumeGlyph(level < 0 ? 1f : level, AudioEndpoint.GetMute());
        }

        private static string NetworkGlyph()
        {
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable())
                    return "";
                bool wireless = NetworkInterface.GetAllNetworkInterfaces().Any(network =>
                    network.OperationalStatus == OperationalStatus.Up &&
                    network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
                return wireless ? "" : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string BatteryGlyph(int percent, bool charging)
        {
            int level = Math.Max(0, Math.Min(10, (int)Math.Round(percent / 10.0)));
            if (charging)
                return level >= 10 ? "" : ((char)(0xE85A + Math.Min(9, level))).ToString();
            return level >= 10 ? "" : ((char)(0xE850 + level)).ToString();
        }

        private string StatusSignature()
        {
            PowerStatus power = SystemInformation.PowerStatus;
            return ClockText() + "|" + CurrentVolumeGlyph() + "|" + NetworkGlyph() + "|" + power.BatteryLifePercent + "|" + power.PowerLineStatus;
        }

        private void OnTick()
        {
            if (NativeMethods.IsFullScreenAppRunning())
            {
                HideBar();
                return;
            }
            if (!fullScreenHidden)
                ShowBar();
            bool maximized = AutoHideWhenMaximized && ForegroundIsMaximized();
            if (maximized && !collapsed)
                Collapse();
            else if (!maximized && collapsed)
                Expand();
            // Status items change slowly; check them about once a second.
            if (++tickCount % 3 != 0)
                return;
            if (tickCount % 600 == 3)
                RefreshUsageAsync(); // About every three minutes.
            if (shown)
            {
                // The taskbar and other topmost windows can rise above us.
                NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
            if (StatusSignature() != lastStatus)
                RebuildItems();
        }

        // ---------------------------------------------------------------- Drawing

        private void CreateFonts()
        {
            if (textFont != null) textFont.Dispose();
            if (boldFont != null) boldFont.Dispose();
            if (glyphFont != null) glyphFont.Dispose();
            string family = FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
            textFont = new Font(family, 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            boldFont = new Font(family, 13f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            string glyphFamily = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            glyphFont = new Font(glyphFamily, 14f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private static bool FontExists(string name)
        {
            using (var fonts = new InstalledFontCollection())
                return fonts.Families.Any(family => family.Name == name);
        }

        private void Render()
        {
            if (barBounds.Width <= 0 || textFont == null)
                return;
            Graphics graphics = canvas.Begin(barBounds.Width, barBounds.Height);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using (var background = new SolidBrush(Color.FromArgb(214, 24, 24, 28)))
                graphics.FillRectangle(background, 0, 0, barBounds.Width, barBounds.Height);
            using (var line = new Pen(Color.FromArgb(40, 255, 255, 255)))
                graphics.DrawLine(line, 0, barBounds.Height - 0.5f, barBounds.Width, barBounds.Height - 0.5f);

            float padding = 9 * scale;
            float height = barBounds.Height;
            float x = 10 * scale;
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                foreach (BarItem item in items.Where(item => !item.Right))
                {
                    float width = item.Text == "logo"
                        ? 14 * scale
                        : graphics.MeasureString(item.Text, item.Bold ? boldFont : textFont, PointF.Empty, format).Width;
                    item.Bounds = new RectangleF(x, 3 * scale, width + padding * 2, height - 6 * scale);
                    x += item.Bounds.Width + 2 * scale;
                }
                float right = barBounds.Width - 10 * scale;
                foreach (BarItem item in items.Where(item => item.Right))
                {
                    Font font = item.Glyph ? glyphFont : textFont;
                    float width = item.Text == "usage"
                        ? 15 * scale
                        : graphics.MeasureString(item.Text, font, PointF.Empty, format).Width;
                    float itemPadding = item.Glyph ? 7 * scale : padding;
                    item.Bounds = new RectangleF(right - width - itemPadding * 2, 3 * scale,
                        width + itemPadding * 2, height - 6 * scale);
                    right = item.Bounds.Left - 1 * scale;
                }

                foreach (BarItem item in items)
                {
                    if (item == hovered || item == opened)
                    {
                        using (GraphicsPath path = RoundedRectangle(item.Bounds, 5 * scale))
                        using (var highlight = new SolidBrush(Color.FromArgb(item == opened ? 70 : 42, 255, 255, 255)))
                            graphics.FillPath(highlight, path);
                    }
                    if (item.Text == "logo")
                    {
                        DrawLogo(graphics, item.Bounds);
                        continue;
                    }
                    if (item.Text == "usage")
                    {
                        DrawUsageRing(graphics, item.Bounds);
                        continue;
                    }
                    Font font = item.Glyph ? glyphFont : item.Bold ? boldFont : textFont;
                    using (var brush = new SolidBrush(Color.FromArgb(item.Dim ? 150 : 240, 255, 255, 255)))
                    using (var centered = new StringFormat(format))
                    {
                        centered.Alignment = StringAlignment.Center;
                        centered.LineAlignment = StringAlignment.Center;
                        RectangleF textBounds = item.Bounds;
                        if (!item.Glyph)
                            textBounds.Offset(0, -0.5f * scale);
                        graphics.DrawString(item.Text, font, brush, textBounds, centered);
                    }
                }
            }
            PresentBar();
        }

        /// <summary>A small ring that fills up with the highest Claude or Codex usage.</summary>
        private void DrawUsageRing(Graphics graphics, RectangleF bounds)
        {
            float size = 13 * scale;
            var ring = new RectangleF(bounds.Left + (bounds.Width - size) / 2f, bounds.Top + (bounds.Height - size) / 2f, size, size);
            using (var track = new Pen(Color.FromArgb(90, 255, 255, 255), 2.2f * scale))
                graphics.DrawEllipse(track, ring);
            float fraction = Math.Max(0f, Math.Min(1f, usageFraction ?? 0f));
            if (fraction <= 0f)
                return;
            Color color = fraction >= 0.9f ? Color.FromArgb(255, 69, 58)
                : fraction >= 0.7f ? Color.FromArgb(255, 159, 10)
                : Color.FromArgb(240, 255, 255, 255);
            using (var arc = new Pen(color, 2.2f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                graphics.DrawArc(arc, ring, -90, Math.Max(8f, 360f * fraction));
        }

        private void RefreshUsageAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                float? highest = null;
                foreach (UsageReport report in new[] { UsageProviders.ReadClaude(), UsageProviders.ReadCodex() })
                    if (report.Highest.HasValue && (!highest.HasValue || report.Highest > highest))
                        highest = report.Highest;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        usageFraction = highest;
                        Render();
                    });
                }
                catch (InvalidOperationException)
                {
                }
            });
        }

        private void TogglePopover(BarItem item, Action<PopoverForm> build)
        {
            bool sameItem = popoverItem != null && popoverItem.Text == item.Text;
            if (popover != null && !popover.IsDisposed)
            {
                popover.CloseSoon();
                if (sameItem)
                    return;
            }
            else if (sameItem && (DateTime.UtcNow - popoverClosedAt).TotalMilliseconds < 400)
            {
                return; // This click is what closed it.
            }

            var panel = new PopoverForm(scale);
            build(panel);
            popover = panel;
            popoverItem = item;
            opened = item;
            Render();
            panel.FormClosed += delegate
            {
                popoverClosedAt = DateTime.UtcNow;
                if (popover == panel)
                {
                    popover = null;
                    opened = null;
                    Render();
                }
                BeginInvoke((MethodInvoker)panel.Dispose);
            };
            panel.ShowAt(barBounds.Left + (int)(item.Bounds.Left + item.Bounds.Width / 2),
                barBounds.Bottom + (int)(5 * scale));
        }

        private void DrawLogo(Graphics graphics, RectangleF bounds)
        {
            float size = 12 * scale;
            float gap = 1.2f * scale;
            float tile = (size - gap) / 2f;
            float left = bounds.Left + (bounds.Width - size) / 2f;
            float top = bounds.Top + (bounds.Height - size) / 2f;
            using (var brush = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
            {
                graphics.FillRectangle(brush, left, top, tile, tile);
                graphics.FillRectangle(brush, left + tile + gap, top, tile, tile);
                graphics.FillRectangle(brush, left, top + tile + gap, tile, tile);
                graphics.FillRectangle(brush, left + tile + gap, top + tile + gap, tile, tile);
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

        // ---------------------------------------------------------------- Mouse

        private BarItem ItemAt(Point client)
        {
            return items.FirstOrDefault(item => item.Bounds.Contains(client));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            BarItem item = ItemAt(e.Location);
            if (item != hovered)
            {
                hovered = item;
                Render();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hovered != null)
            {
                hovered = null;
                Render();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
                return;
            BarItem item = ItemAt(e.Location);
            if (item != null && item.Click != null)
                item.Click(item);
        }

        // ---------------------------------------------------------------- Menus

        private static void AddMenuRow(PopoverForm panel, string label, Action action)
        {
            panel.Add(new RowWidget
            {
                Label = label,
                Click = delegate
                {
                    panel.CloseSoon();
                    action();
                }
            });
        }

        private void ShowSystemMenu(BarItem item)
        {
            TogglePopover(item, panel =>
            {
                panel.PanelWidth = 260;
                AddMenuRow(panel, "Acerca de este equipo", delegate { Launch("ms-settings:about"); });
                panel.Add(new SeparatorWidget());
                AddMenuRow(panel, "Configuración del sistema…", delegate { Launch("ms-settings:"); });
                AddMenuRow(panel, "Microsoft Store…", delegate { Launch("ms-windows-store:"); });
                panel.Add(new SeparatorWidget());
                AddMenuRow(panel, "Forzar salida…", delegate { Launch("taskmgr.exe"); });
                panel.Add(new SeparatorWidget());
                AddMenuRow(panel, "Suspender", delegate { Application.SetSuspendState(PowerState.Suspend, false, false); });
                AddMenuRow(panel, "Reiniciar…", delegate { ConfirmAndRun("¿Seguro que quieres reiniciar el equipo ahora?", "/r /t 0"); });
                AddMenuRow(panel, "Apagar…", delegate { ConfirmAndRun("¿Seguro que quieres apagar el equipo ahora?", "/s /t 0"); });
                panel.Add(new SeparatorWidget());
                AddMenuRow(panel, "Bloquear pantalla", delegate { NativeMethods.LockWorkStation(); });
                AddMenuRow(panel, "Cerrar sesión…", delegate { ConfirmAndRun("¿Seguro que quieres cerrar la sesión?", "/l"); });
            });
        }

        private void ShowAppMenu(BarItem item)
        {
            IntPtr window = targetWindow;
            if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
                return;
            TogglePopover(item, panel =>
            {
                panel.PanelWidth = 240;
                AddMenuRow(panel, "Minimizar", delegate { NativeMethods.ShowWindow(window, NativeMethods.SW_MINIMIZE); });
                AddMenuRow(panel, "Maximizar o restaurar", delegate
                {
                    bool maximized = NativeMethods.IsZoomed(window);
                    NativeMethods.ShowWindow(window, maximized ? NativeMethods.SW_RESTORE : NativeMethods.SW_SHOWMAXIMIZED);
                });
                panel.Add(new SeparatorWidget());
                AddMenuRow(panel, "Cerrar ventana", delegate
                {
                    NativeMethods.PostMessage(window, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                });
            });
        }

        private static void ConfirmAndRun(string question, string shutdownArguments)
        {
            if (MessageBox.Show(question, "TaskbarDock", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
                return;
            Launch("shutdown.exe", shutdownArguments);
        }

        private static void Launch(string target, string arguments = null)
        {
            try
            {
                var start = new ProcessStartInfo(target) { UseShellExecute = true };
                if (arguments != null)
                {
                    start.Arguments = arguments;
                    start.WindowStyle = ProcessWindowStyle.Hidden;
                }
                Process.Start(start);
            }
            catch (Exception exception)
            {
                AppTrace.Log("Launching " + target + " failed: " + exception.Message);
            }
        }

        /// <summary>Presses Windows + key, which opens the matching Windows panel.</summary>
        private static void SendWinKey(Keys key)
        {
            const byte VK_LWIN = 0x5B;
            const uint KEYEVENTF_KEYUP = 0x0002;
            NativeMethods.keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event((byte)key, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event((byte)key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tickTimer.Stop();
                tickTimer.Dispose();
                slideTimer.Stop();
                slideTimer.Dispose();
                UnregisterAppBar();
                if (foregroundHook != IntPtr.Zero)
                    NativeMethods.UnhookWinEvent(foregroundHook);
                canvas.Dispose();
                if (textFont != null) textFont.Dispose();
                if (boldFont != null) boldFont.Dispose();
                if (glyphFont != null) glyphFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
