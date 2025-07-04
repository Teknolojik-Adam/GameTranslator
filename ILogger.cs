using System;

namespace P5S_ceviri
{
    public interface ILogger
    {
        void LogInformation(string message);
        void LogWarning(string message);
        void LogError(string message, Exception exception = null);
    }
}