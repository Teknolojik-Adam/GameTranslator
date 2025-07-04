using System;

namespace P5S_ceviri
{
    // Bu sınıf, ILogger arayüzünü uygular ve log mesajlarını konsola yazar.
    public class ConsoleLogger : ILogger
    {
        public void LogInformation(string message)
        {
            // Bilgi mesajlarını formatlayarak konsola yazdırır.
            Console.WriteLine($"[INFO] {DateTime.Now:HH:mm:ss} - {message}");
        }

        public void LogWarning(string message)
        {
            // Uyarı mesajlarını formatlayarak konsola yazdırır.
            Console.WriteLine($"[WARN] {DateTime.Now:HH:mm:ss} - {message}");
        }

        public void LogError(string message, Exception exception = null)
        {
            // Hata mesajlarını formatlayarak konsola yazdırır.
            Console.WriteLine($"[ERROR] {DateTime.Now:HH:mm:ss} - {message}");

            // Eğer bir exception nesnesi de gönderildiyse, detaylarını da yazdırır.
            if (exception != null)
            {
                Console.WriteLine($"Exception: {exception}");
            }
        }
    }
}