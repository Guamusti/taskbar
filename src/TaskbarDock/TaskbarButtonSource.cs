using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace TaskbarDock
{
    internal sealed class TaskbarButtonInfo
    {
        public string Id;
        public string Name;
        public RectangleF ScreenBounds;
        public AutomationElement Element;
    }

    /// <summary>
    /// Everything the dock needs to know about the primary taskbar at one moment.
    /// </summary>
    internal sealed class TaskbarSnapshot
    {
        public IntPtr Taskbar;
        public Rectangle TaskbarBounds;
        public RectangleF ContentBounds;
        public bool HasContent;
        public int Dpi;
        public List<TaskbarButtonInfo> Buttons = new List<TaskbarButtonInfo>();
    }

    internal static class TaskbarButtonSource
    {
        private const string AppIdPrefix = "Appid: ";

        /// <summary>
        /// Reads the taskbar in a single cached UI Automation round trip. Safe to call from
        /// a background thread. Returns null when the taskbar is not available.
        /// </summary>
        public static TaskbarSnapshot ReadSnapshot()
        {
            IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero)
                return null;

            NativeMethods.RECT bounds;
            if (!NativeMethods.GetWindowRect(taskbar, out bounds))
                return null;

            var snapshot = new TaskbarSnapshot
            {
                Taskbar = taskbar,
                TaskbarBounds = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                Dpi = NativeMethods.GetDpiForWindow(taskbar)
            };

            AutomationElementCollection descendants;
            try
            {
                var cache = new CacheRequest();
                cache.Add(AutomationElement.AutomationIdProperty);
                cache.Add(AutomationElement.NameProperty);
                cache.Add(AutomationElement.BoundingRectangleProperty);
                using (cache.Activate())
                {
                    AutomationElement root = AutomationElement.FromHandle(taskbar);
                    descendants = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                }
            }
            catch (Exception)
            {
                return null;
            }

            for (int index = 0; index < descendants.Count; index++)
            {
                try
                {
                    AutomationElement element = descendants[index];
                    string automationId = element.Cached.AutomationId;
                    if (string.IsNullOrEmpty(automationId))
                        continue;
                    bool isAppButton = automationId.StartsWith(AppIdPrefix, StringComparison.Ordinal);
                    bool isDockButton = isAppButton ||
                                        automationId == "StartButton" ||
                                        automationId == "SearchButton" ||
                                        automationId == "TaskViewButton";
                    if (!isDockButton)
                        continue;

                    System.Windows.Rect rect = element.Cached.BoundingRectangle;
                    if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                        continue;
                    var rectangle = new RectangleF(
                        (float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);

                    snapshot.ContentBounds = snapshot.HasContent
                        ? RectangleF.Union(snapshot.ContentBounds, rectangle)
                        : rectangle;
                    snapshot.HasContent = true;

                    if (!isAppButton)
                        continue;
                    snapshot.Buttons.Add(new TaskbarButtonInfo
                    {
                        Id = automationId.Substring(AppIdPrefix.Length),
                        Name = CleanName(element.Cached.Name),
                        ScreenBounds = rectangle,
                        Element = element
                    });
                }
                catch (Exception)
                {
                    // Explorer can rebuild one button while it is being read.
                }
            }

            snapshot.Buttons.Sort((a, b) => a.ScreenBounds.Left.CompareTo(b.ScreenBounds.Left));
            return snapshot;
        }

        public static void Invoke(TaskbarButtonInfo button)
        {
            if (button == null || button.Element == null)
                return;
            try
            {
                object pattern;
                if (button.Element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                    ((InvokePattern)pattern).Invoke();
            }
            catch (Exception)
            {
                // Explorer may rebuild the button between refresh and click.
            }
        }

        private static string CleanName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Aplicación";
            string clean = Regex.Replace(
                name,
                @"\s*[-:]\s*\d+\s+ventanas?\s+en\s+ejecución",
                string.Empty,
                RegexOptions.IgnoreCase);
            clean = Regex.Replace(
                clean,
                @"\s*[-:]\s*\d+\s+running\s+windows?",
                string.Empty,
                RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\s+(anclado|pinned)$", string.Empty, RegexOptions.IgnoreCase);
            return clean.Trim();
        }
    }
}
