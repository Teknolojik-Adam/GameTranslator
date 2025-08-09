using HtmlAgilityPack;
using System;
using System.Collections.Concurrent;
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

    public class TranslationContextManager
    {
        private readonly Queue<string> _translationHistory = new Queue<string>();
        private const int MaxHistorySize = 10;

        public void AddToHistory(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            _translationHistory.Enqueue(text);
            if (_translationHistory.Count > MaxHistorySize)
            {
                _translationHistory.Dequeue();
            }
        }

        public string GetContextualPrompt(string currentText)
        {
            if (_translationHistory.Count == 0) return currentText;

            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine("Önceki çeviriler:");

            foreach (var historicalText in _translationHistory)
            {
                contextBuilder.AppendLine($"- {historicalText}");
            }

            contextBuilder.AppendLine();
            contextBuilder.AppendLine("Şimdi çevrilecek metin:");
            contextBuilder.AppendLine(currentText);
            contextBuilder.AppendLine();

            return contextBuilder.ToString();
        }

        public void ClearHistory()
        {
            _translationHistory.Clear();
        }
    }

    // Google çeviri stratejisi
    public class GoogleContextualTranslationStrategy : ITranslationStrategy
    {
        public async Task<string> Translate(string text, string targetLanguage, HttpClient client, ILogger logger)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            try
            {
                var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={targetLanguage}&dt=t&q={HttpUtility.UrlEncode(text)}";
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
                logger.LogError($"Google bağlamsal çeviri sırasında hata: {ex.Message}", ex);
                return null;
            }
        }
    }

    // Yer tutucu 
    public static class PlaceholderProtector
    {
        public static string Protect(string text, out Dictionary<string, string> placeholders)
        {
            var localPlaceholders = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                placeholders = localPlaceholders;
                return text;
            }

            int placeholderCounter = 0;

            string protectedText = Regex.Replace(text, @"\b\d+(?:\.\d+)?\b", match =>
            {
                string placeholder = $"__NUMBER_{placeholderCounter}__";
                localPlaceholders[placeholder] = match.Value;
                placeholderCounter++;
                return placeholder;
            });

            protectedText = Regex.Replace(protectedText, @"\b\d{4}-\d{2}-\d{2}\b|\b\d{2}/\d{2}/\d{4}\b|\b\d{1,2}\.\d{1,2}\.\d{4}\b", match =>
            {
                string placeholder = $"__DATE_{placeholderCounter}__";
                localPlaceholders[placeholder] = match.Value;
                placeholderCounter++;
                return placeholder;
            });

            protectedText = Regex.Replace(protectedText, @"\{[^}]*\}|\[[^\]]*\]", match =>
            {
                string placeholder = $"__TAG_{placeholderCounter}__";
                localPlaceholders[placeholder] = match.Value;
                placeholderCounter++;
                return placeholder;
            });

            protectedText = Regex.Replace(protectedText, @"<[^>]+>", match =>
            {
                string placeholder = $"__HTML_{placeholderCounter}__";
                localPlaceholders[placeholder] = match.Value;
                placeholderCounter++;
                return placeholder;
            });

            placeholders = localPlaceholders;
            return protectedText;
        }

        public static string Restore(string text, Dictionary<string, string> placeholders)
        {
            if (string.IsNullOrWhiteSpace(text) || placeholders == null || placeholders.Count == 0)
            {
                return text;
            }

            string result = text;
            foreach (var placeholder in placeholders.OrderByDescending(p => p.Key.Length))
            {
                result = result.Replace(placeholder.Key, placeholder.Value);
            }
            return result;
        }
    }

    // Cümle bölme ve birleştirme
    public class SentenceProcessor
    {
        private static readonly string[] SentenceEndings = { ".", "!", "?", "。", "！", "？" };
        private const int MaxSentenceLength = 500; // Çok uzun cümleleri böl

        public static List<string> SplitIntoSentences(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();

            var sentences = new List<string>();
            var currentSentence = new StringBuilder();

            foreach (char c in text)
            {
                currentSentence.Append(c);

                if (SentenceEndings.Contains(c.ToString()))
                {
                    string sentence = currentSentence.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(sentence))
                    {
                        if (sentence.Length > MaxSentenceLength)
                        {
                            sentences.AddRange(SplitLongSentence(sentence));
                        }
                        else
                        {
                            sentences.Add(sentence);
                        }
                    }
                    currentSentence.Clear();
                }
            }

            string remaining = currentSentence.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(remaining))
            {
                sentences.Add(remaining);
            }

            return sentences;
        }

        private static List<string> SplitLongSentence(string sentence)
        {
            var parts = new List<string>();
            var words = sentence.Split(' ');
            var currentPart = new StringBuilder();

            foreach (string word in words)
            {
                if (currentPart.Length + word.Length + 1 > MaxSentenceLength)
                {
                    if (currentPart.Length > 0)
                    {
                        parts.Add(currentPart.ToString().Trim());
                        currentPart.Clear();
                    }
                }
                currentPart.Append(word + " ");
            }

            if (currentPart.Length > 0)
            {
                parts.Add(currentPart.ToString().Trim());
            }

            return parts;
        }

        public static string MergeSentences(List<string> sentences)
        {
            if (sentences == null || sentences.Count == 0) return string.Empty;

            return string.Join(" ", sentences.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    public interface ITranslationStrategy
    {
        Task<string> Translate(string text, string targetLanguage, HttpClient client, ILogger logger);
    }

    #region Web Kazıma Stratejileri

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
        private readonly ConcurrentDictionary<string, string> _translationCache;
        private readonly TranslationCacheManager _cacheManager;
        private readonly List<ITranslationStrategy> _strategies;
        public List<StrategyInfo> AvailableStrategies { get; }
        private readonly TranslationContextManager _contextManager;
        private readonly ConcurrentDictionary<Type, int> _strategyFailureCounts = new ConcurrentDictionary<Type, int>();
        private readonly ConcurrentDictionary<Type, DateTime> _strategyBlockedUntil = new ConcurrentDictionary<Type, DateTime>();
        private const int CircuitBreakerFailureThreshold = 3;
        private static readonly TimeSpan CircuitBreakerBlockDuration = TimeSpan.FromMinutes(2);

        public AdvancedTranslationService(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _contextManager = new TranslationContextManager();
            _cacheManager = new TranslationCacheManager(_logger);
            _translationCache = new ConcurrentDictionary<string, string>(_cacheManager.LoadCache(), StringComparer.OrdinalIgnoreCase);

            try
            {
                if (_httpClient.Timeout == default) _httpClient.Timeout = TimeSpan.FromSeconds(15);
                if (!_httpClient.DefaultRequestHeaders.UserAgent.Any()) _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
                _httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"HTTP istemci başlıkları ayarlanırken uyarı: {ex.Message}");
            }

            _strategies = new List<ITranslationStrategy>
            {
                new GoogleContextualTranslationStrategy(),
                new GoogleWebTranslationStrategy(),
                new DeepLWebScrapingStrategy(),
                new BingWebTranslationStrategy(),
                new YandexWebScrapingStrategy()
            };

            AvailableStrategies = _strategies.Select(s => new StrategyInfo
            {
                Name = GetStrategyName(s),
                Type = s.GetType()
            }).ToList();
        }

        private string GetStrategyName(ITranslationStrategy s)
        {
            if (s is GoogleContextualTranslationStrategy) return "Google (Akıllı Çeviri)";
            return s.GetType().Name.Replace("Strategy", "").Replace("WebScraping", "").Replace("WebTranslation", "");
        }

        public async Task<string> TranslateAsync(string text, string targetLanguage = "tr", Type strategyType = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            string protectedText = PlaceholderProtector.Protect(text, out Dictionary<string, string> placeholders);
            string textToProcess = protectedText;

            var sentences = SentenceProcessor.SplitIntoSentences(textToProcess);
            var translatedSentences = new List<string>();
            string normalizedTarget = (targetLanguage ?? "tr").Trim().ToLowerInvariant();

            foreach (string sentence in sentences)
            {
                if (string.IsNullOrWhiteSpace(sentence)) continue;

                string cacheKey = GenerateCacheKey(sentence, normalizedTarget);
                if (_translationCache.TryGetValue(cacheKey, out var cachedTranslation))
                {
                    translatedSentences.Add(cachedTranslation);
                    continue;
                }

                var selectedStrategy = _strategies.FirstOrDefault(s => s.GetType() == strategyType) ?? _strategies.First();
                string textToSend = sentence;

                if (selectedStrategy is GoogleContextualTranslationStrategy)
                {
                    textToSend = _contextManager.GetContextualPrompt(sentence);
                }

                string translatedSentence = await TranslateWithStrategies(textToSend, normalizedTarget, strategyType);

                if (!string.IsNullOrWhiteSpace(translatedSentence))
                {
                    if (selectedStrategy is GoogleContextualTranslationStrategy && _contextManager.GetContextualPrompt("").Length > 0)
                    {
                        int lastDot = translatedSentence.LastIndexOf(". ");
                        if (lastDot > -1 && translatedSentence.Length > lastDot + 2)
                        {
                            translatedSentence = translatedSentence.Substring(lastDot + 2);
                        }
                    }
                    _translationCache[cacheKey] = translatedSentence;
                    translatedSentences.Add(translatedSentence);
                }
                else
                {
                    translatedSentences.Add(sentence);
                }
            }

            string mergedResult = SentenceProcessor.MergeSentences(translatedSentences);
            _contextManager.AddToHistory(text);
            string finalResult = PlaceholderProtector.Restore(mergedResult, placeholders);
            return finalResult;
        }

        private async Task<string> TranslateWithStrategies(string text, string targetLanguage, Type strategyType)
        {
            IEnumerable<ITranslationStrategy> strategiesToUse = _strategies;
            if (strategyType != null)
            {
                var selectedStrategy = _strategies.FirstOrDefault(s => s.GetType() == strategyType);
                if (selectedStrategy != null)
                {
                    strategiesToUse = new List<ITranslationStrategy> { selectedStrategy };
                }
            }

            foreach (var strategy in strategiesToUse)
            {
                if (IsStrategyBlocked(strategy.GetType())) continue;
                try
                {
                    var result = await AttemptTranslateWithRetries(strategy, text, targetLanguage);
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        RecordStrategySuccess(strategy.GetType());
                        _logger.LogInformation($"Metin başarıyla '{GetStrategyName(strategy)}' ile çevrildi.");
                        return result;
                    }
                    else
                    {
                        RecordStrategyFailure(strategy.GetType());
                    }
                }
                catch (Exception ex)
                {
                    RecordStrategyFailure(strategy.GetType());
                    _logger.LogWarning($"{GetStrategyName(strategy)} servisi hata verdi: {ex.Message}");
                }
            }

            _logger.LogError($"Tüm çeviri servisleri başarısız oldu: '{text}'", null);
            return null;
        }

        #region Mevcut Yardımcı Metotlar
        private async Task<string> AttemptTranslateWithRetries(ITranslationStrategy strategy, string text, string targetLanguage)
        {
            const int maxAttempts = 2;
            int attempt = 0;
            string result = null;
            while (attempt < maxAttempts && string.IsNullOrWhiteSpace(result))
            {
                attempt++;
                try
                {
                    result = await strategy.Translate(text, targetLanguage, _httpClient, _logger);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"{GetStrategyName(strategy)} denemesi hata verdi: {ex.Message}");
                }

                if (string.IsNullOrWhiteSpace(result) && attempt < maxAttempts)
                {
                    int delayMs = 250 * (int)Math.Pow(2, attempt - 1);
                    await Task.Delay(delayMs);
                }
            }
            return result;
        }

        public void SaveCacheToDisk()
        {
            _cacheManager.SaveCache(new Dictionary<string, string>(_translationCache));
        }
        public void ClearTranslationContext()
        {
            _contextManager.ClearHistory();
            _logger.LogInformation("Çeviri geçmişi temizlendi.");
        }

        private static string GenerateCacheKey(string input, string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(input)) return $"_{targetLanguage}";
            var normalized = new string(input.Trim().Replace('\r', ' ').Replace('\n', ' ').Select(c => char.IsWhiteSpace(c) ? ' ' : c).ToArray());
            normalized = string.Join(" ", normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return $"{normalized.ToLowerInvariant()}_{targetLanguage}";
        }

        private bool IsStrategyBlocked(Type strategyType)
        {
            if (_strategyBlockedUntil.TryGetValue(strategyType, out var until))
            {
                if (DateTime.UtcNow < until) return true;
                _strategyBlockedUntil.TryRemove(strategyType, out _);
            }
            return false;
        }

        private void RecordStrategyFailure(Type strategyType)
        {
            int failures = _strategyFailureCounts.AddOrUpdate(strategyType, 1, (_, current) => current + 1);
            if (failures >= CircuitBreakerFailureThreshold)
            {
                _strategyBlockedUntil[strategyType] = DateTime.UtcNow.Add(CircuitBreakerBlockDuration);
                _strategyFailureCounts[strategyType] = 0;
                _logger.LogWarning($"{strategyType.Name} devre kesici: {CircuitBreakerBlockDuration.TotalMinutes} dakika bloke edildi.");
            }
        }

        private void RecordStrategySuccess(Type strategyType)
        {
            _strategyFailureCounts.TryRemove(strategyType, out _);
            _strategyBlockedUntil.TryRemove(strategyType, out _);
        }
        #endregion
    }
}