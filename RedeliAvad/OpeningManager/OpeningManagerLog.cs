using System;
using System.IO;

namespace RedeliAvad
{
    /// <summary>
    /// Minimal file logger for the Opening Manager, consistent with the RK Tools style.
    /// Writes to %LocalAppData%\RK Tools\Revit.openings\opening-manager.log.
    /// All methods are best-effort and never throw.
    /// </summary>
    internal static class OpeningManagerLog
    {
        private static readonly object _lock = new object();

        private static string LogFilePath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RK Tools", "Revit.openings");
                return Path.Combine(dir, "opening-manager.log");
            }
        }

        public static void Info(string message) => Write("INFO ", message);
        public static void Warn(string message) => Write("WARN ", message);

        public static void Error(string message, Exception ex = null)
        {
            Write("ERROR", ex == null ? message : message + " :: " + ex.GetType().Name + ": " + ex.Message);
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (_lock)
                {
                    var path = LogFilePath;
                    var dir = Path.GetDirectoryName(path);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    // Simple size guard: start over above ~2 MB
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024) fi.Delete();

                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + message + Environment.NewLine);
                }
            }
            catch { /* logging must never break the plugin */ }
        }
    }
}
