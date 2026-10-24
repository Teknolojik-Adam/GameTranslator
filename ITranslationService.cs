using System;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public interface ITranslationService
    {
        Task<string> TranslateAsync(
            string text,
            string targetLanguage,
            Type strategyType = null);

        Task<string> TranslateAsync(
            string text,
            string targetLanguage,
            string strategyId);
    }
}