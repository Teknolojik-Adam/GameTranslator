using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace P5S_ceviri
{
    public class StrategyInfo
    {
        public string Name { get; set; }
        public Type Type { get; set; }
    }

    public class AdvancedTranslationService : ITranslationService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly Dictionary<string, string> _translationCache = new Dictionary<string, string>();
        private readonly List<ITranslationStrategy> _strategies;
        public List<StrategyInfo> AvailableStrategies { get; }

        public AdvancedTranslationService(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _strategies = new List<ITranslationStrategy>
            {
                new DeepLWebScrapingStrategy(),
                new GoogleWebTranslationStrategy(),
                new YandexWebScrapingStrategy(),
                new BingWebTranslationStrategy()
            };

            AvailableStrategies = new List<StrategyInfo>
            {
                new StrategyInfo { Name = "DeepL (Web)", Type = typeof(DeepLWebScrapingStrategy) },
                new StrategyInfo { Name = "Google (Web)", Type = typeof(GoogleWebTranslationStrategy) },
                new StrategyInfo { Name = "Yandex (Web)", Type = typeof(YandexWebScrapingStrategy) },
                new StrategyInfo { Name = "Bing (Web)", Type = typeof(BingWebTranslationStrategy) }
            };
        }

        // Bu metot, güncellenmiş ITranslationService arayüzündeki tanıma uyuyor.
        public async Task<string> TranslateAsync(string text, string targetLanguage = "tr", Type strategyType = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var cacheKey = $"{text}_{targetLanguage}";
            if (_translationCache.TryGetValue(cacheKey, out var cachedTranslation))
            {
                return cachedTranslation;
            }

            IEnumerable<ITranslationStrategy> strategiesToUse = _strategies;

            if (strategyType != null)
            {
                var selectedStrategy = _strategies.FirstOrDefault(s => s.GetType() == strategyType);
                if (selectedStrategy != null)
                {
                    strategiesToUse = new List<ITranslationStrategy> { selectedStrategy };
                }
                else
                {
                    _logger.LogWarning($"İstenen çeviri servisi '{strategyType.Name}' bulunamadı. Varsayılan sıra denenecek.");
                }
            }

            const int chunkSize = 4500;
            var chunks = SplitTextIntoChunks(text, chunkSize);
            var sb = new StringBuilder();

            foreach (var chunk in chunks)
            {
                string result = string.Empty;
                foreach (var strategy in strategiesToUse)
                {
                    try
                    {
                        result = await strategy.Translate(chunk, targetLanguage, _httpClient, _logger);
                        if (!string.IsNullOrWhiteSpace(result))
                        {
                            _logger.LogInformation($"Metin başarıyla '{strategy.GetType().Name}' ile çevrildi.");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"{strategy.GetType().Name} servisi hata verdi: {ex.Message}");
                    }
                }

                if (string.IsNullOrWhiteSpace(result))
                {
                    _logger.LogError($"Tüm çeviri servisleri başarısız oldu: '{chunk}'", null);
                    result = $"[Çeviri Başarısız: {chunk}]";
                }

                if (sb.Length > 0) sb.Append(" ");
                sb.Append(result);
            }

            var finalTranslation = sb.ToString();
            _translationCache[cacheKey] = finalTranslation;
            return finalTranslation;
        }

        private List<string> SplitTextIntoChunks(string text, int maxChunkSize)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(text)) return chunks;
            for (int i = 0; i < text.Length; i += maxChunkSize)
            {
                int size = Math.Min(maxChunkSize, text.Length - i);
                chunks.Add(text.Substring(i, size));
            }
            return chunks;
        }
    }

    public interface ITranslationStrategy
    {
        Task<string> Translate(string text, string targetLanguage, HttpClient client, ILogger logger);
    }

    // ... Diğer strateji sınıfları burada değişmeden kalır ...
    #region Web Kazıma Stratejileri

    public class DeepLWebScrapingStrategy : ITranslationStrategy
    {
        public async Task<string> Translate(string text, string targetLanguage, HttpClient client, ILogger logger)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            try
            {
                var url = "https://www2.deepl.com/jsonrpc";
                var requestBody = new
                {
                    jsonrpc = "2.0",
                    method = "LMT_handle_jobs",
                    @params = new
                    {
                        jobs = new[] {
                            new {
                                kind = "default",
                                raw_en_sentence = text
                            }
                        },
                        lang = new
                        {
                            target_lang = targetLanguage.ToUpper()
                        }
                    }
                };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content);

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("DeepL Web isteği başarısız oldu.");
                    return null;
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                using (var doc = JsonDocument.Parse(responseJson))
                {
                    var translatedText = doc.RootElement
                                            .GetProperty("result")
                                            .GetProperty("translations")[0]
                                            .GetProperty("beams")[0]
                                            .GetProperty("postprocessed_sentence")
                                            .GetString();
                    return translatedText;
                }
            }
            catch (Exception ex)
            {
                logger.LogError("DeepL web kazıma sırasında hata.", ex);
                return null;
            }
        }
    }

    public class YandexWebScrapingStrategy : ITranslationStrategy
    {
        public async Task<string> Translate(string text, string targetLanguage, HttpClient client, ILogger logger)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            try
            {
                var url = $"https://translate.yandex.com/?source_lang=auto&target_lang={targetLanguage}&text={HttpUtility.UrlEncode(text)}";

                var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
                requestMessage.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

                var response = await client.SendAsync(requestMessage);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning($"Yandex Web isteği başarısız oldu. Durum Kodu: {response.StatusCode}");
                    return null;
                }

                var html = await response.Content.ReadAsStringAsync();

                var match = Regex.Match(html, @"<span data-complaint-type=""translation""[^>]*>(.*?)</span>", RegexOptions.Singleline);
                if (match.Success)
                {
                    var translatedText = HttpUtility.HtmlDecode(match.Groups[1].Value);
                    return translatedText;
                }

                logger.LogWarning("Yandex sayfa yapısında çeviri bulunamadı. (Yapı değişmiş olabilir)");
                return null;
            }
            catch (Exception ex)
            {
                logger.LogError("Yandex web kazıma sırasında hata.", ex);
                return null;
            }
        }
    }

    public class GoogleWebTranslationStrategy : ITranslationStrategy
    {
        public async Task<string> Translate(string text, string targetLanguage, HttpClient client, ILogger logger)
        {
            text = text.Replace(Environment.NewLine, " ").Trim();
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
                logger.LogError($"Google isteği sırasında hata: {ex.Message}", ex);
                return null;
            }
        }
    }

    public class BingWebTranslationStrategy : ITranslationStrategy
    {
        public async Task<string> Translate(string text, string targetLanguage, HttpClient client, ILogger logger)
        {
            text = text.Replace(Environment.NewLine, " ").Trim();
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var url = $"https://www.bing.com/translator?text={HttpUtility.UrlEncode(text)}&from=auto&to={targetLanguage}";
            try
            {
                string html = await client.GetStringAsync(url);
                const string marker = "id=\"tta_output_ta\"";
                int start = html.IndexOf(marker, StringComparison.Ordinal);
                if (start == -1) return null;

                start = html.IndexOf('>', start) + 1;
                if (start < 1) return null;

                int end = html.IndexOf('<', start);
                if (end <= start) return null;

                string translatedText = html.Substring(start, end - start);
                return HttpUtility.HtmlDecode(translatedText);
            }
            catch (Exception ex)
            {
                logger.LogError($"Bing isteği sırasında hata: {ex.Message}");
                return null;
            }
        }
    }
    #endregion
}