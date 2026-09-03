using HtmlAgilityPack;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace GameTranslatorUltimate
{
    public sealed class StrategyInfo
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public Type Type { get; set; }

        public StrategyInfo()
        {
            Id = string.Empty;
            Name = string.Empty;
        }
    }

    public sealed class TranslationContextManager
    {
        private const int MaxHistorySize = 3;

        private readonly Queue<string> _translationHistory;
        private readonly object _lock;

        public TranslationContextManager()
        {
            _translationHistory =
                new Queue<string>();

            _lock =
                new object();
        }

        public void AddToHistory(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            lock (_lock)
            {
                _translationHistory.Enqueue(
                    text.Trim());

                while (_translationHistory.Count >
                       MaxHistorySize)
                {
                    _translationHistory.Dequeue();
                }
            }
        }

        public string GetContextualPrompt(
            string currentText)
        {
            if (string.IsNullOrWhiteSpace(
                currentText))
            {
                return string.Empty;
            }

            List<string> history;

            lock (_lock)
            {
                history =
                    _translationHistory.ToList();
            }

            if (history.Count == 0)
            {
                return currentText;
            }

            var builder =
                new StringBuilder();

            builder.AppendLine(
                "Context:");

            for (int i = 0;
                 i < history.Count;
                 i++)
            {
                builder.AppendLine(
                    history[i]);
            }

            builder.AppendLine();
            builder.AppendLine(
                "Text:");

            builder.Append(
                currentText);

            return builder.ToString();
        }

        public void ClearHistory()
        {
            lock (_lock)
            {
                _translationHistory.Clear();
            }
        }
    }

    public static class PlaceholderProtector
    {
        public static string Protect(
            string text,
            out Dictionary<string, string> placeholders)
        {
            var localPlaceholders =
                new Dictionary<string, string>(
                    StringComparer.Ordinal);

            if (string.IsNullOrWhiteSpace(
                text))
            {
                placeholders =
                    localPlaceholders;

                return text;
            }

            int counter =
                0;

            string protectedText =
                text;

            protectedText =
                Regex.Replace(
                    protectedText,
                    @"<[^>]+>",
                    match =>
                    {
                        string key =
                            CreatePlaceholder(
                                "HTML",
                                counter++);

                        localPlaceholders[key] =
                            match.Value;

                        return key;
                    });

            protectedText =
                Regex.Replace(
                    protectedText,
                    @"\{[^}]*\}|\[[^\]]*\]",
                    match =>
                    {
                        string key =
                            CreatePlaceholder(
                                "TAG",
                                counter++);

                        localPlaceholders[key] =
                            match.Value;

                        return key;
                    });

            protectedText =
                Regex.Replace(
                    protectedText,
                    @"\b\d{4}-\d{2}-\d{2}\b|\b\d{2}/\d{2}/\d{4}\b|\b\d{1,2}\.\d{1,2}\.\d{4}\b",
                    match =>
                    {
                        string key =
                            CreatePlaceholder(
                                "DATE",
                                counter++);

                        localPlaceholders[key] =
                            match.Value;

                        return key;
                    });

            protectedText =
                Regex.Replace(
                    protectedText,
                    @"\b\d+(?:[.,]\d+)?\b",
                    match =>
                    {
                        string key =
                            CreatePlaceholder(
                                "NUMBER",
                                counter++);

                        localPlaceholders[key] =
                            match.Value;

                        return key;
                    });

            placeholders =
                localPlaceholders;

            return protectedText;
        }

        public static string Restore(
            string text,
            Dictionary<string, string> placeholders)
        {
            if (string.IsNullOrWhiteSpace(
                    text) ||
                placeholders == null ||
                placeholders.Count == 0)
            {
                return text;
            }

            string result =
                text;

            foreach (KeyValuePair<string, string> pair
                     in placeholders
                         .OrderByDescending(
                             item => item.Key.Length))
            {
                result =
                    result.Replace(
                        pair.Key,
                        pair.Value);
            }

            return result;
        }

        private static string CreatePlaceholder(
            string type,
            int index)
        {
            return string.Format(
                "__GT_{0}_{1:D4}__",
                type,
                index);
        }
    }

    public static class SentenceProcessor
    {
        private const int MaxSentenceLength = 500;

        public static List<string> SplitIntoSentences(
            string text)
        {
            var sentences =
                new List<string>();

            if (string.IsNullOrWhiteSpace(
                text))
            {
                return sentences;
            }

            var current =
                new StringBuilder();

            for (int i = 0;
                 i < text.Length;
                 i++)
            {
                char c =
                    text[i];

                current.Append(c);

                if (!IsSentenceEnding(c))
                    continue;

                string sentence =
                    current
                        .ToString()
                        .Trim();

                if (!string.IsNullOrWhiteSpace(
                    sentence))
                {
                    AddSentence(
                        sentences,
                        sentence);
                }

                current.Clear();
            }

            string remaining =
                current
                    .ToString()
                    .Trim();

            if (!string.IsNullOrWhiteSpace(
                remaining))
            {
                AddSentence(
                    sentences,
                    remaining);
            }

            return sentences;
        }

        public static string MergeSentences(
            IList<string> sentences)
        {
            if (sentences == null ||
                sentences.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                " ",
                sentences.Where(
                    sentence =>
                        !string.IsNullOrWhiteSpace(
                            sentence)));
        }

        private static bool IsSentenceEnding(
            char c)
        {
            return
                c == '.' ||
                c == '!' ||
                c == '?' ||
                c == '。' ||
                c == '！' ||
                c == '？';
        }

        private static void AddSentence(
            List<string> sentences,
            string sentence)
        {
            if (sentence.Length <=
                MaxSentenceLength)
            {
                sentences.Add(
                    sentence);

                return;
            }

            sentences.AddRange(
                SplitLongSentence(
                    sentence));
        }

        private static List<string> SplitLongSentence(
            string sentence)
        {
            var result =
                new List<string>();

            if (string.IsNullOrWhiteSpace(
                sentence))
            {
                return result;
            }

            string[] words =
                sentence.Split(
                    new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries);

            var current =
                new StringBuilder();

            for (int i = 0;
                 i < words.Length;
                 i++)
            {
                string word =
                    words[i];

                if (word.Length >
                    MaxSentenceLength)
                {
                    if (current.Length > 0)
                    {
                        result.Add(
                            current
                                .ToString()
                                .Trim());

                        current.Clear();
                    }

                    int position =
                        0;

                    while (position <
                           word.Length)
                    {
                        int length =
                            Math.Min(
                                MaxSentenceLength,
                                word.Length -
                                position);

                        result.Add(
                            word.Substring(
                                position,
                                length));

                        position +=
                            length;
                    }

                    continue;
                }

                int required =
                    current.Length == 0
                        ? word.Length
                        : word.Length + 1;

                if (current.Length +
                    required >
                    MaxSentenceLength)
                {
                    result.Add(
                        current
                            .ToString()
                            .Trim());

                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(
                    word);
            }

            if (current.Length > 0)
            {
                result.Add(
                    current
                        .ToString()
                        .Trim());
            }

            return result;
        }
    }

    public interface ITranslationStrategy
    {
        string Name { get; }

        Task<string> Translate(
            string text,
            string targetLanguage,
            HttpClient client,
            ILogger logger);
    }

    public class GoogleTranslationStrategy :
        ITranslationStrategy
    {
        public virtual string Name
        {
            get
            {
                return "Google";
            }
        }

        public virtual bool IsContextual
        {
            get
            {
                return false;
            }
        }

        public virtual async Task<string> Translate(
            string text,
            string targetLanguage,
            HttpClient client,
            ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(
                text))
            {
                return string.Empty;
            }

            try
            {
                string target =
                    NormalizeTargetLanguage(
                        targetLanguage);

                string url =
                    "https://translate.googleapis.com/translate_a/single" +
                    "?client=gtx" +
                    "&sl=auto" +
                    "&tl=" +
                    HttpUtility.UrlEncode(target) +
                    "&dt=t" +
                    "&q=" +
                    HttpUtility.UrlEncode(text);

                string responseJson =
                    await client.GetStringAsync(
                        url);

                using (JsonDocument document =
                       JsonDocument.Parse(
                           responseJson))
                {
                    JsonElement root =
                        document.RootElement;

                    if (root.ValueKind !=
                            JsonValueKind.Array ||
                        root.GetArrayLength() == 0)
                    {
                        return null;
                    }

                    JsonElement translations =
                        root[0];

                    if (translations.ValueKind !=
                        JsonValueKind.Array)
                    {
                        return null;
                    }

                    var builder =
                        new StringBuilder();

                    foreach (JsonElement item
                             in translations.EnumerateArray())
                    {
                        if (item.ValueKind !=
                                JsonValueKind.Array ||
                            item.GetArrayLength() == 0)
                        {
                            continue;
                        }

                        JsonElement translated =
                            item[0];

                        if (translated.ValueKind ==
                            JsonValueKind.String)
                        {
                            builder.Append(
                                translated.GetString());
                        }
                    }

                    string result =
                        builder
                            .ToString()
                            .Trim();

                    return string.IsNullOrWhiteSpace(
                        result)
                        ? null
                        : result;
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(
                    $"{Name} çevirisi sırasında hata oluştu.",
                    ex);

                return null;
            }
        }

        protected static string NormalizeTargetLanguage(
            string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(
                targetLanguage))
            {
                return "tr";
            }

            return targetLanguage
                .Trim()
                .Replace(
                    '_',
                    '-')
                .ToLowerInvariant();
        }
    }

    public sealed class ContextualGoogleTranslationStrategy :
        GoogleTranslationStrategy
    {
        public override string Name
        {
            get
            {
                return "Google (Akıllı Çeviri)";
            }
        }

        public override bool IsContextual
        {
            get
            {
                return true;
            }
        }
    }

    public sealed class DeepLWebScrapingStrategy :
        ITranslationStrategy
    {
        public string Name
        {
            get
            {
                return "DeepL";
            }
        }

        public async Task<string> Translate(
            string text,
            string targetLanguage,
            HttpClient client,
            ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(
                text))
            {
                return string.Empty;
            }

            try
            {
                string target =
                    NormalizeTargetLanguage(
                        targetLanguage)
                        .ToUpperInvariant();

                var requestBody =
                    new
                    {
                        jsonrpc = "2.0",
                        method = "LMT_handle_jobs",
                        @params = new
                        {
                            jobs = new[]
                            {
                                new
                                {
                                    kind = "default",
                                    raw_en_sentence = text
                                }
                            },
                            lang = new
                            {
                                target_lang = target
                            }
                        }
                    };

                string payload =
                    JsonSerializer.Serialize(
                        requestBody);

                using (var content =
                       new StringContent(
                           payload,
                           Encoding.UTF8,
                           "application/json"))
                using (HttpResponseMessage response =
                       await client.PostAsync(
                           "https://www2.deepl.com/jsonrpc",
                           content))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    string responseJson =
                        await response.Content
                            .ReadAsStringAsync();

                    using (JsonDocument document =
                           JsonDocument.Parse(
                               responseJson))
                    {
                        JsonElement root =
                            document.RootElement;

                        JsonElement result;
                        JsonElement translations;
                        JsonElement beams;
                        JsonElement sentence;

                        if (!root.TryGetProperty(
                                "result",
                                out result))
                        {
                            return null;
                        }

                        if (!result.TryGetProperty(
                                "translations",
                                out translations))
                        {
                            return null;
                        }

                        if (translations.ValueKind !=
                                JsonValueKind.Array ||
                            translations.GetArrayLength() == 0)
                        {
                            return null;
                        }

                        if (!translations[0].TryGetProperty(
                                "beams",
                                out beams))
                        {
                            return null;
                        }

                        if (beams.ValueKind !=
                                JsonValueKind.Array ||
                            beams.GetArrayLength() == 0)
                        {
                            return null;
                        }

                        if (!beams[0].TryGetProperty(
                                "postprocessed_sentence",
                                out sentence))
                        {
                            return null;
                        }

                        if (sentence.ValueKind !=
                            JsonValueKind.String)
                        {
                            return null;
                        }

                        return sentence.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(
                    $"{Name} çevirisi sırasında hata oluştu.",
                    ex);

                return null;
            }
        }

        private static string NormalizeTargetLanguage(
            string language)
        {
            if (string.IsNullOrWhiteSpace(
                language))
            {
                return "tr";
            }

            return language
                .Trim()
                .Replace(
                    '_',
                    '-')
                .ToLowerInvariant();
        }
    }

    public sealed class YandexWebScrapingStrategy :
        ITranslationStrategy
    {
        public string Name
        {
            get
            {
                return "Yandex";
            }
        }

        public async Task<string> Translate(
            string text,
            string targetLanguage,
            HttpClient client,
            ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(
                text))
            {
                return string.Empty;
            }

            try
            {
                string target =
                    NormalizeTargetLanguage(
                        targetLanguage);

                string url =
                    "https://translate.yandex.com/" +
                    "?source_lang=auto" +
                    "&target_lang=" +
                    HttpUtility.UrlEncode(
                        target) +
                    "&text=" +
                    HttpUtility.UrlEncode(
                        text);

                using (var request =
                       new HttpRequestMessage(
                           HttpMethod.Get,
                           url))
                {
                    if (!request.Headers
                        .UserAgent.Any())
                    {
                        request.Headers
                            .UserAgent
                            .ParseAdd(
                                "Mozilla/5.0");
                    }

                    using (HttpResponseMessage response =
                           await client.SendAsync(
                               request))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            return null;
                        }

                        string html =
                            await response.Content
                                .ReadAsStringAsync();

                        var document =
                            new HtmlDocument();

                        document.LoadHtml(
                            html);

                        HtmlNode node =
                            document
                                .DocumentNode
                                .SelectSingleNode(
                                    "//span[@data-complaint-type='translation']");

                        if (node == null)
                        {
                            logger?.LogWarning(
                                "Yandex sayfasında çeviri metni bulunamadı.");

                            return null;
                        }

                        return HttpUtility.HtmlDecode(
                            node.InnerText);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(
                    $"{Name} çevirisi sırasında hata oluştu.",
                    ex);

                return null;
            }
        }

        private static string NormalizeTargetLanguage(
            string language)
        {
            if (string.IsNullOrWhiteSpace(
                language))
            {
                return "tr";
            }

            return language
                .Trim()
                .Replace(
                    '_',
                    '-')
                .ToLowerInvariant();
        }
    }

    public sealed class BingWebTranslationStrategy :
        ITranslationStrategy
    {
        public string Name
        {
            get
            {
                return "Bing";
            }
        }

        public async Task<string> Translate(
            string text,
            string targetLanguage,
            HttpClient client,
            ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(
                text))
            {
                return string.Empty;
            }

            try
            {
                string target =
                    string.IsNullOrWhiteSpace(
                            targetLanguage)
                        ? "tr"
                        : targetLanguage
                            .Trim()
                            .Replace(
                                '_',
                                '-')
                            .ToLowerInvariant();

                string url =
                    "https://www.bing.com/translator" +
                    "?text=" +
                    HttpUtility.UrlEncode(
                        text) +
                    "&from=auto" +
                    "&to=" +
                    HttpUtility.UrlEncode(
                        target);

                string html =
                    await client.GetStringAsync(
                        url);

                var document =
                    new HtmlDocument();

                document.LoadHtml(
                    html);

                HtmlNode translationNode =
                    document.GetElementbyId(
                        "tta_output_ta");

                if (translationNode == null)
                {
                    logger?.LogWarning(
                        "Bing sayfasında çeviri metni bulunamadı.");

                    return null;
                }

                return HttpUtility.HtmlDecode(
                    translationNode.InnerText);
            }
            catch (Exception ex)
            {
                logger?.LogError(
                    $"{Name} çevirisi sırasında hata oluştu.",
                    ex);

                return null;
            }
        }
    }

    public sealed class TranslationCompletedEventArgs :
        EventArgs
    {
        public string OriginalText { get; set; }

        public string TranslatedText { get; set; }

        public string TargetLanguage { get; set; }

        public DateTime TranslationTime { get; set; }

        public double Confidence { get; set; }

        public string ErrorMessage { get; set; }
    }

    public sealed class TranslationProgressEventArgs :
        EventArgs
    {
        public int ProgressPercentage { get; set; }

        public string CurrentSentence { get; set; }

        public int TotalSentences { get; set; }

        public int CompletedSentences { get; set; }
    }

    public sealed class AdvancedTranslationService :
        ITranslationService,
        IBatchTranslationService,
        IDisposable
    {
        private const int CircuitBreakerFailureThreshold =
            3;

        private const int MaxParallelTranslations =
            5;

        private const int CacheSizeLimit =
            10000;

        private static readonly TimeSpan CircuitBreakerBlockDuration =
            TimeSpan.FromMinutes(2);

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly TranslationCacheManager _cacheManager;
        private readonly TranslationContextManager _contextManager;

        private readonly ConcurrentDictionary<string, string>
            _translationCache;

        private readonly ConcurrentDictionary<Type, int>
            _strategyFailureCounts;

        private readonly ConcurrentDictionary<Type, DateTime>
            _strategyBlockedUntil;

        private readonly List<ITranslationStrategy>
            _strategies;

        private int _disposed;

        public List<StrategyInfo> AvailableStrategies
        {
            get;
            private set;
        }

        public event EventHandler<TranslationCompletedEventArgs>
            TranslationCompleted;

        public event EventHandler<TranslationProgressEventArgs>
            TranslationProgress;

        public AdvancedTranslationService(
            HttpClient httpClient,
            ILogger logger)
        {
            if (httpClient == null)
            {
                throw new ArgumentNullException(
                    nameof(httpClient));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(
                    nameof(logger));
            }

            _httpClient =
                httpClient;

            _logger =
                logger;

            _contextManager =
                new TranslationContextManager();

            _cacheManager =
                new TranslationCacheManager(
                    logger);

            _strategyFailureCounts =
                new ConcurrentDictionary<Type, int>();

            _strategyBlockedUntil =
                new ConcurrentDictionary<Type, DateTime>();

            try
            {
                _cacheManager.ExpireEntries();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Çeviri önbelleği temizlenemedi: {ex.Message}");
            }

            Dictionary<string, string> loadedCache;

            try
            {
                loadedCache =
                    _cacheManager.LoadCache() ??
                    new Dictionary<string, string>();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Çeviri önbelleği yüklenemedi.",
                    ex);

                loadedCache =
                    new Dictionary<string, string>();
            }

            _translationCache =
                new ConcurrentDictionary<string, string>(
                    loadedCache
                        .Take(CacheSizeLimit)
                        .ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value),
                    StringComparer.Ordinal);

            ConfigureHttpClient();

            _strategies =
                new List<ITranslationStrategy>
                {
                    new OllamaTranslationStrategy(),
                    new ContextualGoogleTranslationStrategy(),
                    new GoogleTranslationStrategy(),
                    new DeepLWebScrapingStrategy(),
                    new BingWebTranslationStrategy(),
                    new YandexWebScrapingStrategy()
                };

            AvailableStrategies =
                _strategies
                    .Select(
                        strategy =>
                            new StrategyInfo
                            {
                                Id =
                                    GetStrategyId(
                                        strategy),

                                Name =
                                    strategy.Name,

                                Type =
                                    strategy.GetType()
                            })
                    .ToList();
        }

        public async Task<string> TranslateAsync(
            string text,
            string targetLanguage,
            Type strategyType = null)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(
                text))
            {
                return string.Empty;
            }

            string normalizedTarget =
                NormalizeTargetLanguage(
                    targetLanguage);

            ITranslationStrategy strategy =
                ResolveStrategy(
                    strategyType);

            string protectedText =
                PlaceholderProtector.Protect(
                    text,
                    out Dictionary<string, string> placeholders);

            List<string> sentences =
                SentenceProcessor.SplitIntoSentences(
                    protectedText);

            if (sentences.Count == 0)
            {
                return string.Empty;
            }

            var translated =
                new string[sentences.Count];

            bool contextual =
                strategy is ContextualGoogleTranslationStrategy;

            if (contextual)
            {
                for (int i = 0;
                     i < sentences.Count;
                     i++)
                {
                    translated[i] =
                        await TranslateSentenceAsync(
                            sentences[i],
                            normalizedTarget,
                            strategy,
                            true);
                }
            }
            else
            {
                using (var semaphore =
                       new SemaphoreSlim(
                           MaxParallelTranslations,
                           MaxParallelTranslations))
                {
                    var tasks =
                        new Task[sentences.Count];

                    for (int i = 0;
                         i < sentences.Count;
                         i++)
                    {
                        int index =
                            i;

                        tasks[index] =
                            TranslateIndexedSentenceAsync(
                                index,
                                sentences[index],
                                normalizedTarget,
                                strategy,
                                translated,
                                semaphore);
                    }

                    await Task.WhenAll(
                        tasks);
                }
            }

            _contextManager.AddToHistory(
                text);

            string merged =
                SentenceProcessor.MergeSentences(
                    translated);

            return PlaceholderProtector.Restore(
                merged,
                placeholders);
        }

        public Task<string> TranslateAsync(
            string text,
            string targetLanguage,
            string strategyId)
        {
            Type strategyType =
                ResolveStrategyType(
                    strategyId);

            return TranslateAsync(
                text,
                targetLanguage,
                strategyType);
        }

        private async Task TranslateIndexedSentenceAsync(
            int index,
            string sentence,
            string targetLanguage,
            ITranslationStrategy strategy,
            string[] translated,
            SemaphoreSlim semaphore)
        {
            await semaphore.WaitAsync();

            try
            {
                translated[index] =
                    await TranslateSentenceAsync(
                        sentence,
                        targetLanguage,
                        strategy,
                        false);
            }
            finally
            {
                semaphore.Release();
            }
        }

        private async Task<string> TranslateSentenceAsync(
            string sentence,
            string targetLanguage,
            ITranslationStrategy strategy,
            bool contextual)
        {
            if (string.IsNullOrWhiteSpace(
                sentence))
            {
                return string.Empty;
            }

            string cacheKey =
                GenerateCacheKey(
                    sentence,
                    targetLanguage,
                    strategy.GetType());

            string cached;

            if (_translationCache.TryGetValue(
                cacheKey,
                out cached))
            {
                return cached;
            }

            string textToSend =
                sentence;

            if (contextual)
            {
                textToSend =
                    _contextManager.GetContextualPrompt(
                        sentence);
            }

            string translated =
                await TranslateWithStrategyAsync(
                    strategy,
                    textToSend,
                    targetLanguage);

            if (string.IsNullOrWhiteSpace(
                translated))
            {
                return sentence;
            }

            if (contextual)
            {
                translated =
                    ExtractContextualResult(
                        translated,
                        sentence);
            }

            if (string.IsNullOrWhiteSpace(
                translated))
            {
                return sentence;
            }

            AddToCache(
                cacheKey,
                translated);

            return translated;
        }

        private async Task<string> TranslateWithStrategyAsync(
            ITranslationStrategy strategy,
            string text,
            string targetLanguage)
        {
            if (strategy == null)
            {
                return null;
            }

            return await TryStrategyAsync(
                strategy,
                text,
                targetLanguage);
        }

        private async Task<string> TryStrategyAsync(
            ITranslationStrategy strategy,
            string text,
            string targetLanguage)
        {
            Type strategyType =
                strategy.GetType();

            if (IsStrategyBlocked(
                strategyType))
            {
                return null;
            }

            try
            {
                string result =
                    await AttemptTranslateWithRetries(
                        strategy,
                        text,
                        targetLanguage);

                if (!string.IsNullOrWhiteSpace(
                    result))
                {
                    RecordStrategySuccess(
                        strategyType);

                    _logger.LogInformation(
                        $"Metin '{strategy.Name}' ile çevrildi. Hedef: {targetLanguage}");

                    return result;
                }

                RecordStrategyFailure(
                    strategyType);
            }
            catch (Exception ex)
            {
                RecordStrategyFailure(
                    strategyType);

                _logger.LogWarning(
                    $"{strategy.Name} servisi başarısız oldu: {ex.Message}");
            }

            return null;
        }

        private async Task<string> AttemptTranslateWithRetries(
            ITranslationStrategy strategy,
            string text,
            string targetLanguage)
        {
            const int maxAttempts =
                2;

            for (int attempt = 1;
                 attempt <= maxAttempts;
                 attempt++)
            {
                try
                {
                    string result =
                        await strategy.Translate(
                            text,
                            targetLanguage,
                            _httpClient,
                            _logger);

                    if (!string.IsNullOrWhiteSpace(
                        result))
                    {
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        $"{strategy.Name} denemesi {attempt} başarısız oldu: {ex.Message}");
                }

                if (attempt <
                    maxAttempts)
                {
                    int delay =
                        250 *
                        (1 << (attempt - 1));

                    await Task.Delay(
                        delay);
                }
            }

            return null;
        }

        private ITranslationStrategy ResolveStrategy(
            Type strategyType)
        {
            if (strategyType == null)
            {
                return _strategies[0];
            }

            ITranslationStrategy strategy =
                _strategies.FirstOrDefault(
                    item =>
                        item.GetType() ==
                        strategyType);

            return strategy ??
                   _strategies[0];
        }

        private Type ResolveStrategyType(
            string strategyId)
        {
            if (string.IsNullOrWhiteSpace(
                strategyId))
            {
                return null;
            }

            string normalized =
                strategyId
                    .Trim()
                    .ToLowerInvariant();

            for (int i = 0;
                 i < _strategies.Count;
                 i++)
            {
                ITranslationStrategy strategy =
                    _strategies[i];

                string id =
                    GetStrategyId(
                        strategy);

                if (string.Equals(
                    id,
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return strategy.GetType();
                }
            }

            _logger.LogWarning(
                $"Bilinmeyen çeviri stratejisi ID'si: {strategyId}");

            return null;
        }

        private static string GetStrategyId(
            ITranslationStrategy strategy)
        {
            if (strategy == null)
            {
                return string.Empty;
            }

            if (strategy is ContextualGoogleTranslationStrategy)
            {
                return "google-contextual";
            }

            if (strategy is GoogleTranslationStrategy)
            {
                return "google";
            }

            if (strategy is OllamaTranslationStrategy)
            {
                return "ollama";
            }

            if (strategy is DeepLWebScrapingStrategy)
            {
                return "deepl";
            }

            if (strategy is BingWebTranslationStrategy)
            {
                return "bing";
            }

            if (strategy is YandexWebScrapingStrategy)
            {
                return "yandex";
            }

            return strategy
                .GetType()
                .Name
                .ToLowerInvariant();
        }

        private static string ExtractContextualResult(
            string translatedText,
            string originalSentence)
        {
            if (string.IsNullOrWhiteSpace(
                translatedText))
            {
                return originalSentence;
            }

            string text =
                translatedText.Trim();

            int marker =
                text.LastIndexOf(
                    "Text:",
                    StringComparison.OrdinalIgnoreCase);

            if (marker >= 0)
            {
                string tail =
                    text.Substring(
                            marker + 5)
                        .Trim();

                if (!string.IsNullOrWhiteSpace(
                    tail))
                {
                    return tail;
                }
            }

            string[] lines =
                text.Split(
                    new[]
                    {
                        "\r\n",
                        "\n"
                    },
                    StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length > 0)
            {
                return lines[
                    lines.Length - 1]
                    .Trim();
            }

            return text;
        }

        private bool IsStrategyBlocked(
            Type strategyType)
        {
            DateTime until;

            if (!_strategyBlockedUntil.TryGetValue(
                strategyType,
                out until))
            {
                return false;
            }

            if (DateTime.UtcNow <
                until)
            {
                return true;
            }

            DateTime ignored;

            _strategyBlockedUntil.TryRemove(
                strategyType,
                out ignored);

            return false;
        }

        private void RecordStrategyFailure(
            Type strategyType)
        {
            int failures =
                _strategyFailureCounts.AddOrUpdate(
                    strategyType,
                    1,
                    delegate (
                        Type key,
                        int current)
                    {
                        return current + 1;
                    });

            if (failures <
                CircuitBreakerFailureThreshold)
            {
                return;
            }

            _strategyBlockedUntil[strategyType] =
                DateTime.UtcNow.Add(
                    CircuitBreakerBlockDuration);

            _strategyFailureCounts[strategyType] =
                0;

            _logger.LogWarning(
                $"{strategyType.Name} {CircuitBreakerBlockDuration.TotalMinutes:F0} dakika bloke edildi.");
        }

        private void RecordStrategySuccess(
            Type strategyType)
        {
            int ignoredFailure;

            _strategyFailureCounts.TryRemove(
                strategyType,
                out ignoredFailure);

            DateTime ignoredBlock;

            _strategyBlockedUntil.TryRemove(
                strategyType,
                out ignoredBlock);
        }

        private void AddToCache(
            string key,
            string value)
        {
            if (string.IsNullOrWhiteSpace(
                    key) ||
                string.IsNullOrWhiteSpace(
                    value))
            {
                return;
            }

            if (_translationCache.Count >=
                CacheSizeLimit)
            {
                string keyToRemove =
                    _translationCache.Keys
                        .FirstOrDefault();

                if (keyToRemove != null)
                {
                    string ignored;

                    _translationCache.TryRemove(
                        keyToRemove,
                        out ignored);
                }
            }

            _translationCache[key] =
                value;
        }

        private static string GenerateCacheKey(
            string input,
            string targetLanguage,
            Type strategyType)
        {
            string normalizedText =
                NormalizeTextForCache(
                    input);

            string strategy =
                strategyType != null
                    ? strategyType.FullName
                    : "default";

            string material =
                targetLanguage +
                "\n" +
                strategy +
                "\n" +
                normalizedText;

            using (SHA256 sha256 =
                   SHA256.Create())
            {
                byte[] bytes =
                    Encoding.UTF8.GetBytes(
                        material);

                byte[] hash =
                    sha256.ComputeHash(
                        bytes);

                var builder =
                    new StringBuilder(
                        hash.Length * 2);

                for (int i = 0;
                     i < hash.Length;
                     i++)
                {
                    builder.Append(
                        hash[i].ToString(
                            "x2"));
                }

                return builder.ToString();
            }
        }

        private static string NormalizeTextForCache(
            string input)
        {
            if (string.IsNullOrWhiteSpace(
                input))
            {
                return string.Empty;
            }

            string normalized =
                input
                    .Trim()
                    .Replace(
                        '\r',
                        ' ')
                    .Replace(
                        '\n',
                        ' ');

            return Regex.Replace(
                normalized,
                @"\s+",
                " ");
        }

        private static string NormalizeTargetLanguage(
            string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(
                targetLanguage))
            {
                return "tr";
            }

            return targetLanguage
                .Trim()
                .Replace(
                    '_',
                    '-')
                .ToLowerInvariant();
        }

        private void ConfigureHttpClient()
        {
            try
            {
                if (_httpClient.Timeout ==
                    Timeout.InfiniteTimeSpan)
                {
                    _httpClient.Timeout =
                        TimeSpan.FromSeconds(
                            15);
                }

                if (!_httpClient
                    .DefaultRequestHeaders
                    .UserAgent.Any())
                {
                    _httpClient
                        .DefaultRequestHeaders
                        .UserAgent
                        .ParseAdd(
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                }

                if (!_httpClient
                    .DefaultRequestHeaders
                    .Accept.Any())
                {
                    _httpClient
                        .DefaultRequestHeaders
                        .Accept
                        .ParseAdd(
                            "*/*");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"HTTP istemcisi yapılandırılamadı: {ex.Message}");
            }
        }

        public async Task<string[]> TranslateBatchAsync(
            string[] texts,
            string targetLanguage,
            Type strategyType = null)
        {
            ThrowIfDisposed();

            if (texts == null ||
                texts.Length == 0)
            {
                return new string[0];
            }

            string target =
                NormalizeTargetLanguage(
                    targetLanguage);

            var tasks =
                new Task<string>[texts.Length];

            for (int i = 0;
                 i < texts.Length;
                 i++)
            {
                int index =
                    i;

                tasks[index] =
                    TranslateBatchItemAsync(
                        texts[index],
                        target,
                        strategyType);
            }

            return await Task.WhenAll(
                tasks);
        }

        private async Task<string> TranslateBatchItemAsync(
            string text,
            string targetLanguage,
            Type strategyType)
        {
            try
            {
                string result =
                    await TranslateAsync(
                        text,
                        targetLanguage,
                        strategyType);

                return result ??
                       text ??
                       string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Toplu çeviri sırasında hata oluştu.",
                    ex);

                return text ??
                       string.Empty;
            }
        }

        public async Task<string[]> TranslateBatchAsyncWithProgress(
            string[] texts,
            string targetLanguage,
            Type strategyType = null,
            IProgress<int> progress = null)
        {
            ThrowIfDisposed();

            if (texts == null ||
                texts.Length == 0)
            {
                return new string[0];
            }

            string target =
                NormalizeTargetLanguage(
                    targetLanguage);

            var results =
                new string[texts.Length];

            var tasks =
                new Task[texts.Length];

            int completed =
                0;

            for (int i = 0;
                 i < texts.Length;
                 i++)
            {
                int index =
                    i;

                tasks[index] =
                    TranslateBatchItemWithProgressAsync(
                        index,
                        texts,
                        results,
                        target,
                        strategyType,
                        progress,
                        delegate
                        {
                            return Interlocked.Increment(
                                ref completed);
                        });
            }

            await Task.WhenAll(
                tasks);

            return results;
        }

        private async Task TranslateBatchItemWithProgressAsync(
            int index,
            string[] texts,
            string[] results,
            string targetLanguage,
            Type strategyType,
            IProgress<int> progress,
            Func<int> incrementCompleted)
        {
            string original =
                texts[index] ??
                string.Empty;

            string translated =
                null;

            Exception error =
                null;

            try
            {
                translated =
                    await TranslateAsync(
                        original,
                        targetLanguage,
                        strategyType);

                results[index] =
                    translated ??
                    original;
            }
            catch (Exception ex)
            {
                error =
                    ex;

                results[index] =
                    original;

                _logger.LogError(
                    $"Toplu çeviri {index} indeksinde başarısız oldu.",
                    ex);
            }

            int completed =
                incrementCompleted();

            int percentage =
                (int)(
                    completed *
                    100.0 /
                    texts.Length);

            OnTranslationCompleted(
                new TranslationCompletedEventArgs
                {
                    OriginalText =
                        original,

                    TranslatedText =
                        results[index],

                    TargetLanguage =
                        targetLanguage,

                    TranslationTime =
                        DateTime.UtcNow,

                    Confidence =
                        error == null &&
                        !string.IsNullOrWhiteSpace(
                            translated)
                            ? 1.0
                            : 0.0,

                    ErrorMessage =
                        error != null
                            ? error.Message
                            : null
                });

            OnTranslationProgress(
                new TranslationProgressEventArgs
                {
                    ProgressPercentage =
                        percentage,

                    CurrentSentence =
                        original,

                    TotalSentences =
                        texts.Length,

                    CompletedSentences =
                        completed
                });

            if (progress != null)
            {
                progress.Report(
                    percentage);
            }
        }

        public void SaveCacheToDisk()
        {
            if (Volatile.Read(
                ref _disposed) != 0)
            {
                return;
            }

            SaveCacheToDiskInternal();
        }

        private void SaveCacheToDiskInternal()
        {
            try
            {
                _cacheManager.SaveCache(
                    new Dictionary<string, string>(
                        _translationCache));

                _logger.LogInformation(
                    $"{_translationCache.Count} çeviri diske kaydedildi.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Çeviri önbelleği kaydedilirken hata oluştu.",
                    ex);
            }
        }

        public void ClearExpiredCache()
        {
            ThrowIfDisposed();

            try
            {
                SaveCacheToDiskInternal();

                _cacheManager.ExpireEntries();

                Dictionary<string, string> fresh =
                    _cacheManager.LoadCache() ??
                    new Dictionary<string, string>();

                _translationCache.Clear();

                foreach (KeyValuePair<string, string> pair
                         in fresh.Take(
                             CacheSizeLimit))
                {
                    _translationCache[pair.Key] =
                        pair.Value;
                }

                _logger.LogInformation(
                    $"Önbellek temizlendi. Kalan kayıt: {_translationCache.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Önbellek temizlenirken hata oluştu.",
                    ex);
            }
        }

        public void ClearTranslationContext()
        {
            ThrowIfDisposed();

            _contextManager.ClearHistory();

            _logger.LogInformation(
                "Çeviri bağlam geçmişi temizlendi.");
        }

        private void OnTranslationCompleted(
            TranslationCompletedEventArgs e)
        {
            EventHandler<TranslationCompletedEventArgs> handler =
                TranslationCompleted;

            if (handler == null)
                return;

            try
            {
                handler(
                    this,
                    e);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "TranslationCompleted event hatası.",
                    ex);
            }
        }

        private void OnTranslationProgress(
            TranslationProgressEventArgs e)
        {
            EventHandler<TranslationProgressEventArgs> handler =
                TranslationProgress;

            if (handler == null)
                return;

            try
            {
                handler(
                    this,
                    e);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "TranslationProgress event hatası.",
                    ex);
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(
                ref _disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(AdvancedTranslationService));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) != 0)
            {
                return;
            }

            try
            {
                SaveCacheToDiskInternal();

                _cacheManager.ExpireEntries();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "AdvancedTranslationService kapatılırken hata oluştu.",
                    ex);
            }

            _logger.LogInformation(
                "AdvancedTranslationService kapatıldı.");

            GC.SuppressFinalize(
                this);
        }
    }
}