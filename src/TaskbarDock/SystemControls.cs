using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Foundation;
using Point = System.Drawing.Point;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TaskbarDock
{
    /// <summary>
    /// Waits for WinRT asynchronous operations. .NET Framework cannot await them without
    /// extra packages, and these calls are short and made off the UI thread.
    /// </summary>
    internal static class WinRt
    {
        public static T Wait<T>(IAsyncOperation<T> operation, int timeoutMs = 4000)
        {
            int waited = 0;
            while (operation.Status == AsyncStatus.Started && waited < timeoutMs)
            {
                Thread.Sleep(5);
                waited += 5;
            }
            return operation.Status == AsyncStatus.Completed ? operation.GetResults() : default(T);
        }
    }

    /// <summary>Turns the Wi-Fi and Bluetooth radios on and off, without admin rights.</summary>
    internal static class Radios
    {
        public static bool? IsOn(RadioKind kind)
        {
            try
            {
                Radio radio = Find(kind);
                return radio == null ? (bool?)null : radio.State == RadioState.On;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Set(RadioKind kind, bool on)
        {
            try
            {
                Radio radio = Find(kind);
                if (radio != null)
                    WinRt.Wait(radio.SetStateAsync(on ? RadioState.On : RadioState.Off));
            }
            catch (Exception exception)
            {
                AppTrace.Log("Radio change failed: " + exception.Message);
            }
        }

        private static Radio Find(RadioKind kind)
        {
            WinRt.Wait(Radio.RequestAccessAsync());
            IReadOnlyList<Radio> radios = WinRt.Wait(Radio.GetRadiosAsync());
            return radios == null ? null : radios.FirstOrDefault(radio => radio.Kind == kind);
        }
    }

    internal sealed class NowPlaying
    {
        public string Title;
        public string Artist;
        public string Source;
        public bool Playing;
        public Image Artwork;
    }

    /// <summary>What is playing anywhere in Windows (Spotify, browsers, Apple Music…).</summary>
    internal static class MediaSession
    {
        private static GlobalSystemMediaTransportControlsSession Current()
        {
            GlobalSystemMediaTransportControlsSessionManager manager =
                WinRt.Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
            return manager == null ? null : manager.GetCurrentSession();
        }

        public static NowPlaying Read()
        {
            try
            {
                GlobalSystemMediaTransportControlsSession session = Current();
                if (session == null)
                    return null;
                GlobalSystemMediaTransportControlsSessionMediaProperties properties =
                    WinRt.Wait(session.TryGetMediaPropertiesAsync());
                if (properties == null || string.IsNullOrEmpty(properties.Title))
                    return null;
                var playing = new NowPlaying
                {
                    Title = properties.Title,
                    Artist = properties.Artist,
                    Source = session.SourceAppUserModelId,
                    Playing = session.GetPlaybackInfo().PlaybackStatus ==
                              GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                };
                playing.Artwork = ReadArtwork(properties.Thumbnail);
                return playing;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Image ReadArtwork(IRandomAccessStreamReference reference)
        {
            if (reference == null)
                return null;
            try
            {
                IRandomAccessStreamWithContentType stream = WinRt.Wait(reference.OpenReadAsync());
                if (stream == null)
                    return null;
                using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                {
                    uint size = (uint)stream.Size;
                    WinRt.Wait(reader.LoadAsync(size));
                    var bytes = new byte[size];
                    reader.ReadBytes(bytes);
                    using (var memory = new MemoryStream(bytes))
                        return new Bitmap(Image.FromStream(memory));
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void PlayPause()
        {
            Try(session => WinRt.Wait(session.TryTogglePlayPauseAsync()));
        }

        public static void Next()
        {
            Try(session => WinRt.Wait(session.TrySkipNextAsync()));
        }

        public static void Previous()
        {
            Try(session => WinRt.Wait(session.TrySkipPreviousAsync()));
        }

        private static void Try(Action<GlobalSystemMediaTransportControlsSession> action)
        {
            try
            {
                GlobalSystemMediaTransportControlsSession session = Current();
                if (session != null)
                    action(session);
            }
            catch (Exception exception)
            {
                AppTrace.Log("Media control failed: " + exception.Message);
            }
        }
    }

    /// <summary>
    /// Screen brightness. External monitors through DDC/CI (Monitor Configuration API),
    /// laptop panels through WMI.
    /// </summary>
    internal static class Brightness
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
        }

        [DllImport("dxva2.dll")]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

        [DllImport("dxva2.dll")]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll")]
        private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll")]
        private static extern bool GetMonitorBrightness(IntPtr monitor, out uint minimum, out uint current, out uint maximum);

        [DllImport("dxva2.dll")]
        private static extern bool SetMonitorBrightness(IntPtr monitor, uint value);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point point, uint flags);

        private static readonly object Gate = new object();

        /// <summary>Brightness of the primary monitor from 0 to 1, or null when unsupported.</summary>
        public static float? Get()
        {
            lock (Gate)
            {
                float? value = null;
                WithMonitors(monitor =>
                {
                    uint minimum, current, maximum;
                    if (value == null && GetMonitorBrightness(monitor, out minimum, out current, out maximum) && maximum > minimum)
                        value = (current - minimum) / (float)(maximum - minimum);
                });
                return value ?? GetWmi();
            }
        }

        public static void Set(float fraction)
        {
            lock (Gate)
            {
                bool done = false;
                WithMonitors(monitor =>
                {
                    uint minimum, current, maximum;
                    if (GetMonitorBrightness(monitor, out minimum, out current, out maximum) && maximum > minimum)
                    {
                        SetMonitorBrightness(monitor, (uint)Math.Round(minimum + (maximum - minimum) * fraction));
                        done = true;
                    }
                });
                if (!done)
                    SetWmi(fraction);
            }
        }

        private static void WithMonitors(Action<IntPtr> action)
        {
            const uint MONITOR_DEFAULTTOPRIMARY = 1;
            IntPtr monitor = MonitorFromPoint(Point.Empty, MONITOR_DEFAULTTOPRIMARY);
            uint count;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out count) || count == 0)
                return;
            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, monitors))
                return;
            try
            {
                foreach (PHYSICAL_MONITOR physical in monitors)
                    action(physical.Handle);
            }
            finally
            {
                DestroyPhysicalMonitors(count, monitors);
            }
        }

        private static float? GetWmi()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                    foreach (System.Management.ManagementObject item in searcher.Get())
                        return Convert.ToByte(item["CurrentBrightness"]) / 100f;
            }
            catch (Exception) { }
            return null;
        }

        private static void SetWmi(float fraction)
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
                    foreach (System.Management.ManagementObject item in searcher.Get())
                        item.InvokeMethod("WmiSetBrightness", new object[] { 0u, (byte)Math.Round(fraction * 100) });
            }
            catch (Exception) { }
        }
    }

    /// <summary>The system wide light/dark theme switch.</summary>
    internal static class DarkMode
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public static bool IsOn()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Key))
                return key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
        }

        public static void Set(bool dark)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Key))
            {
                key.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
                key.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            }
            // Tell running apps and the shell to pick up the new theme.
            IntPtr result;
            IntPtr area = Marshal.StringToHGlobalUni("ImmersiveColorSet");
            try
            {
                NativeMethods.SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, area,
                    NativeMethods.SMTO_ABORTIFHUNG, 200, out result);
            }
            finally
            {
                Marshal.FreeHGlobal(area);
            }
        }
    }
}
