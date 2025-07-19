using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using HtmlAgilityPack;

namespace P5S_ceviri
{

    public class StrategyInfo
    {
        public string Name { get; set; }
        public Type Type { get; set; }
    }


    public interface ITranslationStrategy
    {

        Task<string> Translate(string text, string targetLanguage, HttpClient client, ILogger logger);
    }

    #region Web Kazıma Stratejileri

    /// DeepL web sitesini kazıyarak çeviri yapar
    public class DeepLWebScrapingStrategy : ITranslationStrategy
    {
        public async Task<string> Translate(string text, string targetLanguage, HttpClient client, ILogger logger)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            try
            {
                var url = "https://www2.deepl.com/jsonrpc";
                var requestBody = new { jsonrpc = "2.0", method = "LMT_handle_jobs", @params = new { jobs = new[] { new { kind = "default", raw_en_sentence = text } }, lang = new { target_lang = targetLanguage.ToUpper() } } };
                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content);
                if (!response.IsSuccessStatusCode) return null;
                var responseJson = await response.Content.ReadAsStringAsync();
                using (var doc = JsonDocument.Parse(responseJson)) { return doc.RootElement.GetProperty("result").GetProperty("translations")[0].GetProperty("beams")[0].GetProperty("postprocessed_sentence").GetString(); }
            }
            catch (Exception ex) { logger.LogError("DeepL web kazıma sırasında hata.", ex); return null; }
        }
    }

    /// Yandex Translate web sitesini kazıyarak çeviri yapar.
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
                if (!response.IsSuccessStatusCode) return null;

                var html = await response.Content.ReadAsStringAsync();

                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);

                var translationNode = htmlDoc.DocumentNode.SelectSingleNode("//span[@data-complaint-type='translation']");

                if (translationNode != null)
                {
                    return HttpUtility.HtmlDecode(translationNode.InnerText);
                }

                logger.LogWarning("Yandex sayfasında çeviri metni bulunamadı. (Yapı değişmiş olabilir)");
                return null;
            }
            catch (Exception ex) { logger.LogError("Yandex web kazıma sırasında hata.", ex); return null; }
        }
    }

    /// Google'ın web API'sini kullanarak çeviri yapar.
    public class GoogleWebTranslationStrategy : ITranslationStrategy
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
                    foreach (var translation in translations) { if (translation.GetArrayLength() > 0 && translation[0].ValueKind == JsonValueKind.String) { sb.Append(translation[0].GetString()); } }
                    return sb.ToString().TrimEnd('\n');
                }
            }
            catch (Exception ex) { logger.LogError($"Google isteği sırasında hata: {ex.Message}", ex); return null; }
        }
    }

    /// Bing Translator web sitesini kazıyarak çevirisi
    public class BingWebTranslationStrategy : ITranslationStrategy
    {
        public async Task<string> Translate(string text, string targetLanguage, HttpClient client, ILogger logger)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var url = $"https://www.bing.com/translator?text={HttpUtility.UrlEncode(text)}&from=auto&to={targetLanguage}";
            try
            {
                string html = await client.GetStringAsync(url);

                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);

                var translationNode = htmlDoc.GetElementbyId("tta_output_ta");

                if (translationNode != null)
                {
                    return HttpUtility.HtmlDecode(translationNode.InnerText);
                }

                logger.LogWarning("Bing sayfasında çeviri metni bulunamadı. (ID 'tta_output_ta' değişmiş olabilir)");
                return null;
            }
            catch (Exception ex) { logger.LogError($"Bing isteği sırasında hata: {ex.Message}", ex); return null; }
        }
    }
    #endregion

    public class AdvancedTranslationService : ITranslationService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;

        private readonly Dictionary<string, string> _translationCache;
        private readonly TranslationCacheManager _cacheManager;

        private readonly List<ITranslationStrategy> _strategies;
        public List<StrategyInfo> AvailableStrategies { get; }

        public AdvancedTranslationService(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            _cacheManager = new TranslationCacheManager(_logger);
            _translationCache = _cacheManager.LoadCache();

            _strategies = new List<ITranslationStrategy>
            {
                new DeepLWebScrapingStrategy(),
                new GoogleWebTranslationStrategy(),
                new YandexWebScrapingStrategy(),
                new BingWebTranslationStrategy()
            };

            AvailableStrategies = _strategies.Select(s => new StrategyInfo
            {
                Name = s.GetType().Name.Replace("Strategy", "").Replace("WebScraping", "").Replace("WebTranslation", ""),
                Type = s.GetType()
            }).ToList();
        }

        public async Task<string> TranslateAsync(string text, string targetLanguage = "tr", Type strategyType = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            var cacheKey = $"{text.ToLower()}_{targetLanguage}";
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
            }

            string finalTranslation = string.Empty;
            foreach (var strategy in strategiesToUse)
            {
                try
                {
                    string result = await strategy.Translate(text, targetLanguage, _httpClient, _logger);
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        finalTranslation = result;
                        _logger.LogInformation($"Metin başarıyla '{strategy.GetType().Name}' ile çevrildi.");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"{strategy.GetType().Name} servisi hata verdi: {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(finalTranslation))
            {
                _logger.LogError($"Tüm çeviri servisleri başarısız oldu: '{text}'", null);
                return $"[Çeviri Başarısız: {text}]";
            }

            _translationCache[cacheKey] = finalTranslation;
            return finalTranslation;
        }

        public void SaveCacheToDisk()
        {
            _cacheManager.SaveCache(_translationCache);
        }
    }
}