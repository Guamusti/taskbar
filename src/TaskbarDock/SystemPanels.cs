using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarDock
{
    /// <summary>
    /// Builds the popovers that open from the menu bar status items.
    /// </summary>
    internal static class SystemPanels
    {
        private static readonly CultureInfo Spanish = new CultureInfo("es-ES");

        public static void BuildControlCenter(PopoverForm panel)
        {
            WifiStatus wifi = WifiStatus.Read();
            bool wifiOn = Radios.IsOn(Windows.Devices.Radios.RadioKind.WiFi) ?? wifi.Connected;
            bool? bluetoothOn = Radios.IsOn(Windows.Devices.Radios.RadioKind.Bluetooth);

            var tiles = panel.Add(new TilesWidget());
            tiles.Tiles.Add(new TilesWidget.Tile
            {
                Glyph = "",
                Title = "Wi‑Fi",
                Detail = !wifiOn ? "Desactivado" : wifi.Connected ? wifi.Ssid : "Sin conexión",
                On = wifiOn,
                Toggle = on => Radios.Set(Windows.Devices.Radios.RadioKind.WiFi, on),
                Click = delegate { panel.Clear(); BuildWifi(panel); panel.Invalidate(); }
            });
            tiles.Tiles.Add(new TilesWidget.Tile
            {
                Glyph = "",
                Title = "Bluetooth",
                Detail = bluetoothOn == null ? "No disponible" : bluetoothOn.Value ? "Activado" : "Desactivado",
                On = bluetoothOn ?? false,
                Toggle = bluetoothOn == null ? (Action<bool>)null : on => Radios.Set(Windows.Devices.Radios.RadioKind.Bluetooth, on),
                Click = bluetoothOn == null ? (Action)delegate { Launch("ms-settings:bluetooth"); panel.CloseSoon(); } : null
            });
            var more = panel.Add(new TilesWidget());
            more.Tiles.Add(new TilesWidget.Tile
            {
                Glyph = "",
                Title = "Modo oscuro",
                Detail = DarkMode.IsOn() ? "Activado" : "Desactivado",
                On = DarkMode.IsOn(),
                Toggle = on => DarkMode.Set(on)
            });
            more.Tiles.Add(new TilesWidget.Tile
            {
                Glyph = "",
                Title = "Captura",
                Detail = "Recorte de pantalla",
                Click = delegate { panel.CloseSoon(); Launch("ms-screenclip:"); }
            });

            float? brightness = Brightness.Get();
            if (brightness.HasValue)
            {
                panel.Add(new SliderWidget
                {
                    Label = "Pantalla",
                    Glyph = "",
                    Value = brightness.Value,
                    Changed = Brightness.Set,
                    Source = Brightness.Get
                });
            }
            AddVolume(panel, "Sonido");
            AddMixerEntry(panel, BuildControlCenter);
            panel.Add(new MediaWidget());
            panel.Add(new SeparatorWidget());
            panel.Add(new RowWidget
            {
                Glyph = "",
                Label = "Configuración del sistema…",
                Click = delegate { Launch("ms-settings:"); panel.CloseSoon(); }
            });
        }

        public static void BuildVolume(PopoverForm panel)
        {
            AddVolume(panel, "Sonido");
            AddMixerEntry(panel, BuildVolume);
            panel.Add(new MediaWidget());
            panel.Add(new SeparatorWidget());
            panel.Add(new RowWidget
            {
                Glyph = "",
                Label = "Configuración de sonido…",
                Click = delegate { Launch("ms-settings:sound"); panel.CloseSoon(); }
            });
        }

        /// <summary>
        /// A "Mezclador de aplicaciones ›" row that opens the per-app mixer in place, with a
        /// way back to <paramref name="rebuildParent"/>.
        /// </summary>
        private static void AddMixerEntry(PopoverForm panel, Action<PopoverForm> rebuildParent)
        {
            int count = AudioMixer.List().Count;
            panel.Add(new RowWidget
            {
                Glyph = "",
                Label = "Mezclador de aplicaciones",
                Value = (count == 1 ? "1 app" : count + " apps") + "  ›",
                Click = delegate
                {
                    panel.Clear();
                    BuildMixer(panel, rebuildParent);
                    panel.Invalidate();
                }
            });
        }

        /// <summary>One slider per app that is playing sound, like the Volume Mixer.</summary>
        private static void BuildMixer(PopoverForm panel, Action<PopoverForm> rebuildParent)
        {
            panel.Add(new RowWidget
            {
                Glyph = "",
                Label = "Mezclador de aplicaciones",
                Click = delegate
                {
                    panel.Clear();
                    rebuildParent(panel);
                    panel.Invalidate();
                }
            });
            panel.Add(new SeparatorWidget());
            List<AppAudioSession> sessions = AudioMixer.List();
            if (sessions.Count == 0)
            {
                panel.Add(new TextWidget { Title = "Ninguna aplicación está usando el sonido ahora.", Dim = true });
                return;
            }
            foreach (AppAudioSession session in sessions.Take(10))
            {
                int processId = session.ProcessId;
                bool muted = session.Muted;
                panel.Add(new SliderWidget
                {
                    Label = session.Name,
                    Icon = session.Icon,
                    Value = session.Volume,
                    GlyphFor = value => VolumeGlyph(value, muted),
                    Changed = value => AudioMixer.SetVolume(processId, value),
                    Source = () => AudioMixer.GetVolume(processId),
                    GlyphClick = delegate
                    {
                        muted = !muted;
                        ThreadPool.QueueUserWorkItem(delegate { AudioMixer.ToggleMute(processId); });
                        panel.Invalidate();
                    }
                });
            }
        }

        public static string VolumeGlyph(float level, bool muted)
        {
            if (muted || level <= 0.001f)
                return "";
            return level < 0.34f ? "" : level < 0.67f ? "" : "";
        }

        private static void AddVolume(PopoverForm panel, string label)
        {
            float level = AudioEndpoint.GetVolume();
            panel.Add(new SliderWidget
            {
                Label = label,
                Value = level < 0 ? 0.5f : level,
                GlyphFor = value => VolumeGlyph(value, AudioEndpoint.GetMute()),
                Changed = value =>
                {
                    AudioEndpoint.SetVolume(value);
                    if (value > 0.001f && AudioEndpoint.GetMute())
                        AudioEndpoint.SetMute(false);
                },
                Source = () =>
                {
                    float current = AudioEndpoint.GetVolume();
                    return current < 0 ? (float?)null : current;
                },
                GlyphClick = delegate
                {
                    AudioEndpoint.SetMute(!AudioEndpoint.GetMute());
                    panel.Invalidate();
                }
            });
        }

        public static void BuildWifi(PopoverForm panel)
        {
            bool wifiOn = Radios.IsOn(Windows.Devices.Radios.RadioKind.WiFi) ?? true;
            WifiStatus status = WifiStatus.Read();
            var header = panel.Add(new TilesWidget());
            header.Tiles.Add(new TilesWidget.Tile
            {
                Glyph = "",
                Title = "Wi‑Fi",
                Detail = !wifiOn ? "Desactivado" : status.Connected ? status.Ssid + " · " + status.Signal + " %" : "Sin conexión",
                On = wifiOn,
                Toggle = on =>
                {
                    Radios.Set(Windows.Devices.Radios.RadioKind.WiFi, on);
                    Thread.Sleep(on ? 2500 : 300);
                    RunOnPanel(panel, delegate { panel.Clear(); BuildWifi(panel); panel.Invalidate(); });
                }
            });
            if (!wifiOn)
                return;
            var loading = panel.Add(new TextWidget { Title = "Buscando redes…", Dim = true });

            ThreadPool.QueueUserWorkItem(delegate
            {
                List<WifiNetwork> networks = WifiStatus.ScanNetworks();
                RunOnPanel(panel, delegate
                {
                    panel.Remove(loading);
                    panel.Add(new TextWidget { Title = "Otras redes", Dim = true });
                    foreach (WifiNetwork network in networks.Where(n => n.Ssid != status.Ssid).Take(8))
                    {
                        WifiNetwork target = network;
                        panel.Add(new RowWidget
                        {
                            Glyph = network.Secured ? "" : "",
                            Label = network.Ssid,
                            Value = network.Signal + " %",
                            Click = delegate
                            {
                                ThreadPool.QueueUserWorkItem(delegate
                                {
                                    if (!WifiStatus.Connect(target.Ssid))
                                        Launch("ms-availablenetworks:");
                                });
                                panel.CloseSoon();
                            }
                        });
                    }
                    if (networks.Count(n => n.Ssid != status.Ssid) == 0)
                        panel.Add(new TextWidget { Title = "No hay otras redes cerca.", Dim = true });
                    panel.Add(new SeparatorWidget());
                    panel.Add(new RowWidget
                    {
                        Glyph = "",
                        Label = "Configuración de Wi‑Fi…",
                        Click = delegate { Launch("ms-settings:network-wifi"); panel.CloseSoon(); }
                    });
                    panel.Invalidate();
                });
            });
        }

        public static void BuildCalendar(PopoverForm panel, Action showNotifications)
        {
            DateTime now = DateTime.Now;
            string date = now.ToString("dddd, d 'de' MMMM", Spanish);
            panel.Add(new TextWidget
            {
                Title = char.ToUpper(date[0]) + date.Substring(1),
                Detail = now.ToString("H:mm", Spanish),
                Heading = true
            });
            panel.Add(new CalendarWidget());
            panel.Add(new SeparatorWidget());
            panel.Add(new RowWidget
            {
                Glyph = "",
                Label = "Notificaciones",
                Click = delegate { panel.CloseSoon(); showNotifications(); }
            });
            panel.Add(new RowWidget
            {
                Glyph = "",
                Label = "Configuración de fecha y hora…",
                Click = delegate { Launch("ms-settings:dateandtime"); panel.CloseSoon(); }
            });
        }

        public static void BuildSearch(PopoverForm panel)
        {
            panel.Add(new SearchWidget());
        }

        public static void BuildUsage(PopoverForm panel)
        {
            panel.Add(new TextWidget { Title = "Límites de uso", Heading = true });
            panel.Add(new TextWidget { Title = "Cargando…", Dim = true });
            ThreadPool.QueueUserWorkItem(delegate
            {
                UsageReport claude = UsageProviders.ReadClaude();
                UsageReport codex = UsageProviders.ReadCodex();
                RunOnPanel(panel, delegate
                {
                    panel.Clear();
                    panel.Add(new TextWidget { Title = "Límites de uso", Heading = true });
                    AddReport(panel, claude);
                    panel.Add(new SeparatorWidget());
                    AddReport(panel, codex);
                    panel.Add(new SeparatorWidget());
                    panel.Add(new RowWidget
                    {
                        Glyph = "",
                        Label = "Actualizar",
                        Click = delegate
                        {
                            UsageProviders.Invalidate();
                            panel.Clear();
                            BuildUsage(panel);
                            panel.Invalidate();
                        }
                    });
                    panel.Invalidate();
                });
            });
        }

        private static void AddReport(PopoverForm panel, UsageReport report)
        {
            panel.Add(new TextWidget { Title = report.Product, Detail = report.Subtitle });
            foreach (UsageWindow window in report.Windows)
                panel.Add(new MeterWidget { Label = window.Label, Fraction = window.Fraction, Detail = window.Detail });
            if (!string.IsNullOrEmpty(report.Message))
                panel.Add(new TextWidget { Title = report.Message, Dim = true });
        }

        internal static void RunOnPanel(PopoverForm panel, MethodInvoker action)
        {
            try
            {
                if (!panel.IsDisposed && panel.IsHandleCreated)
                    panel.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // The popover was closed meanwhile.
            }
        }

        public static void Launch(string target, string arguments = null)
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
    }

    // ------------------------------------------------------------------ Calendar

    internal sealed class CalendarWidget : PopoverWidget
    {
        private DateTime month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        private RectangleF previous;
        private RectangleF next;

        public override float Measure(Graphics graphics, float width) { return 220 * S; }

        public override void Paint(Graphics graphics, bool hovered)
        {
            var culture = new CultureInfo("es-ES");
            string title = month.ToString("MMMM yyyy", culture);
            var header = new RectangleF(Bounds.Left, Bounds.Top, Bounds.Width, 24 * S);
            DrawText(graphics, char.ToUpper(title[0]) + title.Substring(1), Fonts.BodyBold, Text, header);
            previous = new RectangleF(Bounds.Right - 52 * S, Bounds.Top, 24 * S, 24 * S);
            next = new RectangleF(Bounds.Right - 24 * S, Bounds.Top, 24 * S, 24 * S);
            DrawText(graphics, "", Fonts.Glyph, Text, previous, StringAlignment.Center);
            DrawText(graphics, "", Fonts.Glyph, Text, next, StringAlignment.Center);

            string[] days = { "L", "M", "X", "J", "V", "S", "D" };
            float cell = Bounds.Width / 7f;
            float top = Bounds.Top + 30 * S;
            for (int index = 0; index < 7; index++)
                DrawText(graphics, days[index], Fonts.Small, Secondary,
                    new RectangleF(Bounds.Left + index * cell, top, cell, 18 * S), StringAlignment.Center);
            top += 22 * S;

            int offset = ((int)month.DayOfWeek + 6) % 7;
            int count = DateTime.DaysInMonth(month.Year, month.Month);
            float rowHeight = 27 * S;
            for (int day = 1; day <= count; day++)
            {
                int slot = offset + day - 1;
                var area = new RectangleF(Bounds.Left + slot % 7 * cell, top + slot / 7 * rowHeight, cell, rowHeight);
                bool today = month.Year == DateTime.Today.Year && month.Month == DateTime.Today.Month &&
                             day == DateTime.Today.Day;
                if (today)
                {
                    float size = 24 * S;
                    using (var brush = new SolidBrush(Color.FromArgb(255, 69, 58)))
                        graphics.FillEllipse(brush, area.Left + (area.Width - size) / 2f, area.Top + (area.Height - size) / 2f, size, size);
                }
                bool weekend = slot % 7 >= 5;
                DrawText(graphics, day.ToString(), today ? Fonts.BodyBold : Fonts.Body,
                    weekend && !today ? Secondary : Text, area, StringAlignment.Center);
            }
        }

        public override void OnClick(Point point)
        {
            if (previous.Contains(point))
                month = month.AddMonths(-1);
            else if (next.Contains(point))
                month = month.AddMonths(1);
            else
                return;
            Owner.Invalidate();
        }
    }

    // ------------------------------------------------------------------ Spotlight-like search

    internal sealed class SearchWidget : PopoverWidget
    {
        private static List<string> shortcuts;
        private readonly Dictionary<string, Image> icons = new Dictionary<string, Image>();
        private string query = string.Empty;
        private List<string> results = new List<string>();
        private int selected;

        public override float Measure(Graphics graphics, float width)
        {
            return (40 + (results.Count > 0 ? 8 + results.Count * 32 : 0)) * S;
        }

        public override void Paint(Graphics graphics, bool hovered)
        {
            var box = new RectangleF(Bounds.Left, Bounds.Top, Bounds.Width, 40 * S);
            using (GraphicsPath path = PopoverForm.Rounded(box, 10 * S))
            using (var brush = new SolidBrush(Color.FromArgb(30, 255, 255, 255)))
                graphics.FillPath(brush, path);
            DrawText(graphics, "", Fonts.Glyph, Secondary, new RectangleF(box.Left + 10 * S, box.Top, 24 * S, box.Height));
            bool empty = query.Length == 0;
            DrawText(graphics, empty ? "Buscar aplicaciones" : query + "|", Fonts.Title,
                empty ? Secondary : Text, new RectangleF(box.Left + 40 * S, box.Top, box.Width - 48 * S, box.Height));

            float y = box.Bottom + 8 * S;
            for (int index = 0; index < results.Count; index++)
            {
                var row = new RectangleF(Bounds.Left, y, Bounds.Width, 30 * S);
                if (index == selected)
                {
                    using (GraphicsPath path = PopoverForm.Rounded(row, 6 * S))
                    using (var brush = new SolidBrush(Color.FromArgb(220, Accent)))
                        graphics.FillPath(brush, path);
                }
                Image icon = IconFor(results[index]);
                if (icon != null)
                    graphics.DrawImage(icon, row.Left + 6 * S, row.Top + 5 * S, 20 * S, 20 * S);
                DrawText(graphics, Path.GetFileNameWithoutExtension(results[index]), Fonts.Body, Text,
                    new RectangleF(row.Left + 34 * S, row.Top, row.Width - 40 * S, row.Height));
                y += 32 * S;
            }
        }

        private Image IconFor(string path)
        {
            Image icon;
            if (icons.TryGetValue(path, out icon))
                return icon;
            try
            {
                using (Icon associated = Icon.ExtractAssociatedIcon(path))
                    icon = associated.ToBitmap();
            }
            catch (Exception)
            {
                icon = null;
            }
            icons[path] = icon;
            return icon;
        }

        public override bool HandleKeyPress(char c)
        {
            if (char.IsControl(c))
                return false;
            query += c;
            Search();
            return true;
        }

        public override bool HandleKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Back:
                    if (query.Length > 0)
                        query = query.Substring(0, query.Length - 1);
                    Search();
                    return true;
                case Keys.Down:
                    selected = Math.Min(results.Count - 1, selected + 1);
                    Owner.Invalidate();
                    return true;
                case Keys.Up:
                    selected = Math.Max(0, selected - 1);
                    Owner.Invalidate();
                    return true;
                case Keys.Enter:
                    if (results.Count > 0)
                        Open(results[Math.Max(0, selected)]);
                    else if (query.Length > 0)
                        SystemPanels.Launch("search-ms:query=" + Uri.EscapeDataString(query));
                    Owner.CloseSoon();
                    return true;
            }
            return false;
        }

        public override void OnClick(Point point)
        {
            float firstRow = Bounds.Top + 48 * S;
            int index = (int)((point.Y - firstRow) / (32 * S));
            if (point.Y >= firstRow && index >= 0 && index < results.Count)
            {
                Open(results[index]);
                Owner.CloseSoon();
            }
        }

        private static void Open(string path)
        {
            SystemPanels.Launch(path);
        }

        private void Search()
        {
            selected = 0;
            string needle = query.Trim().ToLowerInvariant();
            if (needle.Length == 0)
            {
                results = new List<string>();
            }
            else
            {
                results = Shortcuts()
                    .Select(path => new { path, name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant() })
                    .Where(item => item.name.Contains(needle))
                    .OrderBy(item => item.name.StartsWith(needle) ? 0 : 1)
                    .ThenBy(item => item.name.Length)
                    .Select(item => item.path)
                    .Take(7)
                    .ToList();
            }
            Owner.Invalidate();
        }

        private static List<string> Shortcuts()
        {
            if (shortcuts != null)
                return shortcuts;
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
            {
                try
                {
                    foreach (string file in Directory.GetFiles(Environment.GetFolderPath(folder), "*.lnk", SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        if (name.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("desinstalar", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        if (!found.ContainsKey(name))
                            found[name] = file;
                    }
                }
                catch (Exception)
                {
                    // A protected folder: skip it.
                }
            }
            shortcuts = found.Values.ToList();
            return shortcuts;
        }
    }

    // ------------------------------------------------------------------ Wi-Fi

    internal sealed class WifiNetwork
    {
        public string Ssid;
        public int Signal;
        public bool Secured;
    }

    internal sealed class WifiStatus
    {
        public bool Connected;
        public string Ssid;
        public int Signal;

        public static WifiStatus Read()
        {
            var status = new WifiStatus();
            string output = RunNetsh("wlan show interfaces");
            Match ssid = Regex.Match(output, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
            Match signal = Regex.Match(output, @"^\s*(Se\S{1,2}al|Signal)\s*:\s*(\d+)", RegexOptions.Multiline);
            if (ssid.Success)
            {
                status.Connected = true;
                status.Ssid = ssid.Groups[1].Value.Trim();
                status.Signal = signal.Success ? int.Parse(signal.Groups[2].Value) : 0;
            }
            return status;
        }

        public static List<WifiNetwork> ScanNetworks()
        {
            var networks = new List<WifiNetwork>();
            string output = RunNetsh("wlan show networks mode=bssid");
            WifiNetwork current = null;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                Match ssid = Regex.Match(line, @"^SSID \d+\s*:\s*(.*)$");
                if (ssid.Success)
                {
                    current = new WifiNetwork { Ssid = ssid.Groups[1].Value.Trim() };
                    if (current.Ssid.Length > 0)
                        networks.Add(current);
                    continue;
                }
                if (current == null)
                    continue;
                Match auth = Regex.Match(line, @"^(Autenticaci\S{1,2}n|Authentication)\s*:\s*(.+)$");
                if (auth.Success)
                    current.Secured = auth.Groups[2].Value.IndexOf("Abierta", StringComparison.OrdinalIgnoreCase) < 0 &&
                                      auth.Groups[2].Value.IndexOf("Open", StringComparison.OrdinalIgnoreCase) < 0;
                Match signal = Regex.Match(line, @"^(Se\S{1,2}al|Signal)\s*:\s*(\d+)");
                if (signal.Success)
                    current.Signal = Math.Max(current.Signal, int.Parse(signal.Groups[2].Value));
            }
            return networks.OrderByDescending(network => network.Signal).ToList();
        }

        /// <summary>Connects to a network that already has a saved profile.</summary>
        public static bool Connect(string ssid)
        {
            string output = RunNetsh("wlan connect name=\"" + ssid.Replace("\"", string.Empty) + "\"");
            return output.IndexOf("correctamente", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   output.IndexOf("successfully", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RunNetsh(string arguments)
        {
            try
            {
                var start = new ProcessStartInfo("netsh.exe", arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
                };
                using (Process process = Process.Start(start))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                    return output;
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    // ------------------------------------------------------------------ Core Audio

    internal static class AudioEndpoint
    {
        // Created from its CLSID rather than a [ComImport] class: two such classes for the
        // same CLSID make the runtime reuse one wrapper and fail to cast it to the other.
        private static readonly Guid DeviceEnumeratorClsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int context, IntPtr parameters,
                [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr notify);
            int UnregisterControlChangeNotify(IntPtr notify);
            int GetChannelCount(out int count);
            int SetMasterVolumeLevel(float level, ref Guid context);
            int SetMasterVolumeLevelScalar(float level, ref Guid context);
            int GetMasterVolumeLevel(out float level);
            int GetMasterVolumeLevelScalar(out float level);
            int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
            int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
            int GetChannelVolumeLevel(uint channel, out float level);
            int GetChannelVolumeLevelScalar(uint channel, out float level);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        private static IAudioEndpointVolume Endpoint()
        {
            const int eRender = 0;
            const int eMultimedia = 1;
            const int CLSCTX_ALL = 23;
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(DeviceEnumeratorClsid));
            IMMDevice device;
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out device) != 0 || device == null)
                return null;
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            object endpoint;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out endpoint) != 0)
                return null;
            return endpoint as IAudioEndpointVolume;
        }

        public static float GetVolume()
        {
            try
            {
                IAudioEndpointVolume endpoint = Endpoint();
                float level;
                if (endpoint != null && endpoint.GetMasterVolumeLevelScalar(out level) == 0)
                    return level;
            }
            catch (Exception) { }
            return -1f;
        }

        public static void SetVolume(float level)
        {
            try
            {
                IAudioEndpointVolume endpoint = Endpoint();
                Guid context = Guid.Empty;
                if (endpoint != null)
                    endpoint.SetMasterVolumeLevelScalar(Math.Max(0f, Math.Min(1f, level)), ref context);
            }
            catch (Exception) { }
        }

        public static bool GetMute()
        {
            try
            {
                IAudioEndpointVolume endpoint = Endpoint();
                bool mute;
                if (endpoint != null && endpoint.GetMute(out mute) == 0)
                    return mute;
            }
            catch (Exception) { }
            return false;
        }

        public static void SetMute(bool mute)
        {
            try
            {
                IAudioEndpointVolume endpoint = Endpoint();
                Guid context = Guid.Empty;
                if (endpoint != null)
                    endpoint.SetMute(mute, ref context);
            }
            catch (Exception) { }
        }
    }
}
