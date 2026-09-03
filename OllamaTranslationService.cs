using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public class OllamaTranslationStrategy : ITranslationStrategy
    {
        private static readonly Dictionary<string, string> LanguageNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "tr", "Turkish" },
                { "tur", "Turkish" },
                { "tr-TR", "Turkish" },

                { "en", "English" },
                { "eng", "English" },
                { "en-US", "English" },
                { "en-GB", "English" },

                { "de", "German" },
                { "deu", "German" },
                { "ger", "German" },
                { "de-DE", "German" },

                { "fr", "French" },
                { "fra", "French" },
                { "fre", "French" },
                { "fr-FR", "French" },

                { "es", "Spanish" },
                { "spa", "Spanish" },
                { "es-ES", "Spanish" },

                { "it", "Italian" },
                { "ita", "Italian" },
                { "it-IT", "Italian" },

                { "pt", "Portuguese" },
                { "por", "Portuguese" },
                { "pt-BR", "Brazilian Portuguese" },
                { "pt-PT", "Portuguese" },

                { "ja", "Japanese" },
                { "jpn", "Japanese" },
                { "ja-JP", "Japanese" },

                { "ko", "Korean" },
                { "kor", "Korean" },
                { "ko-KR", "Korean" },

                { "ru", "Russian" },
                { "rus", "Russian" },
                { "ru-RU", "Russian" },

                { "zh", "Chinese" },
                { "chi_sim", "Simplified Chinese" },
                { "zh-Hans", "Simplified Chinese" },
                { "zh-CN", "Simplified Chinese" },
                { "chi_tra", "Traditional Chinese" },
                { "zh-Hant", "Traditional Chinese" },
                { "zh-TW", "Traditional Chinese" }
            };

        public string Name => "Ollama (Yerel YZ)";

        public async Task<string> Translate(
            string text,
            string targetLanguage,
            HttpClient client,
            ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            if (string.IsNullOrWhiteSpace(targetLanguage))
            {
                logger?.LogWarning(
                    "Ollama çevirisi için hedef dil belirtilmedi.");

                return string.Empty;
            }

            if (client == null)
                throw new ArgumentNullException(nameof(client));

            try
            {
                AppSettings appSettings =
                    ServiceContainer.GetService<AppSettings>();

                if (appSettings == null)
                {
                    logger?.LogError(
                        "Ollama ayarları alınamadı.");

                    return string.Empty;
                }

                if (string.IsNullOrWhiteSpace(appSettings.OllamaApiUrl))
                {
                    logger?.LogWarning(
                        "Ollama API adresi ayarlanmamış.");

                    return string.Empty;
                }

                if (string.IsNullOrWhiteSpace(appSettings.OllamaModelName))
                {
                    logger?.LogWarning(
                        "Ollama modeli seçilmemiş.");

                    return string.Empty;
                }

                string apiUrl =
                    NormalizeApiUrl(
                        appSettings.OllamaApiUrl);

                string languageName =
                    GetLanguageName(
                        targetLanguage);

                string prompt =
                    BuildPrompt(
                        text,
                        languageName);

                var requestBody =
                    new
                    {
                        model = appSettings.OllamaModelName.Trim(),
                        prompt = prompt,
                        stream = false,
                        options = new
                        {
                            temperature = 0.2
                        }
                    };

                string jsonPayload =
                    JsonSerializer.Serialize(
                        requestBody);

                using (var request =
                       new HttpRequestMessage(
                           HttpMethod.Post,
                           apiUrl))
                {
                    request.Content =
                        new StringContent(
                            jsonPayload,
                            Encoding.UTF8,
                            "application/json");

                    using (HttpResponseMessage response =
                           await client
                               .SendAsync(request)
                               .ConfigureAwait(false))
                    {
                        string responseString =
                            await response.Content
                                .ReadAsStringAsync()
                                .ConfigureAwait(false);

                        if (!response.IsSuccessStatusCode)
                        {
                            logger?.LogError(
                                $"Ollama HTTP hatası: {(int)response.StatusCode} {response.ReasonPhrase}. " +
                                $"Yanıt: {TrimForLog(responseString, 500)}");

                            return string.Empty;
                        }

                        return ParseResponse(
                            responseString,
                            logger);
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                logger?.LogError(
                    "Ollama sunucusuna bağlanılamadı.",
                    ex);

                return string.Empty;
            }
            catch (TaskCanceledException ex)
            {
                logger?.LogError(
                    "Ollama çeviri isteği zaman aşımına uğradı.",
                    ex);

                return string.Empty;
            }
            catch (JsonException ex)
            {
                logger?.LogError(
                    "Ollama yanıtı JSON olarak okunamadı.",
                    ex);

                return string.Empty;
            }
            catch (Exception ex)
            {
                logger?.LogError(
                    "Ollama yerel LLM üzerinden çeviri yapılırken hata oluştu.",
                    ex);

                return string.Empty;
            }
        }

        private static string NormalizeApiUrl(
            string apiUrl)
        {
            string value =
                apiUrl.Trim().TrimEnd('/');

            if (value.EndsWith(
                "/api/generate",
                StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (value.EndsWith(
                "/api",
                StringComparison.OrdinalIgnoreCase))
            {
                return value + "/generate";
            }

            return value + "/api/generate";
        }

        private static string GetLanguageName(
            string targetLanguage)
        {
            string value =
                targetLanguage.Trim();

            string languageName;

            if (LanguageNames.TryGetValue(
                value,
                out languageName))
            {
                return languageName;
            }

            return value;
        }

        private static string BuildPrompt(
            string text,
            string targetLanguage)
        {
            return
                "Translate the following video game text into " +
                targetLanguage +
                ".\n" +
                "Preserve the original meaning, tone, character names, numbers, formatting intent and game terminology.\n" +
                "Do not answer the text and do not explain it.\n" +
                "Do not add notes, quotes, prefixes or suffixes.\n" +
                "Return only the translated text.\n" +
                "Target language: " +
                targetLanguage +
                "\n\n" +
                text;
        }

        private static string ParseResponse(
            string responseString,
            ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(responseString))
                return string.Empty;

            using (JsonDocument document =
                   JsonDocument.Parse(responseString))
            {
                JsonElement root =
                    document.RootElement;

                JsonElement responseElement;

                if (!root.TryGetProperty(
                    "response",
                    out responseElement))
                {
                    logger?.LogWarning(
                        "Ollama yanıtında 'response' alanı bulunamadı.");

                    return string.Empty;
                }

                if (responseElement.ValueKind !=
                    JsonValueKind.String)
                {
                    logger?.LogWarning(
                        "Ollama 'response' alanı beklenen formatta değil.");

                    return string.Empty;
                }

                string translatedText =
                    responseElement.GetString();

                return CleanResponse(
                    translatedText);
            }
        }

        private static string CleanResponse(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string result =
                text.Trim();

            if (result.Length >= 2)
            {
                bool doubleQuoted =
                    result[0] == '"' &&
                    result[result.Length - 1] == '"';

                bool singleQuoted =
                    result[0] == '\'' &&
                    result[result.Length - 1] == '\'';

                if (doubleQuoted ||
                    singleQuoted)
                {
                    result =
                        result.Substring(
                            1,
                            result.Length - 2)
                        .Trim();
                }
            }

            return result;
        }

        private static string TrimForLog(
            string text,
            int maxLength)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            if (text.Length <= maxLength)
                return text;

            return text.Substring(
                       0,
                       maxLength) +
                   "...";
        }
    }
}