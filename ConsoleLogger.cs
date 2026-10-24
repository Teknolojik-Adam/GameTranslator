using System;
using System.IO;
using System.Text;

namespace GameTranslatorUltimate
{
    public sealed class ConsoleLogger : ILogger
    {
        private const long MaxLogFileSize =
            5 * 1024 * 1024;

        private readonly object _lock;
        private readonly string _logFilePath;

        public ConsoleLogger()
        {
            _lock =
                new object();

            try
            {
                string baseDirectory =
                    AppDomain.CurrentDomain.BaseDirectory;

                if (string.IsNullOrWhiteSpace(
                    baseDirectory))
                {
                    baseDirectory =
                        ".";
                }

                string logsDirectory =
                    Path.Combine(
                        baseDirectory,
                        "logs");

                if (!Directory.Exists(
                    logsDirectory))
                {
                    Directory.CreateDirectory(
                        logsDirectory);
                }

                _logFilePath =
                    Path.Combine(
                        logsDirectory,
                        "app.log");
            }
            catch
            {
                _logFilePath =
                    null;
            }
        }

        public void LogInformation(
            string message)
        {
            WriteLog(
                "INFO",
                message,
                ConsoleColor.White,
                null);
        }

        public void LogWarning(
            string message)
        {
            WriteLog(
                "WARN",
                message,
                ConsoleColor.Yellow,
                null);
        }

        public void LogError(
            string message,
            Exception ex = null)
        {
            WriteLog(
                "ERROR",
                message,
                ConsoleColor.Red,
                ex);
        }

        private void WriteLog(
            string level,
            string message,
            ConsoleColor color,
            Exception exception)
        {
            string safeMessage =
                message ?? string.Empty;

            string timestamp =
                DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss.fff");

            var builder =
                new StringBuilder();

            builder.Append('[');
            builder.Append(level);
            builder.Append("] ");
            builder.Append(timestamp);
            builder.Append(": ");
            builder.Append(safeMessage);

            if (exception != null)
            {
                builder.AppendLine();
                builder.Append(
                    exception.ToString());
            }

            string output =
                builder.ToString();

            lock (_lock)
            {
                WriteToConsole(
                    output,
                    color);

                WriteToFile(
                    output);
            }
        }

        private static void WriteToConsole(
            string text,
            ConsoleColor color)
        {
            try
            {
                ConsoleColor previousColor =
                    Console.ForegroundColor;

                try
                {
                    Console.ForegroundColor =
                        color;

                    Console.WriteLine(
                        text);
                }
                finally
                {
                    try
                    {
                        Console.ForegroundColor =
                            previousColor;
                    }
                    catch
                    {
                        try
                        {
                            Console.ResetColor();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private void WriteToFile(
            string text)
        {
            if (string.IsNullOrWhiteSpace(
                _logFilePath))
            {
                return;
            }

            try
            {
                RotateLogIfNecessary();

                using (var stream =
                       new FileStream(
                           _logFilePath,
                           FileMode.Append,
                           FileAccess.Write,
                           FileShare.ReadWrite))
                using (var writer =
                       new StreamWriter(
                           stream,
                           new UTF8Encoding(false)))
                {
                    writer.WriteLine(
                        text);
                }
            }
            catch
            {
            }
        }

        private void RotateLogIfNecessary()
        {
            try
            {
                if (!File.Exists(
                    _logFilePath))
                {
                    return;
                }

                var info =
                    new FileInfo(
                        _logFilePath);

                if (info.Length <
                    MaxLogFileSize)
                {
                    return;
                }

                string directory =
                    Path.GetDirectoryName(
                        _logFilePath);

                string archiveName =
                    "app_" +
                    DateTime.Now.ToString(
                        "yyyyMMdd_HHmmss") +
                    ".log";

                string archivePath =
                    Path.Combine(
                        directory ?? ".",
                        archiveName);

                File.Move(
                    _logFilePath,
                    archivePath);
            }
            catch
            {
            }
        }
    }
}