using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Web.Script.Serialization;

namespace TaskbarDock
{
    internal sealed class UsageWindow
    {
        public string Label;
        public float? Fraction;
        public string Detail;
    }

    internal sealed class UsageReport
    {
        public string Product;
        public string Subtitle;
        public string Message;
        public List<UsageWindow> Windows = new List<UsageWindow>();

        public float? Highest
        {
            get
            {
                float? highest = null;
                foreach (UsageWindow window in Windows)
                    if (window.Fraction.HasValue && (!highest.HasValue || window.Fraction > highest))
                        highest = window.Fraction;
                return highest;
            }
        }
    }

    /// <summary>
    /// Reads the plan limits of Claude and Codex.
    /// Claude: the usage endpoint Claude Code itself uses, with the sign-in it keeps in
    /// ~/.claude/.credentials.json (the token is only read, never refreshed or sent elsewhere).
    /// Codex: the latest rate limit snapshot Codex writes into its session logs in ~/.codex.
    /// </summary>
    internal static class UsageProviders
    {
        private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(3);
        private static readonly object Gate = new object();
        private static UsageReport cachedClaude;
        private static DateTime cachedClaudeAt;

        public static void Invalidate()
        {
            lock (Gate)
                cachedClaude = null;
        }

        // ------------------------------------------------------------ Claude

        public static UsageReport ReadClaude()
        {
            lock (Gate)
            {
                if (cachedClaude != null && DateTime.UtcNow - cachedClaudeAt < CacheTime)
                    return cachedClaude;
            }
            UsageReport report = FetchClaude();
            lock (Gate)
            {
                cachedClaude = report;
                cachedClaudeAt = DateTime.UtcNow;
            }
            return report;
        }

        private static UsageReport FetchClaude()
        {
            var report = new UsageReport { Product = "Claude" };
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            Dictionary<string, object> oauth;
            try
            {
                var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path));
                oauth = (Dictionary<string, object>)root["claudeAiOauth"];
            }
            catch (Exception)
            {
                report.Message = "Inicia sesión en Claude Code para ver tus límites.";
                return report;
            }

