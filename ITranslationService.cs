using System.Threading.Tasks;

namespace P5S_ceviri
{
    public interface ITranslationService
    {
        Task<string> TranslateAsync(string text, string targetLanguage = "tr");
    }
}