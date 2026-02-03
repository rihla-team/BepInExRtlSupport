using BepInEx.Logging;
using System;
using System.IO;
using System.Text;

namespace KoH2RTLFix
{
    internal static class PluginLog
    {
        private static readonly object _fileLock = new();
        private static StreamWriter _separateWriter;
        private static string _separateLogPath;

        private static void DisposeWriterNoLock()
        {
            try
            {
                _separateWriter?.Flush();
                _separateWriter?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _separateWriter = null;
                _separateLogPath = null;
            }
        }

        public static void Debug(string message) => Write(LogLevel.Debug, message);
        
        public static void Info(string message) => Write(LogLevel.Info, message);
        public static void Warning(string message) => Write(LogLevel.Warning, message);
        public static void Error(string message) => Write(LogLevel.Error, message);

        public static bool TextTraceEnabled
        {
            get
            {
                if (KoH2ArabicRTL.PluginConfig?.EnableTextTrace?.Value != true)
                    return false;

                return IsEnabled(LogLevel.Debug);
            }
        }

        public static bool DebugEnabled => IsEnabled(LogLevel.Debug);

        public static int TextTraceMaxChars => KoH2ArabicRTL.PluginConfig?.TextTraceMaxChars?.Value ?? 220;

        public static void Shutdown()
        {
            lock (_fileLock)
            {
                DisposeWriterNoLock();
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "KoH2RTLFix.plugin.log";

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            return name;
        }

        private static void EnsureSeparateWriter()
        {
            var cfg = KoH2ArabicRTL.PluginConfig;
            if (cfg?.EnableSeparateLogFile?.Value != true)
            {
                Shutdown();
                return;
            }

            string fileName = SanitizeFileName(cfg.SeparateLogFileName?.Value);
            string dir = BepInEx.Paths.CachePath;
            string path = Path.Combine(dir, fileName);

            lock (_fileLock)
            {
                if (_separateWriter != null && string.Equals(_separateLogPath, path, StringComparison.OrdinalIgnoreCase))
                    return;

                DisposeWriterNoLock();

                Directory.CreateDirectory(dir);
                var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _separateWriter = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };
                _separateLogPath = path;
            }
        }

        private static void WriteToSeparateFile(LogLevel level, string message)
        {
            try
            {
                EnsureSeparateWriter();
                if (_separateWriter == null)
                    return;

                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                    " [" + level + "] " + message;

                lock (_fileLock)
                {
                    _separateWriter?.WriteLine(line);
                }
            }
            catch
            {
            }
        }

        public static void Write(LogLevel level, string message)
        {
            var log = KoH2ArabicRTL.Log;
            if (log == null)
                return;

            if (!IsEnabled(level))
                return;

            log.Log(level, message);
            WriteToSeparateFile(level, message);
        }

        private static bool IsEnabled(LogLevel messageLevel)
        {
            var configLevel = KoH2ArabicRTL.PluginConfig?.LoggingLevel?.Value ?? LogLevel.Info;

            if (configLevel == LogLevel.All)
                return true;

            if (configLevel == LogLevel.None)
                return false;

            int configSeverity = Severity(configLevel);
            int msgSeverity = Severity(messageLevel);

            if (configSeverity < 0 || msgSeverity < 0)
                return true;

            return msgSeverity <= configSeverity;
        }

        private static int Severity(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Fatal:
                    return 0;
                case LogLevel.Error:
                    return 1;
                case LogLevel.Warning:
                    return 2;
                case LogLevel.Message:
                    return 3;
                case LogLevel.Info:
                    return 4;
                case LogLevel.Debug:
                    return 5;
                default:
                    return -1;
            }
        }
    }
}
