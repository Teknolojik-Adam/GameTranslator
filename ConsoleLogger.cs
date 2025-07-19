using System;

namespace P5S_ceviri
{
    public class ConsoleLogger : ILogger
    {
        public void LogInformation(string message)
        {
            // Bilgi mesajlarını yazdırır.
            Console.WriteLine($"[INFO] {DateTime.Now:HH:mm:ss} - {message}");
        }

        public void LogWarning(string message)
        {
            // Uyarı mesajlarını yazdırır.
            Console.WriteLine($"[WARN] {DateTime.Now:HH:mm:ss} - {message}");
        }

        public void LogError(string message, Exception exception = null)
        {
            // Hata mesajlarını yazdırır.
            Console.WriteLine($"[ERROR] {DateTime.Now:HH:mm:ss} - {message}");

            // Eğer bir exception nesnesi de gönderildiyse detaylarını da yazdırır.
            if (exception != null)
            {
                Console.WriteLine($"Exception: {exception}");
            }
        }
    }
}