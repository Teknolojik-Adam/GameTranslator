using HtmlAgilityPack;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
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
        private const int MaxHistorySize = 3;

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
            
            // contextBuilder.AppendLine("Önceki çeviriler:");

            foreach (var historicalText in _translationHistory)
            {
                // contextBuilder.AppendLine($"- {historicalText}");
            }

            contextBuilder.AppendLine();
            // contextBuilder.AppendLine("Şimdi çevrilecek metin:");
            contextBuilder.AppendLine(currentText);
            contextBuilder.AppendLine();

            return contextBuilder.ToString();
        }

        public void ClearHistory()
        {
            _translationHistory.Clear();
        }
    }

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

            // Sayıları koruma ()
            string protectedText = Regex.Replace(text, @"\b\d+(?:\.\d+)?\b", match =>
            {
                string placeholder = $"__NUMBER_{placeholderCounter}__";
                localPlaceholders[placeholder] = match.Value;
                placeholderCounter++;
                return placeholder;
            });

            // Tarihleri koruma ()
            protectedText = Regex.Replace(protectedText, @"\b\d{4}-\d{2}-\d{2}\b|\b\d{2}/\d{2}/\d{4}\b|\b\d{1,2}\.\d{1,2}\.\d{4}\b", match =>
            {
                string placeholder = $"__DATE_{placeholderCounter}__";
                localPlaceholders[placeholder] = match.Value;
                placeholderCounter++;
                return placeholder;
            });

            // Etiketleri koruma (örnek: {tag}, [tag])
            protectedText = Regex.Replace(protectedText, @"\{[^}]*\}|\[[^\]]*\]", match =>
            {
                string placeholder = $"__TAG_{placeholderCounter}__";
                localPlaceholders[placeholder] = match.Value;
                placeholderCounter++;
                return placeholder;
            });

            // HTML etiketler
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
            // Yer tutucuları orijinal değerlerle değiştir (uzunluk sırasına göre)
            foreach (var placeholder in placeholders.OrderByDescending(p => p.Key.Length))
            {
                result = result.Replace(placeholder.Key, placeholder.Value);
            }
            return result;
        }
    }

 
    public class SentenceProcessor
    {
        private static readonly string[] SentenceEndings = { ".", "!", "?", "。", "！", "？" };
        private const int MaxSentenceLength = 500;

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
                            // Uzun cümleleri daha küçük parçalara böl
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

            // Kalan kısmı ekle
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
        string Name { get; }
        Task<string> Translate(string text, string targetLanguage, HttpClient client, ILogger logger);
    }

    #region Web Kazıma Stratejileri

    public class GoogleTranslationStrategy : ITranslationStrategy
    {
        public string Name { get; }
        public bool IsContextual { get; }

        public GoogleTranslationStrategy(bool isContextual)
        {
            IsContextual = isContextual;
            Name = isContextual ? "Google (Akıllı Çeviri)" : "Google";
        }

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
                logger.LogError($"{Name} çevirisi sırasında hata: {ex.Message}", ex);
                return null;
            }
        }
    }

    public class DeepLWebScrapingStrategy : ITranslationStrategy
    {
        public string Name => "DeepL";
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
            catch (Exception ex) { logger.LogError($"{Name} web kazıma sırasında hata.", ex); return null; }
        }
    }

    public class YandexWebScrapingStrategy : ITranslationStrategy
    {
        public string Name => "Yandex";
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

                logger.LogWarning($"{Name} sayfasında çeviri metni bulunamadı. (Yapı değişmiş olabilir)");
                return null;
            }
            catch (Exception ex) { logger.LogError($"{Name} web kazıma sırasında hata.", ex); return null; }
        }
    }

    public class BingWebTranslationStrategy : ITranslationStrategy
    {
        public string Name => "Bing";
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

                logger.LogWarning($"{Name} sayfasında çeviri metni bulunamadı. (ID 'tta_output_ta' değişmiş olabilir)");
                return null;
            }
            catch (Exception ex) { logger.LogError($"{Name} isteği sırasında hata: {ex.Message}", ex); return null; }
        }
    }

    #endregion

    public class TranslationCompletedEventArgs : EventArgs
    {
        public string OriginalText { get; set; }
        public string TranslatedText { get; set; }
        public string TargetLanguage { get; set; }
        public DateTime TranslationTime { get; set; }
        public double Confidence { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class TranslationProgressEventArgs : EventArgs
    {
        public int ProgressPercentage { get; set; }
        public string CurrentSentence { get; set; }
        public int TotalSentences { get; set; }
        public int CompletedSentences { get; set; }
    }

    public class AdvancedTranslationService : ITranslationService, IBatchTranslationService, IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly TranslationCacheManager _cacheManager;
        private readonly TranslationContextManager _contextManager;
        private readonly ConcurrentDictionary<string, string> _translationCache;
        private readonly ConcurrentDictionary<Type, int> _strategyFailureCounts = new ConcurrentDictionary<Type, int>();
        private readonly ConcurrentDictionary<Type, DateTime> _strategyBlockedUntil = new ConcurrentDictionary<Type, DateTime>();
        private readonly List<ITranslationStrategy> _strategies;

        private const int CircuitBreakerFailureThreshold = 3; // Devre kesici başarısızlık eşiği
        private static readonly TimeSpan CircuitBreakerBlockDuration = TimeSpan.FromMinutes(2); // Devre kesici blok süresi
        private const int MaxParallelTranslations = 5; // Maksimum paralel çeviri sayısı
        private const int CacheSizeLimit = 10000; // Önbellek boyut sınırı

        private bool _disposed = false;

        public List<StrategyInfo> AvailableStrategies { get; }

        // Event'ler
        public event EventHandler<TranslationCompletedEventArgs> TranslationCompleted;
        public event EventHandler<TranslationProgressEventArgs> TranslationProgress;

        public AdvancedTranslationService(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            
            _contextManager = new TranslationContextManager();
            _cacheManager = new TranslationCacheManager(_logger);

            // Zamanı dolmuş önbellek girdilerini temizle
            _cacheManager.ExpireEntries();

            // Önbelleği boyut sınırıyla yüklemek iççin
            var loadedCache = _cacheManager.LoadCache();
            _translationCache = new ConcurrentDictionary<string, string>(
                loadedCache.AsEnumerable().Take(CacheSizeLimit).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                StringComparer.OrdinalIgnoreCase
            );

            // HTTP istemcisi
            ConfigureHttpClient();

            // Çeviri stratejileri
            _strategies = new List<ITranslationStrategy>
            {
                new GoogleTranslationStrategy(isContextual: true),
                new GoogleTranslationStrategy(isContextual: false),
                new DeepLWebScrapingStrategy(),
                new BingWebTranslationStrategy(),
                new YandexWebScrapingStrategy()
            };

            AvailableStrategies = _strategies.Select(s => new StrategyInfo
            {
                Name = s.Name,
                Type = s.GetType()
            }).ToList();
        }

        private void ConfigureHttpClient()
        {
            try
            {
                if (_httpClient.Timeout == default)
                    _httpClient.Timeout = TimeSpan.FromSeconds(15);

                if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
                    _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                _httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"HTTP istemci yapılandırma uyarısı: {ex.Message}");
            }
        }

        public async Task<string> TranslateAsync(string text, string targetLanguage, Type strategyType = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            if (string.IsNullOrWhiteSpace(targetLanguage))
                targetLanguage = "tr";

            // metni cümlelere ayır
            string protectedText = PlaceholderProtector.Protect(text, out Dictionary<string, string> placeholders);
            var sentences = SentenceProcessor.SplitIntoSentences(protectedText);

            if (sentences.Count == 0)
                return string.Empty;

            // Cümleleri paralel olarak çevir
            var translatedSentences = new ConcurrentBag<string>();
            string normalizedTarget = targetLanguage.Trim().ToLowerInvariant();

            using (var semaphore = new SemaphoreSlim(MaxParallelTranslations))
            {
                var tasks = sentences.Select(async sentence =>
                {
                    if (string.IsNullOrWhiteSpace(sentence))
                        return;

                    await semaphore.WaitAsync();
                    try
                    {
                        string cacheKey = GenerateCacheKey(sentence, normalizedTarget);

                        // TranslationCacheManager'dan önbelleği kontrol et
                        var cachedTranslation = _cacheManager.GetTranslation(cacheKey);
                        if (!string.IsNullOrEmpty(cachedTranslation))
                        {
                            translatedSentences.Add(cachedTranslation);
                            _logger.LogInformation($"Önbellekten çeviri alındı: {sentence.Substring(0, Math.Min(30, sentence.Length))}...");
                            return;
                        }

                        // ConcurrentDictionary'den de kontrol et (eski önbellek)
                        if (_translationCache.TryGetValue(cacheKey, out var cachedTranslation2))
                        {
                            translatedSentences.Add(cachedTranslation2);
                            return;
                        }

                        // Strateji seç veya varsayılanı kullan
                        var selectedStrategy = _strategies.FirstOrDefault(s => s.GetType() == strategyType) ?? _strategies.First();
                        string textToSend = sentence;

                        // Bağlamsal çeviri için önceki çevirileri ekle
                        if (selectedStrategy is GoogleTranslationStrategy gts && gts.IsContextual)
                        {
                            textToSend = _contextManager.GetContextualPrompt(sentence);
                        }

                        string translatedSentence = await TranslateWithStrategies(textToSend, normalizedTarget, strategyType);

                        if (!string.IsNullOrWhiteSpace(translatedSentence))
                        {
                            // Bağlamsal çeviriden sadece son cümleyi al
                            if (selectedStrategy is GoogleTranslationStrategy gts2 && gts2.IsContextual &&
                                _contextManager.GetContextualPrompt("").Length > 0)
                            {
                                int lastDot = translatedSentence.LastIndexOf(". ");
                                if (lastDot > -1 && translatedSentence.Length > lastDot + 2)
                                {
                                    translatedSentence = translatedSentence.Substring(lastDot + 2);
                                }
                            }

                            // TranslationCacheManager'a kaydet
                            _cacheManager.AddTranslation(cacheKey, translatedSentence);
                            
                            // Hem yeni hem eski önbelleğe ekle
                            _translationCache[cacheKey] = translatedSentence;
                            translatedSentences.Add(translatedSentence);
                            
                            _logger.LogInformation($"Çeviri önbelleğe eklendi: {sentence.Substring(0, Math.Min(30, sentence.Length))}...");
                        }
                        else
                        {
                            // Çeviri başarısız olursa orijinal cümleyi yazdirma
                            translatedSentences.Add(sentence);
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
            }

            // Geçmişe ekle
            _contextManager.AddToHistory(text);

            // Cümleleri birleştir ve  geri yükle
            string mergedResult = SentenceProcessor.MergeSentences(translatedSentences.ToList());
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
                if (IsStrategyBlocked(strategy.GetType()))
                    continue;

                try
                {
                    var result = await AttemptTranslateWithRetries(strategy, text, targetLanguage);

                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        RecordStrategySuccess(strategy.GetType());
                        _logger.LogInformation($"Metin başarıyla '{strategy.Name}' ile çevrildi.");
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
                    _logger.LogWarning($"{strategy.Name} servisi başarısız oldu: {ex.Message}");
                }
            }

            _logger.LogError($"Tüm çeviri servisleri şu metin için başarısız oldu: '{text}'", null);
            return null;
        }

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
                    _logger.LogWarning($"{strategy.Name} denemesi {attempt} başarısız oldu: {ex.Message}");
                }

                if (string.IsNullOrWhiteSpace(result) && attempt < maxAttempts)
                {
                    int delayMs = 250 * (int)Math.Pow(2, attempt - 1);
                    await Task.Delay(delayMs);
                }
            }

            return result;
        }

        private bool IsStrategyBlocked(Type strategyType)
        {
            if (_strategyBlockedUntil.TryGetValue(strategyType, out var until))
            {
                if (DateTime.UtcNow < until)
                    return true;

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
                _logger.LogWarning($"{strategyType.Name} devre kesici: {CircuitBreakerBlockDuration.TotalMinutes} dakika boyunca bloke edildi.");
            }
        }

        private void RecordStrategySuccess(Type strategyType)
        {
            _strategyFailureCounts.TryRemove(strategyType, out _);
            _strategyBlockedUntil.TryRemove(strategyType, out _);
        }

        private static string GenerateCacheKey(string input, string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(input))
                return $"_{targetLanguage}";

            var normalized = new string(input.Trim()
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Select(c => char.IsWhiteSpace(c) ? ' ' : c)
                .ToArray());

            normalized = string.Join(" ", normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return $"{normalized.ToLowerInvariant()}_{targetLanguage}";
        }

        public void SaveCacheToDisk()
        {
            try
            {
                // TranslationCacheManager'a çevirileri kaydet
                _cacheManager.SaveCache(new Dictionary<string, string>(_translationCache));
                _logger.LogInformation($"{_translationCache.Count} adet çeviri diske kaydedildi");
            }
            catch (Exception ex)
            {
                _logger.LogError("Önbellek kaydedilirken hata oluştu", ex);
            }
        }

        public void ClearExpiredCache()
        {
            try
            {
                _cacheManager.ExpireEntries();
                
                // Eski önbelleği yeniden yükle
                var freshCache = _cacheManager.LoadCache();
                
                // ConcurrentDictionary'yi güncelle
                _translationCache.Clear();
                foreach (var kvp in freshCache)
                {
                    _translationCache[kvp.Key] = kvp.Value;
                }
                
                _logger.LogInformation($"Eski önbellek temizlendi. Kalan çeviri sayısı: {_translationCache.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError("Önbellek temizleme sırasında hata oluştu", ex);
            }
        }

        public void ClearTranslationContext()
        {
            _contextManager.ClearHistory();
            _logger.LogInformation("Çeviri geçmişi temizlendi.");
        }

        protected virtual void OnTranslationCompleted(TranslationCompletedEventArgs e)
        {
            TranslationCompleted?.Invoke(this, e);
        }

        protected virtual void OnTranslationProgress(TranslationProgressEventArgs e)
        {
            TranslationProgress?.Invoke(this, e);
        }

        public async Task<string[]> TranslateBatchAsync(string[] texts, string targetLanguage, Type strategyType = null)
        {
            if (texts == null || texts.Length == 0)
                return new string[0];

            if (string.IsNullOrWhiteSpace(targetLanguage))
                targetLanguage = "tr";

            var results = new string[texts.Length];
            var tasks = new Task<string>[texts.Length];

            // Tüm metinleri paralel olarak işle
            for (int i = 0; i < texts.Length; i++)
            {
                int index = i; 
                tasks[i] = Task.Run(async () =>
                {
                    try
                    {
                        return await TranslateAsync(texts[index], targetLanguage, strategyType);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Toplu çeviri {index} indeksindeki metin için başarısız oldu: {ex.Message}", ex);
                        return texts[index]; // Başarısızlık durumunda orijinal metni döndür
                    }
                });
            }

            // Tüm çevirilerin tamamlanmasını bekle
            var translatedResults = await Task.WhenAll(tasks);

            // Sonuçları çıktı dizisine kopyala
            for (int i = 0; i < texts.Length; i++)
            {
                results[i] = translatedResults[i] ?? texts[i];
            }

            return results;
        }

        public async Task<string[]> TranslateBatchAsyncWithProgress(string[] texts, string targetLanguage, Type strategyType = null, IProgress<int> progress = null)
        {
            if (texts == null || texts.Length == 0)
                return new string[0];

            if (string.IsNullOrWhiteSpace(targetLanguage))
                targetLanguage = "tr";

            var results = new string[texts.Length];
            int completedCount = 0;

            // Tüm metinleri paralel olarak işle
            var tasks = new Task[texts.Length];
            for (int i = 0; i < texts.Length; i++)
            {
                int index = i;
                tasks[i] = Task.Run(async () =>
                {
                    try
                    {
                        var translatedText = await TranslateAsync(texts[index], targetLanguage, strategyType);
                        results[index] = translatedText ?? texts[index];

                        // Başarılı çeviri event'i
                        OnTranslationCompleted(new TranslationCompletedEventArgs
                        {
                            OriginalText = texts[index],
                            TranslatedText = translatedText,
                            TargetLanguage = targetLanguage,
                            TranslationTime = DateTime.UtcNow,
                            Confidence = string.IsNullOrEmpty(translatedText) ? 0.0 : 1.0
                        });

                        // İlerleme güncelle
                        int completed = Interlocked.Increment(ref completedCount);
                        int progressPercentage = (int)((double)completed / texts.Length * 100);
                        
                        OnTranslationProgress(new TranslationProgressEventArgs
                        {
                            ProgressPercentage = progressPercentage,
                            CurrentSentence = texts[index],
                            TotalSentences = texts.Length,
                            CompletedSentences = completed
                        });
                        
                        progress?.Report(progressPercentage);
                    }
                    catch (Exception ex)
                    {
                        results[index] = texts[index];

                        // Hata event'i
                        OnTranslationCompleted(new TranslationCompletedEventArgs
                        {
                            OriginalText = texts[index],
                            TranslatedText = texts[index],
                            TargetLanguage = targetLanguage,
                            TranslationTime = DateTime.UtcNow,
                            Confidence = 0.0,
                            ErrorMessage = ex.Message
                        });

                        _logger.LogError($"Toplu çeviri {index} indeksindeki metin için başarısız oldu: {ex.Message}", ex);

                        // İlerleme güncelle (hata durumunda da)
                        int completed = Interlocked.Increment(ref completedCount);
                        int progressPercentage = (int)((double)completed / texts.Length * 100);
                        progress?.Report(progressPercentage);
                    }
                });
            }

            // Tüm çevirilerin tamamlanmasını bekle
            await Task.WhenAll(tasks);

            return results;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Zamanı dolmuş önbellek girdilerini temizle
                    _cacheManager?.ExpireEntries();
                    
                    // Önbelleği kaydet
                    SaveCacheToDisk();
                    
                    _httpClient?.Dispose();
                    
                    _logger.LogInformation("AdvancedTranslationService kapatıldı ve önbellek kaydedildi");
                }
                _disposed = true;
            }
        }
    }
}