using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GameTranslatorUltimate
{
    /// OCR sonuÃ§larÄ±nda anomali tespiti yapar ve anormal metinleri filtreler
    public class AnomalyDetector
    {
        private readonly ILogger _logger;
        private readonly List<string> _recentTexts;
        private readonly int _maxRecentTexts = 50;
        private readonly Dictionary<char, int> _characterFrequency;
        private readonly HashSet<string> _commonWords;
        private readonly HashSet<string> _commonGameTerms;

        public AnomalyDetector(ILogger logger, AppSettings appSettings = null)
        {
            _logger = logger;
            _recentTexts = new List<string>();
            _characterFrequency = new Dictionary<char, int>();
            _commonWords = InitializeCommonWords();
            _commonGameTerms = InitializeGameTerms();
        }
        /// Metnin anormal olup olmadÄ±ÄŸÄ±nÄ± tespit eder
        public AnomalyResult DetectAnomaly(string text, string context = "")
        {
            if (string.IsNullOrWhiteSpace(text))
                return new AnomalyResult { IsAnomalous = false, Confidence = 1.0, Reason = "BoÅŸ metin" };

            var result = new AnomalyResult
            {
                OriginalText = text,
                IsAnomalous = false,
                Confidence = 1.0,
                Reasons = new List<string>()
            };

            // 1. Temel uzunluk kontrolÃ¼
            if (text.Length < 2 || text.Length > 500)
            {
                result.IsAnomalous = true;
                result.Confidence = 0.9;
                result.Reasons.Add($"Uygun olmayan uzunluk: {text.Length} karakter");
            }

            // 2. Karakter daÄŸÄ±lÄ±mÄ± analizi
            var charAnalysis = AnalyzeCharacterDistribution(text);
            if (charAnalysis.IsAnomalous)
            {
                result.IsAnomalous = true;
                result.Confidence = Math.Min(result.Confidence, charAnalysis.Confidence);
                result.Reasons.AddRange(charAnalysis.Reasons);
            }

            // 3. Dil modeli kontrolÃ¼
            var languageAnalysis = AnalyzeLanguagePatterns(text);
            if (languageAnalysis.IsAnomalous)
            {
                result.IsAnomalous = true;
                result.Confidence = Math.Min(result.Confidence, languageAnalysis.Confidence);
                result.Reasons.AddRange(languageAnalysis.Reasons);
            }

            // 4. Tekrar eden karakter kontrolÃ¼
            var repetitionAnalysis = AnalyzeRepetition(text);
            if (repetitionAnalysis.IsAnomalous)
            {
                result.IsAnomalous = true;
                result.Confidence = Math.Min(result.Confidence, repetitionAnalysis.Confidence);
                result.Reasons.AddRange(repetitionAnalysis.Reasons);
            }

            // 5. GeÃ§miÅŸ metinlerle karÅŸÄ±laÅŸtÄ±rma
            var historyAnalysis = AnalyzeWithHistory(text);
            if (historyAnalysis.IsAnomalous)
            {
                result.IsAnomalous = true;
                result.Confidence = Math.Min(result.Confidence, historyAnalysis.Confidence);
                result.Reasons.AddRange(historyAnalysis.Reasons);
            }

            // 6. Oyun terminolojisi kontrolÃ¼
            var gameTermAnalysis = AnalyzeGameTerms(text);
            if (gameTermAnalysis.IsAnomalous)
            {
                result.IsAnomalous = true;
                result.Confidence = Math.Min(result.Confidence, gameTermAnalysis.Confidence);
                result.Reasons.AddRange(gameTermAnalysis.Reasons);
            }

            // 7. BaÄŸlam analizi
            if (!string.IsNullOrEmpty(context))
            {
                var contextAnalysis = AnalyzeContext(text, context);
                if (contextAnalysis.IsAnomalous)
                {
                    result.IsAnomalous = true;
                    result.Confidence = Math.Min(result.Confidence, contextAnalysis.Confidence);
                    result.Reasons.AddRange(contextAnalysis.Reasons);
                }
            }

            // Metni geÃ§miÅŸe ekle
            AddToHistory(text);

            result.Reason = string.Join("; ", result.Reasons);
            return result;
        }
        /// Karakter daÄŸÄ±lÄ±mÄ±nÄ± analiz eder
        private AnomalyResult AnalyzeCharacterDistribution(string text)
        {
            var result = new AnomalyResult { IsAnomalous = false, Confidence = 1.0, Reasons = new List<string>() };

            // Karakter sayÄ±larÄ±nÄ± hesapla
            var charCounts = text.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
            var totalChars = text.Length;

            // Ã‡ok fazla Ã¶zel karakter kontrolÃ¼
            var specialCharCount = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));
            var specialCharRatio = (double)specialCharCount / totalChars;

            if (specialCharRatio > 0.3)
            {
                result.IsAnomalous = true;
                result.Confidence = 0.8;
                result.Reasons.Add($"Ã‡ok fazla Ã¶zel karakter: %{specialCharRatio * 100:F1}");
            }

            // Ã‡ok fazla sayÄ± kontrolÃ¼
            var digitCount = text.Count(char.IsDigit);
            var digitRatio = (double)digitCount / totalChars;

            if (digitRatio > 0.5)
            {
                result.IsAnomalous = true;
                result.Confidence = 0.7;
                result.Reasons.Add($"Ã‡ok fazla sayÄ±: %{digitRatio * 100:F1}");
            }

            // Tek karakter tekrarÄ± kontrolÃ¼
            var maxCharCount = charCounts.Values.Max();
            var maxCharRatio = (double)maxCharCount / totalChars;

            if (maxCharRatio > 0.4)
            {
                result.IsAnomalous = true;
                result.Confidence = 0.6;
                result.Reasons.Add($"Tek karakter tekrarÄ±: %{maxCharRatio * 100:F1}");
            }

            return result;
        }
        /// Dil kalÄ±plarÄ±nÄ± analiz eder
        private AnomalyResult AnalyzeLanguagePatterns(string text)
        {
            var result = new AnomalyResult { IsAnomalous = false, Confidence = 1.0, Reasons = new List<string>() };

            // Kelime uzunluklarÄ± analizi
            var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0)
            {
                var avgWordLength = words.Average(w => w.Length);
                var maxWordLength = words.Max(w => w.Length);

                if (avgWordLength > 15)
                {
                    result.IsAnomalous = true;
                    result.Confidence = 0.7;
                    result.Reasons.Add($"Ortalama kelime uzunluÄŸu Ã§ok yÃ¼ksek: {avgWordLength:F1}");
                }

                if (maxWordLength > 30)
                {
                    result.IsAnomalous = true;
                    result.Confidence = 0.8;
                    result.Reasons.Add($"Ã‡ok uzun kelime: {maxWordLength} karakter");
                }
            }

            // BÃ¼yÃ¼k/kÃ¼Ã§Ã¼k harf oranÄ±
            var upperCount = text.Count(char.IsUpper);
            var lowerCount = text.Count(char.IsLower);
            var totalLetters = upperCount + lowerCount;

            if (totalLetters > 0)
            {
                var upperRatio = (double)upperCount / totalLetters;
                if (upperRatio > 0.8)
                {
                    result.IsAnomalous = true;
                    result.Confidence = 0.6;
                    result.Reasons.Add($"Ã‡ok fazla bÃ¼yÃ¼k harf: %{upperRatio * 100:F1}");
                }
            }

            return result;
        }
        /// Tekrar eden karakterleri analiz eder
        private AnomalyResult AnalyzeRepetition(string text)
        {
            var result = new AnomalyResult { IsAnomalous = false, Confidence = 1.0, Reasons = new List<string>() };

            // ArdÄ±ÅŸÄ±k tekrar kontrolÃ¼
            var consecutivePattern = new Regex(@"(.)\1{3,}");
            if (consecutivePattern.IsMatch(text))
            {
                result.IsAnomalous = true;
                result.Confidence = 0.9;
                result.Reasons.Add("ArdÄ±ÅŸÄ±k karakter tekrarÄ± tespit edildi");
            }

            // Kelime tekrarÄ± kontrolÃ¼
            var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var wordGroups = words.GroupBy(w => w.ToLower()).ToDictionary(g => g.Key, g => g.Count());
            var maxWordRepetition = wordGroups.Values.Max();

            if (maxWordRepetition > 3 && words.Length > 5)
            {
                result.IsAnomalous = true;
                result.Confidence = 0.7;
                result.Reasons.Add($"AÅŸÄ±rÄ± kelime tekrarÄ±: {maxWordRepetition} kez");
            }

            return result;
        }
        /// GeÃ§miÅŸ metinlerle karÅŸÄ±laÅŸtÄ±rÄ±r
        private AnomalyResult AnalyzeWithHistory(string text)
        {
            var result = new AnomalyResult { IsAnomalous = false, Confidence = 1.0, Reasons = new List<string>() };

            if (_recentTexts.Count < 3) return result;

            // Benzerlik kontrolÃ¼
            var similarityScores = _recentTexts.Select(history => CalculateSimilarity(text, history)).ToList();
            var avgSimilarity = similarityScores.Average();

            if (avgSimilarity > 0.9)
            {
                result.IsAnomalous = true;
                result.Confidence = 0.8;
                result.Reasons.Add($"GeÃ§miÅŸ metinlerle Ã§ok benzer: %{avgSimilarity * 100:F1}");
            }

            // Uzunluk deÄŸiÅŸimi kontrolÃ¼
            var avgLength = _recentTexts.Average(t => t.Length);
            var lengthRatio = Math.Abs(text.Length - avgLength) / avgLength;

            if (lengthRatio > 2.0)
            {
                result.IsAnomalous = true;
                result.Confidence = 0.6;
                result.Reasons.Add($"Uzunluk anormalliÄŸi: %{lengthRatio * 100:F1} fark");
            }

            return result;
        }
        /// Oyun terminolojisini analiz eder
        private AnomalyResult AnalyzeGameTerms(string text)
        {
            var result = new AnomalyResult { IsAnomalous = false, Confidence = 1.0, Reasons = new List<string>() };

            var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var recognizedTerms = 0;

            foreach (var word in words)
            {
                var cleanWord = Regex.Replace(word, @"[^\w]", "").ToLower();
                if (_commonGameTerms.Contains(cleanWord) || _commonWords.Contains(cleanWord))
                {
                    recognizedTerms++;
                }
            }

            var recognitionRatio = (double)recognizedTerms / words.Length;

            if (words.Length > 3 && recognitionRatio < 0.2)
            {
                result.IsAnomalous = true;
                result.Confidence = 0.7;
                result.Reasons.Add($"DÃ¼ÅŸÃ¼k terim tanÄ±ma oranÄ±: %{recognitionRatio * 100:F1}");
            }

            return result;
        }
        /// BaÄŸlam analizi yapar
        private AnomalyResult AnalyzeContext(string text, string context)
        {
            var result = new AnomalyResult { IsAnomalous = false, Confidence = 1.0, Reasons = new List<string>() };

            // BaÄŸlam ile metin arasÄ±ndaki uyumsuzluk
            var contextWords = context.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var textWords = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            var commonWords = contextWords.Intersect(textWords, StringComparer.OrdinalIgnoreCase).Count();
            var totalWords = Math.Max(contextWords.Length, textWords.Length);

            if (totalWords > 0)
            {
                var commonRatio = (double)commonWords / totalWords;
                if (commonRatio < 0.1 && totalWords > 5)
                {
                    result.IsAnomalous = true;
                    result.Confidence = 0.6;
                    result.Reasons.Add($"BaÄŸlam uyumsuzluÄŸu: %{commonRatio * 100:F1} ortak kelime");
                }
            }

            return result;
        }
        /// Ä°ki metin arasÄ±ndaki benzerliÄŸi hesaplar
        private double CalculateSimilarity(string text1, string text2)
        {
            if (string.IsNullOrEmpty(text1) || string.IsNullOrEmpty(text2))
                return 0.0;

            var longer = text1.Length > text2.Length ? text1 : text2;
            var shorter = text1.Length > text2.Length ? text2 : text1;

            if (longer.Length == 0) return 1.0;

            var distance = LevenshteinDistance(longer, shorter);
            return (longer.Length - distance) / (double)longer.Length;
        }
        /// Levenshtein mesafesini hesaplar
        private int LevenshteinDistance(string source, string target)
        {
            if (source.Length == 0) return target.Length;
            if (target.Length == 0) return source.Length;

            var distance = new int[source.Length + 1, target.Length + 1];

            for (int i = 0; i <= source.Length; distance[i, 0] = i++) { }
            for (int j = 0; j <= target.Length; distance[0, j] = j++) { }

            for (int i = 1; i <= source.Length; i++)
            {
                for (int j = 1; j <= target.Length; j++)
                {
                    var cost = target[j - 1] == source[i - 1] ? 0 : 1;
                    distance[i, j] = Math.Min(
                        Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                        distance[i - 1, j - 1] + cost);
                }
            }

            return distance[source.Length, target.Length];
        }
        /// Metni geÃ§miÅŸe ekler
        private void AddToHistory(string text)
        {
            _recentTexts.Add(text);
            if (_recentTexts.Count > _maxRecentTexts)
            {
                _recentTexts.RemoveAt(0);
            }
        }
        /// YaygÄ±n kelimeleri baÅŸlatÄ±r
        private HashSet<string> InitializeCommonWords()
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with", "by",
                "is", "are", "was", "were", "be", "been", "have", "has", "had", "do", "does", "did",
                "will", "would", "could", "should", "may", "might", "can", "must", "shall",
                "this", "that", "these", "those", "a", "an", "some", "any", "all", "every", "each",
                "you", "he", "she", "it", "we", "they", "me", "him", "her", "us", "them",
                "my", "your", "his", "her", "its", "our", "their", "mine", "yours", "hers", "ours", "theirs",
                "i", "am", "are", "was", "were", "be", "been", "being", "have", "has", "had", "having",
                "do", "does", "did", "doing", "will", "would", "could", "should", "may", "might", "can", "must", "shall",
                "game", "play", "player", "level", "score", "health", "mana", "experience", "quest", "mission",
                "item", "weapon", "armor", "potion", "gold", "coin", "money", "shop", "store", "buy", "sell",
                "attack", "defend", "damage", "heal", "spell", "magic", "skill", "ability", "power", "strength",
                "menu", "option", "setting", "save", "load", "exit", "start", "begin", "end", "finish", "complete"
            };
        }
        /// Oyun terimlerini baÅŸlatÄ±r
        private HashSet<string> InitializeGameTerms()
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "hp", "mp", "exp", "lvl", "str", "dex", "int", "vit", "agi", "luk",
                "atk", "def", "matk", "mdef", "crit", "hit", "dodge", "block",
                "quest", "mission", "objective", "goal", "target", "enemy", "boss", "monster",
                "npc", "character", "hero", "warrior", "mage", "archer", "thief", "priest",
                "guild", "party", "team", "alliance", "clan", "guild", "raid", "dungeon",
                "pvp", "pve", "arena", "battle", "fight", "combat", "duel", "tournament",
                "inventory", "equipment", "weapon", "armor", "accessory", "consumable",
                "craft", "enchant", "upgrade", "enhance", "socket", "gem", "rune",
                "trade", "auction", "market", "economy", "currency", "price", "cost",
                "reward", "loot", "drop", "treasure", "chest", "box", "bag", "pouch"
            };
        }
        /// GeÃ§miÅŸi temizler
        public void ClearHistory()
        {
            _recentTexts.Clear();
            _logger?.LogInformation("Anomali tespit geÃ§miÅŸi temizlendi");
        }
        /// Ä°statistikleri dÃ¶ndÃ¼rÃ¼r
        public AnomalyStatistics GetStatistics()
        {
            return new AnomalyStatistics
            {
                TotalTextsAnalyzed = _recentTexts.Count,
                AverageTextLength = _recentTexts.Count > 0 ? _recentTexts.Average(t => t.Length) : 0,
                UniqueWords = _recentTexts.SelectMany(t => t.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    .Select(w => w.ToLower()).Distinct().Count()
            };
        }
    }
    /// Anomali tespit sonucu
    public class AnomalyResult
    {
        public string OriginalText { get; set; }
        public bool IsAnomalous { get; set; }
        public double Confidence { get; set; }
        public string Reason { get; set; }
        public List<string> Reasons { get; set; } = new List<string>();
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// Anomali tespit istatistikleri
    public class AnomalyStatistics
    {
        public int TotalTextsAnalyzed { get; set; }
        public double AverageTextLength { get; set; }
        public int UniqueWords { get; set; }
    }
}

