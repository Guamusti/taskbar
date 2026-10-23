using System;
using System.IO;

namespace TaskbarDock
{
    internal static class AppTrace
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskbarDock",
            "TaskbarDock.log");

        public static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(
                    LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message +
                    Environment.NewLine);
            }
            catch (Exception)
            {
                // Diagnostics must never prevent the application from starting.
            }
        }
    }
}
