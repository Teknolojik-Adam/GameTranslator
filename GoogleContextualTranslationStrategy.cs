using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;

namespace GameTranslatorUltimate
{
  
    public class GoogleContextualTranslationStrategy : ITranslationStrategy
    {
        public async Task<string> Translate(string text, string targetLanguage, HttpClient client, ILogger logger)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={targetLanguage}&dt=t&q={HttpUtility.UrlEncode(text)}";

            try
            {
                string responseJson = await client.GetStringAsync(url);
                using (JsonDocument doc = JsonDocument.Parse(responseJson))
                {
                    var sb = new StringBuilder();
                    var translations = doc.RootElement[0].EnumerateArray();
                    foreach (var translation in translations)
                    {
                        if (translation.GetArrayLength() > 0 && translation[0].ValueKind == JsonValueKind.String)
                        {
                            sb.Append(translation[0].GetString());
                        }
                    }
                    return sb.ToString().TrimEnd('\n');
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Google (Bağlamsal) isteği sırasında hata: {ex.Message}", ex);
                return null;
            }
        }
    }
}
