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
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<OcrAccuracyScore> CalculateAccuracyAsync(string recognizedText, string groundTruth)
        {
            return await Task.Run(() =>
            {
                var score = new OcrAccuracyScore();

                if (string.IsNullOrEmpty(groundTruth))
                {
                    _logger.LogWarning("Referans metin boÅŸ, doÄŸruluk hesaplanamÄ±yor");
                    return score;
                }

                var normalizedRecognized = NormalizeText(recognizedText ?? "");
                var normalizedGroundTruth = NormalizeText(groundTruth);

                score.CharacterAccuracy = CalculateCharacterAccuracy(normalizedRecognized, normalizedGroundTruth, score);
                score.WordAccuracy = CalculateWordAccuracy(normalizedRecognized, normalizedGroundTruth, score);
                score.LineAccuracy = CalculateLineAccuracy(recognizedText ?? "", groundTruth, score); // SatÄ±r doÄŸruluÄŸunu kontrol etmek iÃ§in
                score.ConfidenceScore = CalculateConfidenceScore(normalizedRecognized, normalizedGroundTruth);
                score.OverallScore = CalculateOverallScore(score);

                score.DetailedMetrics["CharacterErrorRate"] = 1.0 - score.CharacterAccuracy;
                score.DetailedMetrics["WordErrorRate"] = 1.0 - score.WordAccuracy;
                score.DetailedMetrics["LineErrorRate"] = 1.0 - score.LineAccuracy;
                score.DetailedMetrics["ConfidenceScore"] = score.ConfidenceScore;

                _logger.LogInformation($"DoÄŸruluk hesaplandÄ± - Genel: {score.OverallScore:P}, Karakter: {score.CharacterAccuracy:P}, Kelime: {score.WordAccuracy:P}");
                
                return score;
            });
        }

        public async Task<OcrAccuracyScore> CalculateAccuracyWithImageAsync(Bitmap image, string recognizedText, string groundTruth)
        {
            var score = await CalculateAccuracyAsync(recognizedText, groundTruth);
            
            if (image != null)
            {
                var imageConfidence = CalculateImageBasedConfidence(image, recognizedText);
                score.ConfidenceScore = (score.ConfidenceScore + imageConfidence) / 2.0;
                score.DetailedMetrics["ImageConfidence"] = imageConfidence;
                score.OverallScore = CalculateOverallScore(score); // Genel skoru yeniden hesapla
            }

            return score;
        }

        public async Task<OcrAccuracyReport> GenerateDetailedReportAsync(List<OcrTestResult> testResults)
        {
            return await Task.Run(() =>
            {
                var report = new OcrAccuracyReport { GeneratedAt = DateTime.Now, TotalTests = testResults.Count };

                if (!testResults.Any())
                {
                    _logger.LogWarning("Rapor oluÅŸturmak iÃ§in test sonucu saÄŸlanmadÄ±");
                    return report;
                }

                var engineGroups = testResults.GroupBy(t => t.EngineType);

                foreach (var group in engineGroups)
                {
                    var summary = new EngineAccuracySummary { EngineType = group.Key, TestCount = group.Count() };
                    var validScores = group.Where(t => t.AccuracyScore != null).Select(t => t.AccuracyScore).ToList();
                    
                    if (validScores.Any())
                    {
                        summary.AverageAccuracy = validScores.Average(s => s.OverallScore);
                        summary.BestAccuracy = validScores.Max(s => s.OverallScore);
                        summary.WorstAccuracy = validScores.Min(s => s.OverallScore);
                        summary.CharacterAccuracy = validScores.Average(s => s.CharacterAccuracy);
                        summary.WordAccuracy = validScores.Average(s => s.WordAccuracy);
                        summary.LineAccuracy = validScores.Average(s => s.LineAccuracy);
                    }

                    var processingTimes = group.Select(t => t.ProcessingTime.TotalMilliseconds).ToList();
                    if (processingTimes.Any())
                    {
                        summary.AverageProcessingTime = processingTimes.Average();
                    }

                    summary.CommonErrors = FindCommonErrors(group);
                    report.EngineSummaries[group.Key] = summary;
                }

                var allAccuracies = testResults.Where(t => t.AccuracyScore != null).Select(t => t.AccuracyScore.OverallScore).ToList();
                if (allAccuracies.Any())
                {
                    report.OverallAccuracy = allAccuracies.Average();
                }

                if (report.EngineSummaries.Any())
                {
                    report.BestPerformingEngine = report.EngineSummaries.OrderByDescending(kvp => kvp.Value.AverageAccuracy).First().Key;
                }

                report.Trends = GenerateTrends(testResults);
                report.Recommendations = GenerateRecommendations(report);

                _logger.LogInformation($"DetaylÄ± rapor oluÅŸturuldu: {report.TotalTests} test, genel doÄŸruluk: {report.OverallAccuracy:P}");
                
                return report;
            });
        }

        public async Task<OcrAccuracyScore> CalculateConfidenceScoreAsync(string recognizedText, Bitmap sourceImage)
        {
            return await Task.Run(() =>
            {
                var score = new OcrAccuracyScore();
                if (string.IsNullOrEmpty(recognizedText)) return score;

                var textConfidence = CalculateTextBasedConfidence(recognizedText);
                var imageConfidence = sourceImage != null ? CalculateImageBasedConfidence(sourceImage, recognizedText) : 0.5;
                
                score.ConfidenceScore = (textConfidence + imageConfidence) / 2.0;
                score.OverallScore = score.ConfidenceScore;
                
                score.DetailedMetrics["TextConfidence"] = textConfidence;
                score.DetailedMetrics["ImageConfidence"] = imageConfidence;
                
                return score;
            });
        }

        private string NormalizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();
            text = Regex.Replace(text, @"[^\w\s]", "");
            return text;
        }

        private double CalculateCharacterAccuracy(string recognized, string groundTruth, OcrAccuracyScore score)
        {
            if (string.IsNullOrEmpty(groundTruth)) return 0;
            score.ErrorDetails.AddRange(AnalyzeCharacterErrors(recognized, groundTruth));
            var distance = LevenshteinDistance(recognized, groundTruth);
            score.TotalCharacters = groundTruth.Length;
            score.CharacterErrors = distance;
            return Math.Max(0, 1.0 - (double)distance / groundTruth.Length);
        }

        private double CalculateWordAccuracy(string recognized, string groundTruth, OcrAccuracyScore score)
        {
            if (string.IsNullOrEmpty(groundTruth)) return 0;
            var recognizedWords = recognized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var groundTruthWords = groundTruth.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            score.ErrorDetails.AddRange(AnalyzeWordErrors(recognizedWords, groundTruthWords));
            var distance = LevenshteinDistance(recognizedWords, groundTruthWords);
            score.TotalWords = groundTruthWords.Length;
            score.WordErrors = distance;
            return Math.Max(0, 1.0 - (double)distance / groundTruthWords.Length);
        }

        private double CalculateLineAccuracy(string recognized, string groundTruth, OcrAccuracyScore score)
        {
            if (string.IsNullOrEmpty(groundTruth)) return 0;
            var recognizedLines = recognized.Split('\n');
            var groundTruthLines = groundTruth.Split('\n');
            var distance = LevenshteinDistance(recognizedLines, groundTruthLines);
            score.TotalLines = groundTruthLines.Length;
            score.LineErrors = distance;
            return Math.Max(0, 1.0 - (double)distance / groundTruthLines.Length);
        }

        private double CalculateConfidenceScore(string recognized, string groundTruth)
        {
            if (string.IsNullOrEmpty(recognized)) return 0;
            double confidence = 0.5;
            if (!string.IsNullOrEmpty(groundTruth))
            {
                confidence += (Math.Min(recognized.Length, groundTruth.Length) / (double)Math.Max(recognized.Length, groundTruth.Length)) * 0.3;
            }
            if (recognized.Length > 0)
            {
                confidence += ((double)recognized.Distinct().Count() / recognized.Length) * 0.1;
            }
            if (recognized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length > 3) confidence += 0.1;
            return Math.Min(1.0, confidence);
        }

        private double CalculateTextBasedConfidence(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            double confidence = 0.5;
            if (text.Length > 10) confidence += 0.2;
            else if (text.Length > 5) confidence += 0.1;
            confidence += ((double)text.Distinct().Count() / text.Length) * 0.2;
            if (text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length > 2) confidence += 0.1;
            var commonChars = "abcdefghijklmnopqrstuvwxyz0123456789";
            var commonCharCount = text.ToLowerInvariant().Count(c => commonChars.Contains(c));
            confidence += ((double)commonCharCount / text.Length) * 0.2;
            return Math.Min(1.0, confidence);
        }

        private double CalculateImageBasedConfidence(Bitmap image, string recognizedText)
        {
            if (image == null || string.IsNullOrEmpty(recognizedText)) return 0.5;
            double confidence = 0.5;
            if ((image.Width * image.Height) > 100000) confidence += 0.2;
            double aspectRatio = (double)image.Width / image.Height;
            if (aspectRatio > 1.5) confidence += 0.1;
            return Math.Max(0, Math.Min(1.0, confidence));
        }

        private double CalculateOverallScore(OcrAccuracyScore score)
        {
            return (score.CharacterAccuracy * 0.4) + (score.WordAccuracy * 0.4) + (score.LineAccuracy * 0.1) + (score.ConfidenceScore * 0.1);
        }

       
        private int LevenshteinDistance(string source, string target)
        {
            if (string.IsNullOrEmpty(source)) return target?.Length ?? 0;
            if (string.IsNullOrEmpty(target)) return source.Length;
            
            var matrix = new int[source.Length + 1, target.Length + 1];
            for (int i = 0; i <= source.Length; i++) matrix[i, 0] = i;
            for (int j = 0; j <= target.Length; j++) matrix[0, j] = j;
            
            for (int i = 1; i <= source.Length; i++)
            {
                for (int j = 1; j <= target.Length; j++)
                {
                    var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                    matrix[i, j] = Math.Min(Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1), matrix[i - 1, j - 1] + cost);
                }
            }
            return matrix[source.Length, target.Length];
        }

  
        private int LevenshteinDistance<T>(T[] source, T[] target) where T : IEquatable<T>
        {
            if (source == null || source.Length == 0) return target?.Length ?? 0;
            if (target == null || target.Length == 0) return source.Length;
            
            var matrix = new int[source.Length + 1, target.Length + 1];
            for (int i = 0; i <= source.Length; i++) matrix[i, 0] = i;
            for (int j = 0; j <= target.Length; j++) matrix[0, j] = j;
            
            for (int i = 1; i <= source.Length; i++)
            {
                for (int j = 1; j <= target.Length; j++)
                {
                    var cost = source[i - 1].Equals(target[j - 1]) ? 0 : 1;
                    matrix[i, j] = Math.Min(Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1), matrix[i - 1, j - 1] + cost);
                }
            }
            return matrix[source.Length, target.Length];
        }

        private List<string> FindCommonErrors(IEnumerable<OcrTestResult> testResults)
        {
            var errorPatterns = new Dictionary<string, int>();
            foreach (var result in testResults.Where(t => t.AccuracyScore?.ErrorDetails != null))
            {
                foreach (var error in result.AccuracyScore.ErrorDetails)
                {
                    if (errorPatterns.ContainsKey(error)) errorPatterns[error]++;
                    else errorPatterns[error] = 1;
                }
            }
            return errorPatterns.OrderByDescending(kvp => kvp.Value).Take(5).Select(kvp => $"{kvp.Key} ({kvp.Value} kez)").ToList();
        }

        private List<AccuracyTrend> GenerateTrends(List<OcrTestResult> testResults)
        {
            var trends = new List<AccuracyTrend>();
            var dailyGroups = testResults.Where(t => t.AccuracyScore != null).GroupBy(t => t.TestTime.Date).OrderBy(g => g.Key);
            foreach (var group in dailyGroups)
            {
                foreach (var engineGroup in group.GroupBy(t => t.EngineType))
                {
                    trends.Add(new AccuracyTrend
                    {
                        Date = group.Key,
                        Accuracy = engineGroup.Average(t => t.AccuracyScore.OverallScore),
                        EngineType = engineGroup.Key,
                        TestCategory = "GÃ¼nlÃ¼k Ortalama"
                    });
                }
            }
            return trends;
        }

        private Dictionary<string, object> GenerateRecommendations(OcrAccuracyReport report)
        {
            var recommendations = new Dictionary<string, object> { ["BestEngine"] = report.BestPerformingEngine.ToString() };
            if (report.OverallAccuracy < 0.8)
            {
                recommendations["AccuracyImprovement"] = "Genel doÄŸruluk %80'in altÄ±nda. GÃ¶rÃ¼ntÃ¼ Ã¶n iÅŸlemeyi iyileÅŸtirmeyi veya farklÄ± OCR motorlarÄ± denemeyi dÃ¼ÅŸÃ¼nÃ¼n.";
            }
            foreach (var summary in report.EngineSummaries.Values)
            {
                if (summary.AverageAccuracy < 0.7)
                {
                    recommendations[$"{summary.EngineType}Improvement"] = $"{summary.EngineType} dÃ¼ÅŸÃ¼k doÄŸruluÄŸa sahip ({summary.AverageAccuracy:P}). Parametre ayarÄ± yapmayÄ± veya farklÄ± Ã¶n iÅŸleme kullanmayÄ± dÃ¼ÅŸÃ¼nÃ¼n.";
                }
            }
            return recommendations;
        }

        private List<string> AnalyzeCharacterErrors(string recognized, string groundTruth)
        {
            var errors = new List<string>();
            int minLength = Math.Min(recognized.Length, groundTruth.Length);
            for (int i = 0; i < minLength; i++)
            {
                if (recognized[i] != groundTruth[i])
                {
                    errors.Add($"'{groundTruth[i]}' -> '{recognized[i]}'");
                }
            }
            return errors;
        }

        private List<string> AnalyzeWordErrors(string[] recognizedWords, string[] groundTruthWords)
        {
            var errors = new List<string>();
            var missingWords = groundTruthWords.Except(recognizedWords).ToList();
            var extraWords = recognizedWords.Except(groundTruthWords).ToList();
            
            foreach(var word in missingWords.Take(3)) errors.Add($"Eksik: '{word}'");
            foreach(var word in extraWords.Take(3)) errors.Add($"Fazla: '{word}'");

            return errors;
        }
    }
}
