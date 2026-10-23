namespace TaskbarDock
{
    internal sealed class DockSettings
    {
        public bool Enabled { get; set; }
        public int DockWidth { get; set; }
        public int CornerRadius { get; set; }
        public int HorizontalPadding { get; set; }
        public bool KeepTrayVisible { get; set; }
        public bool RestoreWhenMaximized { get; set; }
        public bool RunAtStartup { get; set; }
        /// <summary>Native icon size in pixels; 0 detects it from the taskbar DPI.</summary>
        public int IconSize { get; set; }
        public int Magnification { get; set; }
        public int AnimationSpeed { get; set; }
        public bool ShowLabels { get; set; }
        /// <summary>How many icons to each side feel the magnification.</summary>
        public int Reach { get; set; }
        /// <summary>Shows the macOS style menu bar at the top of the screen.</summary>
        public bool ShowMenuBar { get; set; }
        /// <summary>Hides the menu bar while the active app is maximized.</summary>
        public bool AutoHideMenuBar { get; set; }

        public static DockSettings CreateDefault()
        {
            return new DockSettings
            {
                Enabled = true,
                DockWidth = 560,
                CornerRadius = 16,
                HorizontalPadding = 8,
                KeepTrayVisible = true,
                RestoreWhenMaximized = false,
                RunAtStartup = false,
                IconSize = 0,
                Magnification = 65,
                AnimationSpeed = 12,
                ShowLabels = true,
                Reach = 3,
                ShowMenuBar = true,
                AutoHideMenuBar = true
            };
        }

        public DockSettings Clone()
        {
            return (DockSettings)MemberwiseClone();
        }
    }
}
