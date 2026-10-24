using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GameTranslatorUltimate
{
    public sealed class AnomalyDetector
    {
        private const int MaxRecentTexts = 50;

        private readonly ILogger _logger;
        private readonly AppSettings _appSettings;
        private readonly List<string> _recentTexts;
        private readonly object _historyLock;
        private readonly HashSet<string> _commonEnglishWords;
        private readonly HashSet<string> _commonGameTerms;

        public AnomalyDetector(
            ILogger logger,
            AppSettings appSettings = null)
        {
            _logger =
                logger;

            _appSettings =
                appSettings;

            _recentTexts =
                new List<string>();

            _historyLock =
                new object();

            _commonEnglishWords =
                InitializeCommonEnglishWords();

            _commonGameTerms =
                InitializeGameTerms();
        }

        public AnomalyResult DetectAnomaly(
            string text,
            string context = "")
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new AnomalyResult
                {
                    OriginalText = text ?? string.Empty,
                    IsAnomalous = false,
                    Confidence = 1.0,
                    Reason = "Boş metin"
                };
            }

            string normalizedText =
                NormalizeText(text);

            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                return new AnomalyResult
                {
                    OriginalText = text,
                    IsAnomalous = false,
                    Confidence = 1.0,
                    Reason = "Boş metin"
                };
            }

            var result =
                new AnomalyResult
                {
                    OriginalText = text,
                    IsAnomalous = false,
                    Confidence = 1.0,
                    Reasons = new List<string>(),
                    Timestamp = DateTime.Now
                };

            AnalyzeLength(
                normalizedText,
                result);

            MergeResult(
                result,
                AnalyzeCharacterDistribution(
                    normalizedText));

            MergeResult(
                result,
                AnalyzeLanguagePatterns(
                    normalizedText));

            MergeResult(
                result,
                AnalyzeRepetition(
                    normalizedText));

            MergeResult(
                result,
                AnalyzeWithHistory(
                    normalizedText));

            if (IsPrimarilyLatinText(
                normalizedText))
            {
                MergeResult(
                    result,
                    AnalyzeGameTerms(
                        normalizedText));
            }

            if (!string.IsNullOrWhiteSpace(
                context))
            {
                MergeResult(
                    result,
                    AnalyzeContext(
                        normalizedText,
                        NormalizeText(context)));
            }

            AddToHistory(
                normalizedText);

            result.Reason =
                result.Reasons != null &&
                result.Reasons.Count > 0
                    ? string.Join(
                        "; ",
                        result.Reasons)
                    : string.Empty;

            ApplyConfiguredThreshold(
                result);

            if (result.IsAnomalous &&
                ShouldLogAnomalies())
            {
                _logger?.LogWarning(
                    $"Anormal OCR metni tespit edildi: {result.Reason}");
            }

            return result;
        }

        private static void AnalyzeLength(
            string text,
            AnomalyResult result)
        {
            if (text.Length < 2)
            {
                MarkAnomaly(
                    result,
                    0.90,
                    $"Metin çok kısa: {text.Length} karakter");
            }
            else if (text.Length > 2000)
            {
                MarkAnomaly(
                    result,
                    0.90,
                    $"Metin olağandışı uzun: {text.Length} karakter");
            }
        }

        private static AnomalyResult AnalyzeCharacterDistribution(
            string text)
        {
            var result =
                CreateNormalResult();

            if (string.IsNullOrEmpty(
                text))
            {
                return result;
            }

            int totalCharacters =
                text.Length;

            int letters =
                text.Count(
                    char.IsLetter);

            int digits =
                text.Count(
                    char.IsDigit);

            int whitespace =
                text.Count(
                    char.IsWhiteSpace);

            int punctuation =
                text.Count(
                    char.IsPunctuation);

            int symbols =
                text.Count(
                    char.IsSymbol);

            int controlCharacters =
                text.Count(
                    char.IsControl);

            int unusualCharacters =
                totalCharacters -
                letters -
                digits -
                whitespace -
                punctuation -
                symbols -
                controlCharacters;

            if (controlCharacters > 0)
            {
                double controlRatio =
                    (double)controlCharacters /
                    totalCharacters;

                if (controlRatio > 0.05)
                {
                    MarkAnomaly(
                        result,
                        0.90,
                        $"Çok fazla kontrol karakteri: %{controlRatio * 100:F1}");
                }
            }

            double symbolRatio =
                (double)(symbols + unusualCharacters) /
                totalCharacters;

            if (symbolRatio > 0.45)
            {
                MarkAnomaly(
                    result,
                    0.78,
                    $"Çok fazla sembol karakteri: %{symbolRatio * 100:F1}");
            }

            double digitRatio =
                (double)digits /
                totalCharacters;

            if (totalCharacters >= 8 &&
                digitRatio > 0.80)
            {
                MarkAnomaly(
                    result,
                    0.70,
                    $"Metnin büyük bölümü sayı: %{digitRatio * 100:F1}");
            }

            Dictionary<char, int> characterCounts =
                text
                    .Where(
                        c => !char.IsWhiteSpace(c))
                    .GroupBy(
                        c => c)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Count());

            if (characterCounts.Count > 0)
            {
                int visibleCharacters =
                    characterCounts.Values.Sum();

                int maxCharacterCount =
                    characterCounts.Values.Max();

                double maxCharacterRatio =
                    visibleCharacters > 0
                        ? (double)maxCharacterCount /
                          visibleCharacters
                        : 0;

                if (visibleCharacters >= 8 &&
                    maxCharacterRatio > 0.65)
                {
                    MarkAnomaly(
                        result,
                        0.70,
                        $"Tek karakter olağandışı sık tekrar ediyor: %{maxCharacterRatio * 100:F1}");
                }
            }

            return result;
        }

        private static AnomalyResult AnalyzeLanguagePatterns(
            string text)
        {
            var result =
                CreateNormalResult();

            string[] words =
                SplitWords(
                    text);

            if (words.Length == 0)
            {
                return result;
            }

            double averageWordLength =
                words.Average(
                    word => word.Length);

            int maximumWordLength =
                words.Max(
                    word => word.Length);

            if (words.Length >= 3 &&
                averageWordLength > 25)
            {
                MarkAnomaly(
                    result,
                    0.70,
                    $"Ortalama kelime uzunluğu çok yüksek: {averageWordLength:F1}");
            }

            if (maximumWordLength > 80)
            {
                MarkAnomaly(
                    result,
                    0.80,
                    $"Olağandışı uzun kelime: {maximumWordLength} karakter");
            }

            int upperCount =
                text.Count(
                    char.IsUpper);

            int lowerCount =
                text.Count(
                    char.IsLower);

            int totalCasedLetters =
                upperCount +
                lowerCount;

            if (totalCasedLetters >= 10)
            {
                double uppercaseRatio =
                    (double)upperCount /
                    totalCasedLetters;

                if (uppercaseRatio > 0.95 &&
                    words.Length > 3)
                {
                    MarkAnomaly(
                        result,
                        0.55,
                        $"Metnin neredeyse tamamı büyük harf: %{uppercaseRatio * 100:F1}");
                }
            }

            return result;
        }

        private static AnomalyResult AnalyzeRepetition(
            string text)
        {
            var result =
                CreateNormalResult();

            if (string.IsNullOrWhiteSpace(
                text))
            {
                return result;
            }

            Match consecutiveMatch =
                Regex.Match(
                    text,
                    @"(.)\1{5,}",
                    RegexOptions.CultureInvariant);

            if (consecutiveMatch.Success)
            {
                MarkAnomaly(
                    result,
                    0.90,
                    "Aşırı ardışık karakter tekrarı tespit edildi");
            }

            string[] words =
                SplitWords(
                    text);

            if (words.Length == 0)
            {
                return result;
            }

            Dictionary<string, int> groups =
                words
                    .GroupBy(
                        word => word,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Count(),
                        StringComparer.OrdinalIgnoreCase);

            if (groups.Count == 0)
            {
                return result;
            }

            int maximumRepetition =
                groups.Values.Max();

            double repetitionRatio =
                (double)maximumRepetition /
                words.Length;

            if (words.Length >= 8 &&
                maximumRepetition >= 5 &&
                repetitionRatio > 0.50)
            {
                MarkAnomaly(
                    result,
                    0.75,
                    $"Aşırı kelime tekrarı: {maximumRepetition} kez");
            }

            return result;
        }

        private AnomalyResult AnalyzeWithHistory(
            string text)
        {
            var result =
                CreateNormalResult();

            List<string> history;

            lock (_historyLock)
            {
                history =
                    new List<string>(
                        _recentTexts);
            }

            if (history.Count < 3)
            {
                return result;
            }

            double averageSimilarity =
                history
                    .Select(
                        previous =>
                            CalculateSimilarity(
                                text,
                                previous))
                    .Average();

            if (averageSimilarity > 0.995)
            {
                result.Reasons.Add(
                    $"Metin geçmiş OCR sonuçlarıyla neredeyse aynı: %{averageSimilarity * 100:F1}");
            }

            double averageLength =
                history.Average(
                    previous => previous.Length);

            if (averageLength > 0 &&
                text.Length >= 10)
            {
                double ratio =
                    Math.Abs(
                        text.Length -
                        averageLength) /
                    averageLength;

                if (ratio > 4.0)
                {
                    MarkAnomaly(
                        result,
                        0.55,
                        $"Geçmişe göre olağandışı uzunluk farkı: %{ratio * 100:F1}");
                }
            }

            return result;
        }

        private AnomalyResult AnalyzeGameTerms(
            string text)
        {
            var result =
                CreateNormalResult();

            string[] words =
                SplitWords(
                    text);

            if (words.Length < 5)
            {
                return result;
            }

            int recognizedTerms =
                0;

            int meaningfulWords =
                0;

            for (int i = 0;
                 i < words.Length;
                 i++)
            {
                string cleanWord =
                    NormalizeLatinWord(
                        words[i]);

                if (string.IsNullOrWhiteSpace(
                    cleanWord))
                {
                    continue;
                }

                if (cleanWord.Length <= 1)
                {
                    continue;
                }

                meaningfulWords++;

                if (_commonGameTerms.Contains(
                        cleanWord) ||
                    _commonEnglishWords.Contains(
                        cleanWord))
                {
                    recognizedTerms++;
                }
            }

            if (meaningfulWords < 5)
            {
                return result;
            }

            double recognitionRatio =
                (double)recognizedTerms /
                meaningfulWords;

            if (meaningfulWords >= 12 &&
                recognitionRatio == 0)
            {
                result.Reasons.Add(
                    "İngilizce oyun terimi algılanmadı.");
            }

            return result;
        }

        private static AnomalyResult AnalyzeContext(
            string text,
            string context)
        {
            var result =
                CreateNormalResult();

            if (string.IsNullOrWhiteSpace(
                    text) ||
                string.IsNullOrWhiteSpace(
                    context))
            {
                return result;
            }

            string[] contextWords =
                SplitWords(
                    context);

            string[] textWords =
                SplitWords(
                    text);

            if (contextWords.Length == 0 ||
                textWords.Length == 0)
            {
                return result;
            }

            HashSet<string> contextSet =
                new HashSet<string>(
                    contextWords,
                    StringComparer.OrdinalIgnoreCase);

            HashSet<string> textSet =
                new HashSet<string>(
                    textWords,
                    StringComparer.OrdinalIgnoreCase);

            int commonWords =
                contextSet.Intersect(
                    textSet,
                    StringComparer.OrdinalIgnoreCase)
                .Count();

            int smallerSet =
                Math.Min(
                    contextSet.Count,
                    textSet.Count);

            if (smallerSet <= 0)
            {
                return result;
            }

            double commonRatio =
                (double)commonWords /
                smallerSet;

            if (contextSet.Count >= 8 &&
                textSet.Count >= 8 &&
                commonRatio < 0.02)
            {
                result.Reasons.Add(
                    "Metin ile önceki bağlam arasında düşük kelime benzerliği.");
            }

            return result;
        }

        private static double CalculateSimilarity(
            string text1,
            string text2)
        {
            if (string.IsNullOrEmpty(
                    text1) ||
                string.IsNullOrEmpty(
                    text2))
            {
                return 0.0;
            }

            if (string.Equals(
                text1,
                text2,
                StringComparison.Ordinal))
            {
                return 1.0;
            }

            string longer =
                text1.Length >= text2.Length
                    ? text1
                    : text2;

            string shorter =
                text1.Length >= text2.Length
                    ? text2
                    : text1;

            if (longer.Length == 0)
            {
                return 1.0;
            }

            int distance =
                LevenshteinDistance(
                    longer,
                    shorter);

            double similarity =
                (longer.Length - distance) /
                (double)longer.Length;

            return Math.Max(
                0,
                Math.Min(
                    1,
                    similarity));
        }

        private static int LevenshteinDistance(
            string source,
            string target)
        {
            if (string.IsNullOrEmpty(
                source))
            {
                return target != null
                    ? target.Length
                    : 0;
            }

            if (string.IsNullOrEmpty(
                target))
            {
                return source.Length;
            }

            if (target.Length > source.Length)
            {
                string temp =
                    source;

                source =
                    target;

                target =
                    temp;
            }

            int[] previous =
                new int[target.Length + 1];

            int[] current =
                new int[target.Length + 1];

            for (int j = 0;
                 j <= target.Length;
                 j++)
            {
                previous[j] =
                    j;
            }

            for (int i = 1;
                 i <= source.Length;
                 i++)
            {
                current[0] =
                    i;

                for (int j = 1;
                     j <= target.Length;
                     j++)
                {
                    int cost =
                        source[i - 1] ==
                        target[j - 1]
                            ? 0
                            : 1;

                    current[j] =
                        Math.Min(
                            Math.Min(
                                current[j - 1] + 1,
                                previous[j] + 1),
                            previous[j - 1] + cost);
                }

                int[] temp =
                    previous;

                previous =
                    current;

                current =
                    temp;
            }

            return previous[
                target.Length];
        }

        private void AddToHistory(
            string text)
        {
            if (string.IsNullOrWhiteSpace(
                text))
            {
                return;
            }

            lock (_historyLock)
            {
                _recentTexts.Add(
                    text);

                while (_recentTexts.Count >
                       MaxRecentTexts)
                {
                    _recentTexts.RemoveAt(
                        0);
                }
            }
        }

        public void ClearHistory()
        {
            lock (_historyLock)
            {
                _recentTexts.Clear();
            }

            _logger?.LogInformation(
                "Anomali tespit geçmişi temizlendi.");
        }

        public AnomalyStatistics GetStatistics()
        {
            List<string> history;

            lock (_historyLock)
            {
                history =
                    new List<string>(
                        _recentTexts);
            }

            if (history.Count == 0)
            {
                return new AnomalyStatistics
                {
                    TotalTextsAnalyzed = 0,
                    AverageTextLength = 0,
                    UniqueWords = 0
                };
            }

            int uniqueWords =
                history
                    .SelectMany(
                        text => SplitWords(text))
                    .Select(
                        NormalizeWordForStatistics)
                    .Where(
                        word =>
                            !string.IsNullOrWhiteSpace(
                                word))
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .Count();

            return new AnomalyStatistics
            {
                TotalTextsAnalyzed =
                    history.Count,

                AverageTextLength =
                    history.Average(
                        text => text.Length),

                UniqueWords =
                    uniqueWords
            };
        }

        private void ApplyConfiguredThreshold(
            AnomalyResult result)
        {
            if (result == null ||
                !result.IsAnomalous)
            {
                return;
            }

            if (_appSettings == null)
            {
                return;
            }

            if (!_appSettings.EnableAnomalyDetection)
            {
                result.IsAnomalous =
                    false;

                result.Confidence =
                    1.0;

                result.Reasons.Clear();

                result.Reason =
                    string.Empty;

                return;
            }

            double threshold =
                _appSettings.AnomalyDetectionThreshold;

            if (double.IsNaN(threshold) ||
                double.IsInfinity(threshold))
            {
                threshold =
                    0.7;
            }

            threshold =
                Math.Max(
                    0,
                    Math.Min(
                        1,
                        threshold));

            double anomalyScore =
                1.0 -
                result.Confidence;

            if (anomalyScore <
                threshold)
            {
                result.IsAnomalous =
                    false;
            }
        }

        private bool ShouldLogAnomalies()
        {
            if (_appSettings == null)
            {
                return true;
            }

            return _appSettings.LogAnomalies;
        }

        private static void MergeResult(
            AnomalyResult target,
            AnomalyResult source)
        {
            if (target == null ||
                source == null)
            {
                return;
            }

            if (!source.IsAnomalous)
            {
                return;
            }

            target.IsAnomalous =
                true;

            target.Confidence =
                Math.Min(
                    target.Confidence,
                    source.Confidence);

            if (source.Reasons != null)
            {
                target.Reasons.AddRange(
                    source.Reasons);
            }
        }

        private static void MarkAnomaly(
            AnomalyResult result,
            double confidence,
            string reason)
        {
            if (result == null)
            {
                return;
            }

            result.IsAnomalous =
                true;

            result.Confidence =
                Math.Min(
                    result.Confidence,
                    Math.Max(
                        0,
                        Math.Min(
                            1,
                            confidence)));

            if (!string.IsNullOrWhiteSpace(
                reason))
            {
                result.Reasons.Add(
                    reason);
            }
        }

        private static AnomalyResult CreateNormalResult()
        {
            return new AnomalyResult
            {
                IsAnomalous = false,
                Confidence = 1.0,
                Reasons = new List<string>(),
                Timestamp = DateTime.Now
            };
        }

        private static string NormalizeText(
            string text)
        {
            if (string.IsNullOrWhiteSpace(
                text))
            {
                return string.Empty;
            }

            string normalized =
                text.Normalize(
                    NormalizationForm.FormKC);

            normalized =
                Regex.Replace(
                    normalized,
                    @"[\t ]+",
                    " ");

            normalized =
                Regex.Replace(
                    normalized,
                    @"(\r\n|\r|\n){3,}",
                    Environment.NewLine +
                    Environment.NewLine);

            return normalized.Trim();
        }

        private static string[] SplitWords(
            string text)
        {
            if (string.IsNullOrWhiteSpace(
                text))
            {
                return new string[0];
            }

            return Regex
                .Split(
                    text,
                    @"\s+")
                .Where(
                    word =>
                        !string.IsNullOrWhiteSpace(
                            word))
                .ToArray();
        }

        private static bool IsPrimarilyLatinText(
            string text)
        {
            if (string.IsNullOrWhiteSpace(
                text))
            {
                return false;
            }

            int letters =
                0;

            int latinLetters =
                0;

            for (int i = 0;
                 i < text.Length;
                 i++)
            {
                char c =
                    text[i];

                if (!char.IsLetter(c))
                {
                    continue;
                }

                letters++;

                if (IsLatinCharacter(c))
                {
                    latinLetters++;
                }
            }

            if (letters == 0)
            {
                return false;
            }

            return
                (double)latinLetters /
                letters >=
                0.75;
        }

        private static bool IsLatinCharacter(
            char c)
        {
            UnicodeCategory category =
                char.GetUnicodeCategory(
                    c);

            if (category !=
                    UnicodeCategory.UppercaseLetter &&
                category !=
                    UnicodeCategory.LowercaseLetter &&
                category !=
                    UnicodeCategory.TitlecaseLetter &&
                category !=
                    UnicodeCategory.ModifierLetter &&
                category !=
                    UnicodeCategory.OtherLetter)
            {
                return false;
            }

            int value =
                c;

            return
                value <= 0x024F ||
                (value >= 0x1E00 &&
                 value <= 0x1EFF);
        }

        private static string NormalizeLatinWord(
            string value)
        {
            if (string.IsNullOrWhiteSpace(
                value))
            {
                return string.Empty;
            }

            var builder =
                new StringBuilder();

            string normalized =
                value.Normalize(
                    NormalizationForm.FormKC);

            for (int i = 0;
                 i < normalized.Length;
                 i++)
            {
                char c =
                    normalized[i];

                if (char.IsLetterOrDigit(c) ||
                    c == '\'' ||
                    c == '-')
                {
                    builder.Append(
                        char.ToLowerInvariant(c));
                }
            }

            return builder
                .ToString()
                .Trim(
                    '\'',
                    '-');
        }

        private static string NormalizeWordForStatistics(
            string value)
        {
            if (string.IsNullOrWhiteSpace(
                value))
            {
                return string.Empty;
            }

            var builder =
                new StringBuilder();

            string normalized =
                value.Normalize(
                    NormalizationForm.FormKC);

            for (int i = 0;
                 i < normalized.Length;
                 i++)
            {
                char c =
                    normalized[i];

                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(
                        char.ToLowerInvariant(c));
                }
            }

            return builder.ToString();
        }

        private static HashSet<string> InitializeCommonEnglishWords()
        {
            return new HashSet<string>(
                new[]
                {
                    "the",
                    "and",
                    "or",
                    "but",
                    "in",
                    "on",
                    "at",
                    "to",
                    "for",
                    "of",
                    "with",
                    "by",
                    "is",
                    "are",
                    "was",
                    "were",
                    "be",
                    "been",
                    "have",
                    "has",
                    "had",
                    "do",
                    "does",
                    "did",
                    "will",
                    "would",
                    "could",
                    "should",
                    "may",
                    "might",
                    "can",
                    "must",
                    "this",
                    "that",
                    "these",
                    "those",
                    "a",
                    "an",
                    "some",
                    "any",
                    "all",
                    "every",
                    "each",
                    "you",
                    "he",
                    "she",
                    "it",
                    "we",
                    "they",
                    "me",
                    "him",
                    "her",
                    "us",
                    "them",
                    "my",
                    "your",
                    "his",
                    "our",
                    "their",
                    "i",
                    "am",
                    "game",
                    "play",
                    "player",
                    "level",
                    "score",
                    "health",
                    "mana",
                    "experience",
                    "quest",
                    "mission",
                    "item",
                    "weapon",
                    "armor",
                    "gold",
                    "attack",
                    "defend",
                    "damage",
                    "heal",
                    "skill",
                    "ability",
                    "menu",
                    "option",
                    "setting",
                    "save",
                    "load",
                    "exit",
                    "start",
                    "end"
                },
                StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> InitializeGameTerms()
        {
            return new HashSet<string>(
                new[]
                {
                    "hp",
                    "mp",
                    "exp",
                    "xp",
                    "lvl",
                    "str",
                    "dex",
                    "int",
                    "vit",
                    "agi",
                    "luk",
                    "atk",
                    "def",
                    "matk",
                    "mdef",
                    "crit",
                    "hit",
                    "dodge",
                    "block",
                    "quest",
                    "mission",
                    "objective",
                    "goal",
                    "target",
                    "enemy",
                    "boss",
                    "monster",
                    "npc",
                    "character",
                    "hero",
                    "warrior",
                    "mage",
                    "archer",
                    "thief",
                    "priest",
                    "guild",
                    "party",
                    "team",
                    "alliance",
                    "clan",
                    "raid",
                    "dungeon",
                    "pvp",
                    "pve",
                    "arena",
                    "battle",
                    "fight",
                    "combat",
                    "duel",
                    "inventory",
                    "equipment",
                    "weapon",
                    "armor",
                    "accessory",
                    "consumable",
                    "craft",
                    "enchant",
                    "upgrade",
                    "enhance",
                    "socket",
                    "gem",
                    "rune",
                    "trade",
                    "auction",
                    "market",
                    "currency",
                    "price",
                    "cost",
                    "reward",
                    "loot",
                    "drop",
                    "treasure",
                    "chest"
                },
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class AnomalyResult
    {
        public string OriginalText { get; set; }

        public bool IsAnomalous { get; set; }

        public double Confidence { get; set; }

        public string Reason { get; set; }

        public List<string> Reasons { get; set; }

        public DateTime Timestamp { get; set; }

        public AnomalyResult()
        {
            OriginalText =
                string.Empty;

            Reason =
                string.Empty;

            Reasons =
                new List<string>();

            Confidence =
                1.0;

            Timestamp =
                DateTime.Now;
        }
    }

    public sealed class AnomalyStatistics
    {
        public int TotalTextsAnalyzed { get; set; }

        public double AverageTextLength { get; set; }

        public int UniqueWords { get; set; }
    }
}