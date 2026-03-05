using System;
using System.IO;

namespace FingerprintBridge
{
    /// <summary>
    /// Simple file + console logger for the bridge service.
    /// Writes to %LOCALAPPDATA%\FingerprintBridge\bridge.log
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new();
        private static readonly string _logDir;
        private static readonly string _logFile;

        public static string LogFilePath => _logFile;

        static Logger()
        {
            _logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FingerprintBridge"
            );

            Directory.CreateDirectory(_logDir);
            _logFile = Path.Combine(_logDir, "bridge.log");

            // Rotate log if > 5MB
            try
            {
                if (File.Exists(_logFile) && new FileInfo(_logFile).Length > 5 * 1024 * 1024)
                {
                    var backup = Path.Combine(_logDir, "bridge.old.log");
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(_logFile, backup);
                }
            }
            catch { }
        }

        public static void Info(string message) => Log("INF", message);
        public static void Warn(string message) => Log("WRN", message);
        public static void Error(string message) => Log("ERR", message);
        public static void Debug(string message)
        {
#if DEBUG
            Log("DBG", message);
#endif
        }

        private static void Log(string level, string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

            lock (_lock)
            {
                try
                {
                    File.AppendAllText(_logFile, line + Environment.NewLine);
                }
                catch { }
            }

            // Also write to console when running interactively
            Console.WriteLine(line);
        }
    }
}
