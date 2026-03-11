using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public class OllamaTranslationStrategy : ITranslationStrategy
    {
        public string Name => "Ollama (Yerel YZ)";

        public async Task<string> Translate(string text, string targetLanguage, HttpClient client, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            try
            {
                var appSettings = ServiceContainer.GetService<AppSettings>();
                
                string apiUrl = appSettings.OllamaApiUrl;
                if (!apiUrl.EndsWith("/api/generate"))
                {
                    apiUrl = apiUrl.TrimEnd('/') + "/api/generate";
                }

                string prompt = $"You are an expert game translator. Translate the following text into {targetLanguage}. The text may contain game characters or UI elements. Return ONLY the translated text, without any explanations, without any quotes, and without conversation filler.\n\nOriginal Text: {text}";

                var requestBody = new
                {
                    model = appSettings.OllamaModelName,
                    prompt = prompt,
                    stream = false,
                    options = new
                    {
                        temperature = 0.3
                    }
                };

                string jsonPayload = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await client.PostAsync(apiUrl, content))
                {
                    response.EnsureSuccessStatusCode();
                    
                    var responseString = await response.Content.ReadAsStringAsync();
                    
                    using (JsonDocument doc = JsonDocument.Parse(responseString))
                    {
                        if (doc.RootElement.TryGetProperty("response", out JsonElement responseElement))
                        {
                            string translatedText = responseElement.GetString()?.Trim();
                            return translatedText ?? string.Empty;
                        }
                    }
                }
                
                return string.Empty;
            }
            catch (Exception ex)
            {
                logger.LogError("Ollama yerel LLM Ã¼zerinden Ã§eviri yapÄ±lÄ±rken hata oluÅŸtu.", ex);
                return null;
            }
        }
    }
}

