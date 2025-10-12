using System;

namespace P5S_ceviri
{

    public class ConsoleLogger : ILogger
    {
        private readonly object _lock = new object();

        public void LogInformation(string message)
        {
            lock (_lock)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"[INFO] {DateTime.Now:T}: {message}");
            }
        }

        public void LogWarning(string message)
        {
            lock (_lock)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[WARN] {DateTime.Now:T}: {message}");
            }
        }

        public void LogError(string message, Exception exception = null)
        {
            lock (_lock)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] {DateTime.Now:T}: {message} | Exception: {exception?.Message}");
                if (exception?.StackTrace != null)
                {
                    Console.WriteLine(exception.StackTrace);
                }
                Console.ResetColor();
            }
        }
    }
}