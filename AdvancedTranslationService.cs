using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;

namespace P5S_ceviri
{
    public class AdvancedTranslationService : ITranslationService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly Dictionary<string, string> _translationCache = new Dictionary<string, string>();


        private readonly List<ITranslationStrategy> _strategies;

        public AdvancedTranslationService(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));


            _strategies = new List<ITranslationStrategy>
            {
                new GoogleWebTranslationStrategy(),
                new BingWebTranslationStrategy()
            };
        }

        
        public async Task<string> TranslateAsync(string text, string targetLanguage = "tr")
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;


            var cacheKey = $"{text}_{targetLanguage}";
            if (_translationCache.TryGetValue(cacheKey, out var cachedTranslation))
            {
                return cachedTranslation;
            }

            const int chunkSize = 4500;

            var chunks = SplitTextIntoChunks(text, chunkSize);

            var sb = new StringBuilder();
            foreach (var chunk in chunks)
            {
                string result = string.Empty;
           
                foreach (var strategy in _strategies)
                {
                    try
                    {
                        result = await strategy.Translate(chunk, targetLanguage, _httpClient, _logger);
                        if (!string.IsNullOrWhiteSpace(result))
                        {
                            break; 
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"{strategy.GetType().Name} failed: {ex.Message}");
                    }
                }
               
                if (string.IsNullOrWhiteSpace(result))
                {
                    _logger.LogError($"All translation strategies failed for chunk: '{chunk}'", null);
                    result = string.Empty;
                }

                if (sb.Length > 0) sb.AppendLine();
                sb.Append(result);
            }

            var finalTranslation = sb.ToString();
            _translationCache[cacheKey] = finalTranslation;
            return finalTranslation;
        }

        private List<string> SplitTextIntoChunks(string text, int maxChunkSize)
        {
            var chunks = new List<string>();
            if (text.Length == 0) return chunks;

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

    public class GoogleWebTranslationStrategy : ITranslationStrategy
    {
        public async Task<string> Translate(string text, string targetLanguage, HttpClient client, ILogger logger)
        {
            text = text.Replace(Environment.NewLine, " ").Trim();
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={targetLanguage}&dt=t&q={HttpUtility.UrlEncode(text)}";

            string responseJson;
            try
            {
                responseJson = await client.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                logger.LogError($"Google request failed: {ex.Message}");
                return null;
            }

            try
            {
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
                logger.LogError($"Google JSON parse error: {ex.Message}, response = {responseJson}", ex);
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
            string html;
            try
            {
                html = await client.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                logger.LogError($"Bing request failed: {ex.Message}");
                return null;
            }

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
    }
}