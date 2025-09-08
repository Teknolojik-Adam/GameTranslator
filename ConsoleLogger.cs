using System;
using System.IO;

namespace P5S_ceviri
{
    public class ConsoleLogger : ILogger, IDisposable
    {
        private static readonly object _logLock = new object();
        private readonly string _logFilePath;

        public ConsoleLogger()
        {
            try
            {
                _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_log.txt");
                RotateLogFile();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FATAL: Günlük döndürme sırasında günlük kaydı başlatılamadı. Hata: {ex.Message}");
            }
        }

        private void RotateLogFile()
        {
            const long maxLogSize = 1 * 1024 * 1024; // 1 MB
            if (File.Exists(_logFilePath))
            {
                var fileInfo = new FileInfo(_logFilePath);
                if (fileInfo.Length > maxLogSize)
                {
                    string oldLogPath = _logFilePath.Replace(".txt", ".old.txt");
                    if (File.Exists(oldLogPath))
                    {
                        File.Delete(oldLogPath);
                    }
                    File.Move(_logFilePath, oldLogPath);
                }
            }
        }

        private void WriteLog(string level, string message, Exception exception = null)
        {
            try
            {
                string logMessage = $"[{level}] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}";
                if (exception != null)
                {
                    logMessage += $"{Environment.NewLine}Exception: {exception}";
                }

                // Konsola yaz
                Console.WriteLine(logMessage);

                // Dosyaya yaz
                lock (_logLock)
                {
                    File.AppendAllText(_logFilePath, logMessage + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                
                Console.WriteLine($"FATAL: Günlük döndürme sırasında günlük kaydı başlatılamadı. Hata: {ex.Message}");
            }
        }

        public void LogInformation(string message)
        {
            WriteLog("INFO", message);
        }

        public void LogWarning(string message)
        {
            WriteLog("WARN", message);
        }

        public void LogError(string message, Exception exception = null)
        {
            WriteLog("ERROR", message, exception);
        }

        public void Dispose()
        {

        }
    }
}