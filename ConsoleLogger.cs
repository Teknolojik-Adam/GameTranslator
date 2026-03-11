using System;
using System.IO;
using System.Text;

namespace GameTranslatorUltimate
{

    public class ConsoleLogger : ILogger
    {
        private readonly object _lock = new object();
        private readonly string _logFilePath;

        public ConsoleLogger()
        {
            try
            {
                var logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "logs");
                if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);
                _logFilePath = Path.Combine(logsDir, "app.log");
            }
            catch
            {
                _logFilePath = null;
            }
        }

        public void LogInformation(string message)
        {
            var line = $"[INFO] {DateTime.Now:T}: {message}";
            lock (_lock)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine(line);
                }
                catch { }
                WriteLineToFile(line);
            }
        }

        public void LogWarning(string message)
        {
            var line = $"[WARN] {DateTime.Now:T}: {message}";
            lock (_lock)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(line);
                }
                catch { }
                WriteLineToFile(line);
            }
        }

        public void LogError(string message, Exception exception = null)
        {
            var sb = new StringBuilder();
            sb.Append($"[ERROR] {DateTime.Now:T}: {message}");
            if (exception != null) sb.Append($" | Exception: {exception.Message}");
            var line = sb.ToString();

            lock (_lock)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(line);
                    if (exception?.StackTrace != null)
                    {
                        Console.WriteLine(exception.StackTrace);
                    }
                }
                catch { }
                WriteLineToFile(line);
                if (exception?.StackTrace != null)
                {
                    WriteLineToFile(exception.StackTrace);
                }
                try { Console.ResetColor(); } catch { }
            }
        }

        private void WriteLineToFile(string line)
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;
            try
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}
