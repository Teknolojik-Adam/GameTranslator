using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public class OcrAccuracyService : IOcrAccuracyService
    {
        private readonly ILogger _logger;

        public OcrAccuracyService(ILogger logger)
        {
            _logger =
                logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<OcrAccuracyScore> CalculateAccuracyAsync(
            string recognizedText,
            string groundTruth)
        {
            return await Task.Run(() =>
            {
                var score =
                    new OcrAccuracyScore();

                if (string.IsNullOrWhiteSpace(groundTruth))
                {
                    _logger.LogWarning(
                        "Referans metin boş. OCR doğruluğu hesaplanamadı.");

                    return score;
                }

                string recognized =
                    recognizedText ?? string.Empty;

                string reference =
                    groundTruth ?? string.Empty;

                string normalizedRecognized =
                    NormalizeText(recognized);

                string normalizedGroundTruth =
                    NormalizeText(reference);

                score.CharacterAccuracy =
                    CalculateCharacterAccuracy(
                        normalizedRecognized,
                        normalizedGroundTruth,
                        score);

                score.WordAccuracy =
                    CalculateWordAccuracy(
                        normalizedRecognized,
                        normalizedGroundTruth,
                        score);

                score.LineAccuracy =
                    CalculateLineAccuracy(
                        recognized,
                        reference,
                        score);

                score.ConfidenceScore =
                    CalculateConfidenceScore(
                        normalizedRecognized,
                        normalizedGroundTruth);

                score.OverallScore =
                    CalculateOverallScore(
                        score);

                score.DetailedMetrics["CharacterErrorRate"] =
                    1.0 -
                    score.CharacterAccuracy;

                score.DetailedMetrics["WordErrorRate"] =
                    1.0 -
                    score.WordAccuracy;

                score.DetailedMetrics["LineErrorRate"] =
                    1.0 -
                    score.LineAccuracy;

                score.DetailedMetrics["ConfidenceScore"] =
                    score.ConfidenceScore;

                _logger.LogInformation(
                    $"OCR doğruluğu hesaplandı. Genel: {score.OverallScore:P2}, Karakter: {score.CharacterAccuracy:P2}, Kelime: {score.WordAccuracy:P2}");

                return score;
            }).ConfigureAwait(false);
        }

        public async Task<OcrAccuracyScore> CalculateAccuracyWithImageAsync(
            Bitmap image,
            string recognizedText,
            string groundTruth)
        {
            OcrAccuracyScore score =
                await CalculateAccuracyAsync(
                        recognizedText,
                        groundTruth)
                    .ConfigureAwait(false);

            if (image == null)
                return score;

            double imageConfidence =
                CalculateImageBasedConfidence(
                    image,
                    recognizedText);

            score.ConfidenceScore =
                Clamp01(
                    score.ConfidenceScore * 0.75 +
                    imageConfidence * 0.25);

            score.DetailedMetrics["ImageConfidence"] =
                imageConfidence;

            score.OverallScore =
                CalculateOverallScore(
                    score);

            return score;
        }

        public async Task<OcrAccuracyReport> GenerateDetailedReportAsync(
            List<OcrTestResult> testResults)
        {
            return await Task.Run(() =>
            {
                List<OcrTestResult> results =
                    testResults == null
                        ? new List<OcrTestResult>()
                        : testResults
                            .Where(result => result != null)
                            .ToList();

                var report =
                    new OcrAccuracyReport
                    {
                        GeneratedAt =
                            DateTime.Now,

                        TotalTests =
                            results.Count
                    };

                if (results.Count == 0)
                {
                    _logger.LogWarning(
                        "OCR doğruluk raporu için test sonucu bulunamadı.");

                    report.Recommendations =
                        GenerateRecommendations(
                            report);

                    return report;
                }

                IEnumerable<IGrouping<OcrEngineType, OcrTestResult>> engineGroups =
                    results.GroupBy(
                        result =>
                            result.EngineType);

                foreach (IGrouping<OcrEngineType, OcrTestResult> group
                         in engineGroups)
                {
                    var summary =
                        new EngineAccuracySummary
                        {
                            EngineType =
                                group.Key,

                            TestCount =
                                group.Count()
                        };

                    List<OcrAccuracyScore> validScores =
                        group
                            .Where(
                                result =>
                                    result.AccuracyScore != null)
                            .Select(
                                result =>
                                    result.AccuracyScore)
                            .ToList();

                    if (validScores.Count > 0)
                    {
                        summary.AverageAccuracy =
                            validScores.Average(
                                score =>
                                    score.OverallScore);

                        summary.BestAccuracy =
                            validScores.Max(
                                score =>
                                    score.OverallScore);

                        summary.WorstAccuracy =
                            validScores.Min(
                                score =>
                                    score.OverallScore);

                        summary.CharacterAccuracy =
                            validScores.Average(
                                score =>
                                    score.CharacterAccuracy);

                        summary.WordAccuracy =
                            validScores.Average(
                                score =>
                                    score.WordAccuracy);

                        summary.LineAccuracy =
                            validScores.Average(
                                score =>
                                    score.LineAccuracy);
                    }

                    List<double> processingTimes =
                        group
                            .Select(
                                result =>
                                    result
                                        .ProcessingTime
                                        .TotalMilliseconds)
                            .Where(
                                milliseconds =>
                                    milliseconds >= 0)
                            .ToList();

                    if (processingTimes.Count > 0)
                    {
                        summary.AverageProcessingTime =
                            processingTimes.Average();
                    }

                    summary.CommonErrors =
                        FindCommonErrors(
                            group);

                    report.EngineSummaries[group.Key] =
                        summary;
                }

                List<double> accuracies =
                    results
                        .Where(
                            result =>
                                result.AccuracyScore != null)
                        .Select(
                            result =>
                                result.AccuracyScore.OverallScore)
                        .ToList();

                if (accuracies.Count > 0)
                {
                    report.OverallAccuracy =
                        accuracies.Average();
                }

                if (report.EngineSummaries.Count > 0)
                {
                    report.BestPerformingEngine =
                        report.EngineSummaries
                            .OrderByDescending(
                                pair =>
                                    pair.Value.AverageAccuracy)
                            .ThenBy(
                                pair =>
                                    pair.Value.AverageProcessingTime)
                            .First()
                            .Key;
                }

                report.Trends =
                    GenerateTrends(
                        results);

                report.Recommendations =
                    GenerateRecommendations(
                        report);

                _logger.LogInformation(
                    $"OCR doğruluk raporu oluşturuldu. Test: {report.TotalTests}, Doğruluk: {report.OverallAccuracy:P2}");

                return report;
            }).ConfigureAwait(false);
        }

        public async Task<OcrAccuracyScore> CalculateConfidenceScoreAsync(
            string recognizedText,
            Bitmap sourceImage)
        {
            return await Task.Run(() =>
            {
                var score =
                    new OcrAccuracyScore();

                if (string.IsNullOrWhiteSpace(
                    recognizedText))
                {
                    return score;
                }

                double textConfidence =
                    CalculateTextBasedConfidence(
                        recognizedText);

                double imageConfidence =
                    sourceImage != null
                        ? CalculateImageBasedConfidence(
                            sourceImage,
                            recognizedText)
                        : 0.5;

                score.ConfidenceScore =
                    Clamp01(
                        textConfidence * 0.80 +
                        imageConfidence * 0.20);

                score.OverallScore =
                    score.ConfidenceScore;

                score.DetailedMetrics["TextConfidence"] =
                    textConfidence;

                score.DetailedMetrics["ImageConfidence"] =
                    imageConfidence;

                return score;
            }).ConfigureAwait(false);
        }

        private string NormalizeText(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string result =
                text.Replace("\r\n", "\n")
                    .Replace('\r', '\n')
                    .Trim()
                    .ToLowerInvariant();

            result =
                Regex.Replace(
                    result,
                    @"\s+",
                    " ");

            result =
                Regex.Replace(
                    result,
                    @"[^\p{L}\p{N}\s]",
                    string.Empty);

            return result.Trim();
        }

        private double CalculateCharacterAccuracy(
            string recognized,
            string groundTruth,
            OcrAccuracyScore score)
        {
            if (string.IsNullOrEmpty(
                groundTruth))
            {
                return 0;
            }

            int distance =
                LevenshteinDistance(
                    recognized,
                    groundTruth);

            score.TotalCharacters =
                groundTruth.Length;

            score.CharacterErrors =
                distance;

            score.ErrorDetails.AddRange(
                AnalyzeCharacterErrors(
                    recognized,
                    groundTruth));

            return Clamp01(
                1.0 -
                (double)distance /
                groundTruth.Length);
        }

        private double CalculateWordAccuracy(
            string recognized,
            string groundTruth,
            OcrAccuracyScore score)
        {
            string[] recognizedWords =
                SplitWords(
                    recognized);

            string[] groundTruthWords =
                SplitWords(
                    groundTruth);

            score.TotalWords =
                groundTruthWords.Length;

            if (groundTruthWords.Length == 0)
            {
                score.WordErrors =
                    recognizedWords.Length;

                return recognizedWords.Length == 0
                    ? 1.0
                    : 0.0;
            }

            int distance =
                LevenshteinDistance(
                    recognizedWords,
                    groundTruthWords);

            score.WordErrors =
                distance;

            score.ErrorDetails.AddRange(
                AnalyzeWordErrors(
                    recognizedWords,
                    groundTruthWords));

            return Clamp01(
                1.0 -
                (double)distance /
                groundTruthWords.Length);
        }

        private double CalculateLineAccuracy(
            string recognized,
            string groundTruth,
            OcrAccuracyScore score)
        {
            string[] recognizedLines =
                SplitLines(
                    recognized);

            string[] groundTruthLines =
                SplitLines(
                    groundTruth);

            score.TotalLines =
                groundTruthLines.Length;

            if (groundTruthLines.Length == 0)
            {
                score.LineErrors =
                    recognizedLines.Length;

                return recognizedLines.Length == 0
                    ? 1.0
                    : 0.0;
            }

            int distance =
                LevenshteinDistance(
                    recognizedLines,
                    groundTruthLines);

            score.LineErrors =
                distance;

            return Clamp01(
                1.0 -
                (double)distance /
                groundTruthLines.Length);
        }

        private double CalculateConfidenceScore(
            string recognized,
            string groundTruth)
        {
            if (string.IsNullOrWhiteSpace(
                recognized))
            {
                return 0;
            }

            double confidence =
                CalculateTextBasedConfidence(
                    recognized);

            if (string.IsNullOrWhiteSpace(
                groundTruth))
            {
                return confidence;
            }

            int maxLength =
                Math.Max(
                    recognized.Length,
                    groundTruth.Length);

            if (maxLength == 0)
                return 1.0;

            double lengthSimilarity =
                (double)Math.Min(
                    recognized.Length,
                    groundTruth.Length) /
                maxLength;

            int distance =
                LevenshteinDistance(
                    recognized,
                    groundTruth);

            double editSimilarity =
                Clamp01(
                    1.0 -
                    (double)distance /
                    maxLength);

            return Clamp01(
                confidence * 0.35 +
                lengthSimilarity * 0.20 +
                editSimilarity * 0.45);
        }

        private double CalculateTextBasedConfidence(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            string value =
                text.Trim();

            int length =
                value.Length;

            if (length == 0)
                return 0;

            double confidence =
                0.35;

            if (length >= 20)
                confidence += 0.10;
            else if (length >= 8)
                confidence += 0.07;
            else if (length >= 3)
                confidence += 0.03;

            int readableCharacters =
                value.Count(
                    character =>
                        char.IsLetterOrDigit(character) ||
                        char.IsWhiteSpace(character) ||
                        char.IsPunctuation(character));

            double readableRatio =
                (double)readableCharacters /
                length;

            confidence +=
                readableRatio *
                0.30;

            int lettersOrDigits =
                value.Count(
                    char.IsLetterOrDigit);

            double contentRatio =
                (double)lettersOrDigits /
                length;

            confidence +=
                contentRatio *
                0.15;

            int controlCharacters =
                value.Count(
                    char.IsControl);

            if (controlCharacters > 0)
            {
                confidence -=
                    Math.Min(
                        0.25,
                        (double)controlCharacters /
                        length);
            }

            int replacementCharacters =
                value.Count(
                    character =>
                        character == '\uFFFD');

            if (replacementCharacters > 0)
            {
                confidence -=
                    Math.Min(
                        0.30,
                        replacementCharacters *
                        0.05);
            }

            int repeatedNoise =
                CountRepeatedNoise(
                    value);

            if (repeatedNoise > 0)
            {
                confidence -=
                    Math.Min(
                        0.20,
                        repeatedNoise *
                        0.02);
            }

            string[] words =
                SplitWords(
                    value);

            if (words.Length >= 2)
            {
                confidence +=
                    0.05;
            }

            return Clamp01(
                confidence);
        }

        private double CalculateImageBasedConfidence(
            Bitmap image,
            string recognizedText)
        {
            if (image == null)
                return 0.5;

            if (string.IsNullOrWhiteSpace(
                recognizedText))
            {
                return 0.25;
            }

            if (image.Width <= 0 ||
                image.Height <= 0)
            {
                return 0.25;
            }

            double confidence =
                0.50;

            double characters =
                Math.Max(
                    1,
                    recognizedText.Trim().Length);

            double pixelsPerCharacter =
                (double)image.Width *
                image.Height /
                characters;

            if (pixelsPerCharacter >= 400)
                confidence += 0.10;
            else if (pixelsPerCharacter < 40)
                confidence -= 0.10;

            double heightPerLine =
                image.Height /
                (double)Math.Max(
                    1,
                    SplitLines(recognizedText).Length);

            if (heightPerLine >= 16)
                confidence += 0.10;
            else if (heightPerLine < 8)
                confidence -= 0.10;

            double aspectRatio =
                (double)image.Width /
                image.Height;

            if (aspectRatio >= 0.2 &&
                aspectRatio <= 20.0)
            {
                confidence +=
                    0.05;
            }

            return Clamp01(
                confidence);
        }

        private double CalculateOverallScore(
            OcrAccuracyScore score)
        {
            if (score == null)
                return 0;

            return Clamp01(
                score.CharacterAccuracy * 0.40 +
                score.WordAccuracy * 0.40 +
                score.LineAccuracy * 0.15 +
                score.ConfidenceScore * 0.05);
        }

        private int LevenshteinDistance(
            string source,
            string target)
        {
            source =
                source ?? string.Empty;

            target =
                target ?? string.Empty;

            if (source.Length == 0)
                return target.Length;

            if (target.Length == 0)
                return source.Length;

            if (source.Length > target.Length)
            {
                string temp =
                    source;

                source =
                    target;

                target =
                    temp;
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
                current[0] =
                    j;

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

                int[] swap =
                    previous;

                previous =
                    current;

                current =
                    swap;
            }

            return previous[source.Length];
        }

        private int LevenshteinDistance<T>(
            T[] source,
            T[] target)
            where T : IEquatable<T>
        {
            source =
                source ?? new T[0];

            target =
                target ?? new T[0];

            if (source.Length == 0)
                return target.Length;

            if (target.Length == 0)
                return source.Length;

            if (source.Length > target.Length)
            {
                T[] temp =
                    source;

                source =
                    target;

                target =
                    temp;
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

            EqualityComparer<T> comparer =
                EqualityComparer<T>.Default;

            for (int j = 1;
                 j <= target.Length;
                 j++)
            {
                current[0] =
                    j;

                for (int i = 1;
                     i <= source.Length;
                     i++)
                {
                    int cost =
                        comparer.Equals(
                            source[i - 1],
                            target[j - 1])
                            ? 0
                            : 1;

                    current[i] =
                        Math.Min(
                            Math.Min(
                                current[i - 1] + 1,
                                previous[i] + 1),
                            previous[i - 1] + cost);
                }

                int[] swap =
                    previous;

                previous =
                    current;

                current =
                    swap;
            }

            return previous[source.Length];
        }

        private List<string> FindCommonErrors(
            IEnumerable<OcrTestResult> testResults)
        {
            var errorPatterns =
                new Dictionary<string, int>(
                    StringComparer.Ordinal);

            if (testResults == null)
                return new List<string>();

            foreach (OcrTestResult result in testResults)
            {
                if (result == null ||
                    result.AccuracyScore == null ||
                    result.AccuracyScore.ErrorDetails == null)
                {
                    continue;
                }

                foreach (string error in
                         result.AccuracyScore.ErrorDetails)
                {
                    if (string.IsNullOrWhiteSpace(error))
                        continue;

                    int count;

                    if (errorPatterns.TryGetValue(
                        error,
                        out count))
                    {
                        errorPatterns[error] =
                            count + 1;
                    }
                    else
                    {
                        errorPatterns[error] =
                            1;
                    }
                }
            }

            return errorPatterns
                .OrderByDescending(
                    pair =>
                        pair.Value)
                .ThenBy(
                    pair =>
                        pair.Key)
                .Take(5)
                .Select(
                    pair =>
                        $"{pair.Key} ({pair.Value} kez)")
                .ToList();
        }

        private List<AccuracyTrend> GenerateTrends(
            List<OcrTestResult> testResults)
        {
            var trends =
                new List<AccuracyTrend>();

            if (testResults == null)
                return trends;

            var dailyGroups =
                testResults
                    .Where(
                        result =>
                            result != null &&
                            result.AccuracyScore != null)
                    .GroupBy(
                        result =>
                            result.TestTime.Date)
                    .OrderBy(
                        group =>
                            group.Key);

            foreach (var group in dailyGroups)
            {
                foreach (var engineGroup in
                         group.GroupBy(
                             result =>
                                 result.EngineType))
                {
                    trends.Add(
                        new AccuracyTrend
                        {
                            Date =
                                group.Key,

                            Accuracy =
                                engineGroup.Average(
                                    result =>
                                        result.AccuracyScore.OverallScore),

                            EngineType =
                                engineGroup.Key,

                            TestCategory =
                                "Günlük Ortalama"
                        });
                }
            }

            return trends;
        }

        private Dictionary<string, object> GenerateRecommendations(
            OcrAccuracyReport report)
        {
            var recommendations =
                new Dictionary<string, object>();

            if (report == null)
                return recommendations;

            if (report.EngineSummaries != null &&
                report.EngineSummaries.Count > 0)
            {
                recommendations["BestEngine"] =
                    report.BestPerformingEngine.ToString();
            }

            if (report.TotalTests > 0 &&
                report.OverallAccuracy < 0.80)
            {
                recommendations["AccuracyImprovement"] =
                    "Genel OCR doğruluğu %80'in altında. Görüntü ön işleme veya OCR motoru ayarları iyileştirilebilir.";
            }

            if (report.EngineSummaries != null)
            {
                foreach (EngineAccuracySummary summary in
                         report.EngineSummaries.Values)
                {
                    if (summary == null ||
                        summary.TestCount == 0)
                    {
                        continue;
                    }

                    if (summary.AverageAccuracy < 0.70)
                    {
                        recommendations[
                            summary.EngineType +
                            "Improvement"] =
                            $"{summary.EngineType} için ortalama OCR doğruluğu düşük ({summary.AverageAccuracy:P1}).";
                    }
                }
            }

            return recommendations;
        }

        private List<string> AnalyzeCharacterErrors(
            string recognized,
            string groundTruth)
        {
            var errors =
                new List<string>();

            recognized =
                recognized ?? string.Empty;

            groundTruth =
                groundTruth ?? string.Empty;

            int maxDetails =
                10;

            int minLength =
                Math.Min(
                    recognized.Length,
                    groundTruth.Length);

            for (int i = 0;
                 i < minLength &&
                 errors.Count < maxDetails;
                 i++)
            {
                if (recognized[i] ==
                    groundTruth[i])
                {
                    continue;
                }

                errors.Add(
                    $"'{groundTruth[i]}' -> '{recognized[i]}'");
            }

            if (errors.Count < maxDetails &&
                recognized.Length <
                groundTruth.Length)
            {
                int missing =
                    groundTruth.Length -
                    recognized.Length;

                errors.Add(
                    $"Eksik karakter: {missing}");
            }
            else if (errors.Count < maxDetails &&
                     recognized.Length >
                     groundTruth.Length)
            {
                int extra =
                    recognized.Length -
                    groundTruth.Length;

                errors.Add(
                    $"Fazla karakter: {extra}");
            }

            return errors;
        }

        private List<string> AnalyzeWordErrors(
            string[] recognizedWords,
            string[] groundTruthWords)
        {
            recognizedWords =
                recognizedWords ??
                new string[0];

            groundTruthWords =
                groundTruthWords ??
                new string[0];

            var errors =
                new List<string>();

            Dictionary<string, int> recognizedCounts =
                BuildWordCounts(
                    recognizedWords);

            Dictionary<string, int> groundTruthCounts =
                BuildWordCounts(
                    groundTruthWords);

            foreach (KeyValuePair<string, int> pair in
                     groundTruthCounts)
            {
                int recognizedCount;

                recognizedCounts.TryGetValue(
                    pair.Key,
                    out recognizedCount);

                int missing =
                    pair.Value -
                    recognizedCount;

                for (int i = 0;
                     i < missing &&
                     errors.Count < 5;
                     i++)
                {
                    errors.Add(
                        $"Eksik: '{pair.Key}'");
                }

                if (errors.Count >= 5)
                    return errors;
            }

            foreach (KeyValuePair<string, int> pair in
                     recognizedCounts)
            {
                int referenceCount;

                groundTruthCounts.TryGetValue(
                    pair.Key,
                    out referenceCount);

                int extra =
                    pair.Value -
                    referenceCount;

                for (int i = 0;
                     i < extra &&
                     errors.Count < 5;
                     i++)
                {
                    errors.Add(
                        $"Fazla: '{pair.Key}'");
                }

                if (errors.Count >= 5)
                    return errors;
            }

            return errors;
        }

        private static Dictionary<string, int> BuildWordCounts(
            IEnumerable<string> words)
        {
            var result =
                new Dictionary<string, int>(
                    StringComparer.Ordinal);

            if (words == null)
                return result;

            foreach (string word in words)
            {
                if (string.IsNullOrWhiteSpace(word))
                    continue;

                int count;

                if (result.TryGetValue(
                    word,
                    out count))
                {
                    result[word] =
                        count + 1;
                }
                else
                {
                    result[word] =
                        1;
                }
            }

            return result;
        }

        private static string[] SplitWords(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new string[0];

            return text.Split(
                new[]
                {
                    ' ',
                    '\t',
                    '\r',
                    '\n'
                },
                StringSplitOptions.RemoveEmptyEntries);
        }

        private static string[] SplitLines(
            string text)
        {
            if (string.IsNullOrEmpty(text))
                return new string[0];

            return text
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split(
                    new[] { '\n' },
                    StringSplitOptions.None)
                .Select(
                    line =>
                        line.Trim())
                .ToArray();
        }

        private static int CountRepeatedNoise(
            string text)
        {
            if (string.IsNullOrEmpty(text) ||
                text.Length < 3)
            {
                return 0;
            }

            int count =
                0;

            for (int i = 2;
                 i < text.Length;
                 i++)
            {
                char character =
                    text[i];

                if (char.IsLetterOrDigit(character) ||
                    char.IsWhiteSpace(character))
                {
                    continue;
                }

                if (text[i - 1] == character &&
                    text[i - 2] == character)
                {
                    count++;
                }
            }

            return count;
        }

        private static double Clamp01(
            double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value))
            {
                return 0;
            }

            if (value < 0)
                return 0;

            if (value > 1)
                return 1;

            return value;
        }
    }
}