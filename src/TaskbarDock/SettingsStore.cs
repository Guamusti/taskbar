using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace TaskbarDock
{
    internal sealed class SettingsStore
    {
        private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValue = "TaskbarDock";
        public const int MinimumIconSize = 16;
        private readonly string settingsPath;

        public SettingsStore()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TaskbarDock");
            Directory.CreateDirectory(directory);
            settingsPath = Path.Combine(directory, "settings.ini");
        }

        public DockSettings Load()
        {
            var settings = DockSettings.CreateDefault();
            if (!File.Exists(settingsPath))
                return settings;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in File.ReadAllLines(settingsPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;
                values[line.Substring(0, separator).Trim()] =
                    line.Substring(separator + 1).Trim();
            }

            settings.Enabled = ReadBool(values, "Enabled", settings.Enabled);
            settings.DockWidth = Clamp(ReadInt(values, "DockWidth", settings.DockWidth), 240, 1400);
            settings.CornerRadius = Clamp(ReadInt(values, "CornerRadius", settings.CornerRadius), 0, 40);
            settings.HorizontalPadding = Clamp(ReadInt(values, "HorizontalPadding", settings.HorizontalPadding), 0, 32);
            settings.KeepTrayVisible = ReadBool(values, "KeepTrayVisible", settings.KeepTrayVisible);
            settings.RestoreWhenMaximized = ReadBool(values, "RestoreWhenMaximized", settings.RestoreWhenMaximized);
            int iconSize = ReadInt(values, "IconSize", settings.IconSize);
            settings.IconSize = iconSize < MinimumIconSize ? 0 : Math.Min(64, iconSize);
            settings.Magnification = Clamp(ReadInt(values, "Magnification", settings.Magnification), 0, 150);
            settings.ShowMenuBar = ReadBool(values, "ShowMenuBar", settings.ShowMenuBar);
            settings.AutoHideMenuBar = ReadBool(values, "AutoHideMenuBar", settings.AutoHideMenuBar);
            settings.Reach = Clamp(ReadInt(values, "Reach", settings.Reach), 1, 5);
            settings.AnimationSpeed = Clamp(ReadInt(values, "AnimationSpeed", settings.AnimationSpeed), 2, 20);
            settings.ShowLabels = ReadBool(values, "ShowLabels", settings.ShowLabels);
            settings.RunAtStartup = IsStartupEnabled();
            return settings;
        }

        public void Save(DockSettings settings)
        {
            string tempPath = settingsPath + ".tmp";
            File.WriteAllLines(tempPath, new[]
            {
                "# TaskbarDock settings",
                "Enabled=" + settings.Enabled,
                "DockWidth=" + settings.DockWidth.ToString(CultureInfo.InvariantCulture),
                "CornerRadius=" + settings.CornerRadius.ToString(CultureInfo.InvariantCulture),
                "HorizontalPadding=" + settings.HorizontalPadding.ToString(CultureInfo.InvariantCulture),
                "KeepTrayVisible=" + settings.KeepTrayVisible,
                "RestoreWhenMaximized=" + settings.RestoreWhenMaximized,
                "IconSize=" + settings.IconSize.ToString(CultureInfo.InvariantCulture),
                "Magnification=" + settings.Magnification.ToString(CultureInfo.InvariantCulture),
                "AnimationSpeed=" + settings.AnimationSpeed.ToString(CultureInfo.InvariantCulture),
                "ShowLabels=" + settings.ShowLabels,
                "Reach=" + settings.Reach.ToString(CultureInfo.InvariantCulture),
                "ShowMenuBar=" + settings.ShowMenuBar,
                "AutoHideMenuBar=" + settings.AutoHideMenuBar
            });
            if (File.Exists(settingsPath))
                File.Replace(tempPath, settingsPath, null);
            else
                File.Move(tempPath, settingsPath);
            SetStartup(settings.RunAtStartup);
        }

        private static bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupKey, false))
                return key != null && key.GetValue(StartupValue) != null;
        }

        private static void SetStartup(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupKey))
            {
                if (enabled)
                    key.SetValue(StartupValue, "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --background");
                else
                    key.DeleteValue(StartupValue, false);
            }
        }

        private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback)
        {
            string value;
            bool result;
            return values.TryGetValue(key, out value) && bool.TryParse(value, out result)
                ? result
                : fallback;
        }

        private static int ReadInt(Dictionary<string, string> values, string key, int fallback)
        {
            string value;
            int result;
            return values.TryGetValue(key, out value) &&
                   int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
                ? result
                : fallback;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
