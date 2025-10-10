using System;

namespace P5S_ceviri
{
    /// <summary>
    /// Uygulama genelinde loglama işlemleri için standart arayüz.
    /// </summary>
    public interface ILogger
    {
        void LogInformation(string message);
        void LogWarning(string message);
        void LogError(string message, Exception ex = null);
    }
}