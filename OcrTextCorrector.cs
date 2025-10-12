using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace P5S_ceviri
{
    /// <summary>
    /// OCR sonrası metinlerde sıkça rastlanan hataları, performansı ve doğruluğu
    /// ön planda tutarak düzelten gelişmiş bir yardımcı sınıftır.
    /// </summary>
    public static class OcrTextCorrector
    {
        // Kurallar, en spesifik olandan en genele doğru sıralanmıştır.
        private static readonly Dictionary<Regex, string> ContextualWordCorrections;
        private static readonly Dictionary<Regex, string> GeneralRegexCorrections;
        private static readonly Dictionary<string, string> SimpleStringCorrections;

        static OcrTextCorrector()
        {
            // Sadece tam kelimeleri hedef alan, en güvenli kurallar.
            ContextualWordCorrections = new Dictionary<Regex, string>
            {
                { new Regex(@"\btlie\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "the" },
                { new Regex(@"\btlle\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "the" },
                { new Regex(@"\bwitli\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "with" },
                { new Regex(@"\bfroln\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "from" },
                { new Regex(@"\byuor\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "your" },
                { new Regex(@"\btaht\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "that" },
                { new Regex(@"\btliat\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "that" },
                { new Regex(@"\bcan 't\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "can't" }
            };

            // Genel desenleri hedef alan Regex kuralları.
            GeneralRegexCorrections = new Dictionary<Regex, string>
            {
                { new Regex(@"\bl\b", RegexOptions.Compiled), "I" },
                { new Regex(@"(?<=\d)[Oo](?=\d)", RegexOptions.Compiled), "0" }, // 'O' or 'o' between digits
                { new Regex(@"(?<=\d)S(?=\d)", RegexOptions.Compiled), "5" },
                { new Regex(@"(?<=\d)[Il](?=\d)", RegexOptions.Compiled), "1" }, // 'I' or 'l' between digits
                { new Regex(@"(?<=\d)Z(?=\d)", RegexOptions.Compiled), "2" },
                { new Regex(@"(?<=\d)B(?=\d)", RegexOptions.Compiled), "8" },
                { new Regex(@"\s{2,}", RegexOptions.Compiled), " " },
                { new Regex(@"\s+([.,!?;:])", RegexOptions.Compiled), "$1" }
            };

            // Hızlı, basit ve güvenli metin değişimleri.
            SimpleStringCorrections = new Dictionary<string, string>
            {
                { " l ", " I " }, { " l'", " I'" }, { "Il ", "I " },
                { "ﬁ", "fi" }, { "ﬂ", "fl" }, { "ﬀ", "ff" },
                { "he llo", "hello" }, { "leve l", "level" }, { "weicome", "welcome" },
                { "go od", "good" }, { "t ime", "time" }, { "worid", "world" }
            };
        }

        public static string CorrectText(string ocrText, bool preserveCase = true, ILogger logger = null)
        {
            if (string.IsNullOrWhiteSpace(ocrText)) return string.Empty;

            string correctedText = ocrText;
            try
            {
                // 1. Adım: En güvenli olan tam kelime düzeltmeleri.
                correctedText = ApplyRegexCorrections(correctedText, ContextualWordCorrections, preserveCase);

                // 2. Adım: Genel desen düzeltmeleri.
                correctedText = ApplyRegexCorrections(correctedText, GeneralRegexCorrections, preserveCase: false);

                // 3. Adım: Hızlı ve basit metin değişimleri.
                foreach (var correction in SimpleStringCorrections)
                {
                    correctedText = correctedText.Replace(correction.Key, correction.Value);
                }
            }
            catch (Exception ex)
            {
                logger?.LogError("OcrTextCorrector sırasında hata oluştu.", ex);
                return ocrText.Trim(); // Hata durumunda orijinal metni döndür.
            }
            return correctedText.Trim();
        }

        private static string ApplyRegexCorrections(string text, Dictionary<Regex, string> corrections, bool preserveCase)
        {
            foreach (var correction in corrections)
            {
                text = correction.Key.Replace(text, match =>
                {
                    string replacement = correction.Value;
                    if (!preserveCase || match.Value.Length == 0) return replacement;

                    if (match.Value.All(char.IsUpper))
                        return replacement.ToUpper();
                    if (char.IsUpper(match.Value[0]))
                        return char.ToUpper(replacement[0]) + replacement.Substring(1);

                    return replacement;
                });
            }
            return text;
        }

        public static CorrectionStats GetCorrectionStats(string original, string corrected) => new CorrectionStats(original, corrected);
    }

    public class CorrectionStats
    {
        public int OriginalLength { get; }
        public int CorrectedLength { get; }
        public int CharactersChanged { get; }
        public bool WasModified { get; }

        public CorrectionStats(string original, string corrected)
        {
            OriginalLength = original?.Length ?? 0;
            CorrectedLength = corrected?.Length ?? 0;
            WasModified = original != corrected;
            CharactersChanged = WasModified ? LevenshteinDistance(original, corrected) : 0;
        }

        // Değişen karakter sayısını daha doğru hesaplamak için Levenshtein mesafesi algoritması.
        private static int LevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return t?.Length ?? 0;
            if (string.IsNullOrEmpty(t)) return s.Length;

            int n = s.Length; int m = t.Length;
            int[,] d = new int[n + 1, m + 1];
            for (int i = 0; i <= n; d[i, 0] = i++) ;
            for (int j = 0; j <= m; d[0, j] = j++) ;
            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }

        public override string ToString() => $"Modified: {WasModified}, Changes: {CharactersChanged}, Length: {OriginalLength} -> {CorrectedLength}";
    }
}