            string plan = Text(oauth, "subscriptionType");
            if (!string.IsNullOrEmpty(plan))
                report.Subtitle = "Plan " + char.ToUpper(plan[0]) + plan.Substring(1);
            double expiresAt = Number(oauth, "expiresAt") ?? 0;
            // Older Claude Code versions store milliseconds, newer ones seconds.
            DateTimeOffset expiry = expiresAt >= 1e11
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)expiresAt)
                : DateTimeOffset.FromUnixTimeSeconds((long)expiresAt);
            if (expiresAt > 0 && expiry < DateTimeOffset.UtcNow)
            {
                report.Message = "La sesión de Claude Code ha caducado. Ábrelo una vez (claude) para renovarla.";
                return report;
            }

            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                var request = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/api/oauth/usage");
                request.Headers["Authorization"] = "Bearer " + Text(oauth, "accessToken");
                request.Headers["anthropic-beta"] = "oauth-2025-04-20";
                request.Accept = "application/json";
                request.UserAgent = "TaskbarDock";
                request.Timeout = 10000;
                string body;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    body = reader.ReadToEnd();
                var usage = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(body);
                AddClaudeWindow(report, usage, "five_hour", "Sesión actual (5 h)");
                AddClaudeWindow(report, usage, "seven_day", "Semana · todos los modelos");
                AddClaudeWindow(report, usage, "seven_day_opus", "Semana · Opus");
                AddClaudeWindow(report, usage, "seven_day_sonnet", "Semana · Sonnet");
                if (report.Windows.Count == 0)
                    report.Message = "No hay datos de uso disponibles.";
            }
            catch (WebException exception)
            {
                var response = exception.Response as HttpWebResponse;
                report.Message = response != null && (int)response.StatusCode == 401
                    ? "La sesión de Claude Code ha caducado. Ábrelo una vez (claude) para renovarla."
                    : "No se pudo consultar el uso de Claude.";
                AppTrace.Log("Claude usage failed: " + exception.Message);
            }
            catch (Exception exception)
            {
                report.Message = "No se pudo consultar el uso de Claude.";
                AppTrace.Log("Claude usage failed: " + exception.Message);
            }
            return report;
        }

        private static void AddClaudeWindow(UsageReport report, Dictionary<string, object> usage, string key, string label)
        {
            object value;
            var window = usage.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
            if (window == null)
                return;
            double? utilization = Number(window, "utilization");
            DateTimeOffset resets;
            string detail = DateTimeOffset.TryParse(Text(window, "resets_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out resets)
                ? ResetText(resets)
                : null;
            report.Windows.Add(new UsageWindow
            {
                Label = label,
                Fraction = utilization.HasValue ? (float)(utilization.Value / 100.0) : (float?)null,
                Detail = detail
            });
        }

        // ------------------------------------------------------------ Codex

        public static UsageReport ReadCodex()
        {
            var report = new UsageReport { Product = "Codex" };
            string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            FileInfo latest = null;
            foreach (string folder in new[] { "sessions", "archived_sessions" })
            {
                string directory = Path.Combine(home, folder);
                if (!Directory.Exists(directory))
                    continue;
                // Session files are named rollout-<date>-<id>.jsonl; the name orders them
                // reliably, while modification times can be touched by migrations.
                foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories))
                    if (latest == null || string.CompareOrdinal(file.Name, latest.Name) > 0)
                        latest = file;
            }
            if (latest == null)
            {
                report.Message = "Usa Codex una vez para ver tus límites.";
                return report;
            }

            string line = null;
            try
            {
                using (var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    string current;
                    while ((current = reader.ReadLine()) != null)
                        if (current.IndexOf("\"rate_limits\"", StringComparison.Ordinal) >= 0)
                            line = current;
                }
            }
            catch (Exception exception)
            {
                AppTrace.Log("Reading Codex session failed: " + exception.Message);
            }
            if (line == null)
            {
                report.Message = "Aún no hay datos de límites en las sesiones de Codex.";
                return report;
            }

            var entry = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(line) as Dictionary<string, object>;
            Dictionary<string, object> limits = FindKey(entry, "rate_limits") as Dictionary<string, object>;
            if (limits == null)
            {
                report.Message = "Aún no hay datos de límites en las sesiones de Codex.";
                return report;
            }
            string plan = Text(limits, "plan_type");
            DateTimeOffset when;
            string updated = entry != null && DateTimeOffset.TryParse(Text(entry, "timestamp"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out when)
                ? "actualizado " + AgoText(when)
                : null;
            report.Subtitle = string.Join(" · ", new[]
            {
                string.IsNullOrEmpty(plan) ? null : "Plan " + char.ToUpper(plan[0]) + plan.Substring(1),
                updated
            }.Where(part => part != null).ToArray());
            AddCodexWindow(report, limits, "primary");
            AddCodexWindow(report, limits, "secondary");
            return report;
        }

        private static void AddCodexWindow(UsageReport report, Dictionary<string, object> limits, string key)
        {
            object value;
            var window = limits.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
            if (window == null)
                return;
            double minutes = Number(window, "window_minutes") ?? 0;
            string label = minutes <= 0 ? "Límite"
                : minutes <= 360 ? "Sesión (" + Math.Round(minutes / 60) + " h)"
                : minutes >= 10000 ? "Semana"
                : Math.Round(minutes / 1440) + " días";
            double used = Number(window, "used_percent") ?? 0;
            double? resetsAt = Number(window, "resets_at");
            string detail = null;
            if (resetsAt.HasValue)
            {
                DateTimeOffset resets = DateTimeOffset.FromUnixTimeSeconds((long)resetsAt.Value);
                if (resets < DateTimeOffset.UtcNow)
                {
                    // The window has rolled over since Codex last wrote it down.
                    used = 0;
                    detail = "Reiniciado";
                }
                else
                {
                    detail = ResetText(resets);
                }
            }
            report.Windows.Add(new UsageWindow { Label = label, Fraction = (float)(used / 100.0), Detail = detail });
        }

        // ------------------------------------------------------------ Helpers

        private static string ResetText(DateTimeOffset resets)
        {
            TimeSpan left = resets - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero)
                return "Reiniciado";
            string when = left.TotalHours < 24
                ? (left.TotalHours >= 1 ? (int)left.TotalHours + " h " : string.Empty) + left.Minutes + " min"
                : resets.ToLocalTime().ToString("ddd d MMM, H:mm", new CultureInfo("es-ES"));
            return "Se restablece " + (left.TotalHours < 24 ? "en " : "el ") + when;
        }

        private static string AgoText(DateTimeOffset when)
        {
            TimeSpan ago = DateTimeOffset.UtcNow - when;
            if (ago.TotalMinutes < 1) return "ahora";
            if (ago.TotalHours < 1) return "hace " + (int)ago.TotalMinutes + " min";
            if (ago.TotalDays < 1) return "hace " + (int)ago.TotalHours + " h";
            return "hace " + (int)ago.TotalDays + " días";
        }

        private static object FindKey(object node, string key)
        {
            var map = node as Dictionary<string, object>;
            if (map != null)
            {
                object value;
                if (map.TryGetValue(key, out value))
                    return value;
                foreach (object child in map.Values)
                {
                    object found = FindKey(child, key);
                    if (found != null)
                        return found;
                }
            }
            var list = node as IEnumerable;
            if (list != null && !(node is string))
            {
                foreach (object child in list)
                {
                    object found = FindKey(child, key);
                    if (found != null)
                        return found;
                }
            }
            return null;
        }

        private static string Text(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        private static double? Number(Dictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null)
                return null;
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
