using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GameTranslatorUltimate
{
    public static class OcrTextCorrector
    {
        private sealed class RegexCorrection
        {
            public Regex Pattern { get; private set; }
            public string Replacement { get; private set; }
            public bool PreserveCase { get; private set; }

            public RegexCorrection(
                string pattern,
                string replacement,
                RegexOptions options,
                bool preserveCase)
            {
                Pattern = new Regex(
                    pattern,
                    options | RegexOptions.Compiled);

                Replacement = replacement;
                PreserveCase = preserveCase;
            }
        }

        private sealed class StringCorrection
        {
            public string Source { get; private set; }
            public string Target { get; private set; }

            public StringCorrection(
                string source,
                string target)
            {
                Source = source;
                Target = target;
            }
        }

        private static readonly RegexCorrection[] CommonCorrections =
        {
            new RegexCorrection(
                @"(?<=\d)[Oo](?=\d)",
                "0",
                RegexOptions.None,
                false),

            new RegexCorrection(
                @"(?<=\d)[Il](?=\d)",
                "1",
                RegexOptions.None,
                false),

            new RegexCorrection(
                @"(?<=\d)S(?=\d)",
                "5",
                RegexOptions.None,
                false),

            new RegexCorrection(
                @"(?<=\d)Z(?=\d)",
                "2",
                RegexOptions.None,
                false),

            new RegexCorrection(
                @"(?<=\d)B(?=\d)",
                "8",
                RegexOptions.None,
                false),

            new RegexCorrection(
                @"[ \t]{2,}",
                " ",
                RegexOptions.None,
                false),

            new RegexCorrection(
                @"\s+([.,!?;:])",
                "$1",
                RegexOptions.None,
                false)
        };

        private static readonly RegexCorrection[] EnglishCorrections =
        {
            new RegexCorrection(
                @"\btlie\b",
                "the",
                RegexOptions.IgnoreCase,
                true),

            new RegexCorrection(
                @"\btlle\b",
                "the",
                RegexOptions.IgnoreCase,
                true),

            new RegexCorrection(
                @"\bwitli\b",
                "with",
                RegexOptions.IgnoreCase,
                true),

            new RegexCorrection(
                @"\bfroln\b",
                "from",
                RegexOptions.IgnoreCase,
                true),

            new RegexCorrection(
                @"\byuor\b",
                "your",
                RegexOptions.IgnoreCase,
                true),

            new RegexCorrection(
                @"\btaht\b",
                "that",
                RegexOptions.IgnoreCase,
                true),

            new RegexCorrection(
                @"\btliat\b",
                "that",
                RegexOptions.IgnoreCase,
                true),

            new RegexCorrection(
                @"\bcan\s+'t\b",
                "can't",
                RegexOptions.IgnoreCase,
                true),

            new RegexCorrection(
                @"\bl\b",
                "I",
                RegexOptions.None,
                false)
        };

        private static readonly StringCorrection[] CommonStringCorrections =
        {
            new StringCorrection("ﬁ", "fi"),
            new StringCorrection("ﬂ", "fl"),
            new StringCorrection("ﬀ", "ff"),
            new StringCorrection("ﬃ", "ffi"),
            new StringCorrection("ﬄ", "ffl"),
            new StringCorrection("\u00A0", " ")
        };

        private static readonly StringCorrection[] EnglishStringCorrections =
        {
            new StringCorrection(" l ", " I "),
            new StringCorrection(" l'", " I'"),
            new StringCorrection("Il ", "I "),
            new StringCorrection("he llo", "hello"),
            new StringCorrection("leve l", "level"),
            new StringCorrection("weicome", "welcome"),
            new StringCorrection("go od", "good"),
            new StringCorrection("t ime", "time"),
            new StringCorrection("worid", "world")
        };

        public static string CorrectText(
            string ocrText,
            bool preserveCase = true,
            ILogger logger = null)
        {
            return CorrectText(
                ocrText,
                null,
                preserveCase,
                logger);
        }

        public static string CorrectText(
            string ocrText,
            string language,
            bool preserveCase = true,
            ILogger logger = null)
        {
            if (string.IsNullOrWhiteSpace(ocrText))
                return string.Empty;

            try
            {
                string result =
                    NormalizeText(
                        ocrText);

                result =
                    ApplyCorrections(
                        result,
                        CommonCorrections,
                        preserveCase);

                result =
                    ApplyStringCorrections(
                        result,
                        CommonStringCorrections);

                if (IsEnglish(language))
                {
                    result =
                        ApplyCorrections(
                            result,
                            EnglishCorrections,
                            preserveCase);

                    result =
                        ApplyStringCorrections(
                            result,
                            EnglishStringCorrections);
                }

                result =
                    NormalizeText(
                        result);

                return result;
            }
            catch (Exception ex)
            {
                logger?.LogError(
                    "OCR metin düzeltme işlemi sırasında hata oluştu.",
                    ex);

                return ocrText.Trim();
            }
        }

        private static string ApplyCorrections(
            string text,
            IEnumerable<RegexCorrection> corrections,
            bool preserveCase)
        {
            string result = text;

            foreach (RegexCorrection correction in corrections)
            {
                result =
                    correction.Pattern.Replace(
                        result,
                        match =>
                            GetReplacement(
                                match,
                                correction,
                                preserveCase));
            }

            return result;
        }

        private static string GetReplacement(
            Match match,
            RegexCorrection correction,
            bool preserveCase)
        {
            string replacement =
                correction.Replacement;

            if (!preserveCase ||
                !correction.PreserveCase ||
                string.IsNullOrEmpty(match.Value) ||
                string.IsNullOrEmpty(replacement))
            {
                return replacement;
            }

            if (match.Value.All(char.IsUpper))
            {
                return replacement.ToUpperInvariant();
            }

            if (char.IsUpper(match.Value[0]))
            {
                if (replacement.Length == 1)
                {
                    return replacement.ToUpperInvariant();
                }

                return
                    char.ToUpperInvariant(
                        replacement[0]) +
                    replacement.Substring(1);
            }

            return replacement;
        }

        private static string ApplyStringCorrections(
            string text,
            IEnumerable<StringCorrection> corrections)
        {
            string result = text;

            foreach (StringCorrection correction in corrections)
            {
                result =
                    result.Replace(
                        correction.Source,
                        correction.Target);
            }

            return result;
        }

        private static string NormalizeText(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string result =
                text.Replace("\r\n", "\n")
                    .Replace('\r', '\n')
                    .Replace('\t', ' ')
                    .Trim();

            result =
                Regex.Replace(
                    result,
                    @"[ ]{2,}",
                    " ");

            result =
                Regex.Replace(
                    result,
                    @" *\n *",
                    "\n");

            return result;
        }

        private static bool IsEnglish(
            string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return false;

            string value =
                language
                    .Trim()
                    .ToLowerInvariant();

            return
                value == "eng" ||
                value == "en" ||
                value == "en-us" ||
                value == "en-gb" ||
                value == "english";
        }

        public static CorrectionStats GetCorrectionStats(
            string original,
            string corrected)
        {
            return new CorrectionStats(
                original,
                corrected);
        }
    }

    public sealed class CorrectionStats
    {
        public int OriginalLength { get; private set; }
        public int CorrectedLength { get; private set; }
        public int CharactersChanged { get; private set; }
        public bool WasModified { get; private set; }

        public CorrectionStats(
            string original,
            string corrected)
        {
            string source =
                original ?? string.Empty;

            string target =
                corrected ?? string.Empty;

            OriginalLength =
                source.Length;

            CorrectedLength =
                target.Length;

            WasModified =
                !string.Equals(
                    source,
                    target,
                    StringComparison.Ordinal);

            CharactersChanged =
                WasModified
                    ? LevenshteinDistance(
                        source,
                        target)
                    : 0;
        }

        private static int LevenshteinDistance(
            string source,
            string target)
        {
            if (string.IsNullOrEmpty(source))
                return target?.Length ?? 0;

            if (string.IsNullOrEmpty(target))
                return source.Length;

            if (source.Length > target.Length)
            {
                string temp = source;
                source = target;
                target = temp;
            }

            int[] previous =
                new int[source.Length + 1];

            int[] current =
                new int[source.Length + 1];

            for (int i = 0;
                 i <= source.Length;
                 i++)
            {
                previous[i] = i;
            }

            for (int j = 1;
                 j <= target.Length;
                 j++)
            {
                current[0] = j;

                for (int i = 1;
                     i <= source.Length;
                     i++)
                {
                    int cost =
                        source[i - 1] ==
                        target[j - 1]
                            ? 0
                            : 1;

                    current[i] =
                        Math.Min(
                            Math.Min(
                                current[i - 1] + 1,
                                previous[i] + 1),
                            previous[i - 1] + cost);
                }

                int[] temp =
                    previous;

                previous =
                    current;

                current =
                    temp;
            }

            return previous[source.Length];
        }

        public override string ToString()
        {
            return
                $"Modified: {WasModified}, " +
                $"Changes: {CharactersChanged}, " +
                $"Length: {OriginalLength} -> {CorrectedLength}";
        }
    }
}