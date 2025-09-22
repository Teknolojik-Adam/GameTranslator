using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public class OcrAccuracyService : IOcrAccuracyService
    {
        private readonly ILogger _logger;

        public OcrAccuracyService(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<OcrAccuracyScore> CalculateAccuracyAsync(string recognizedText, string groundTruth)
        {
            return await Task.Run(() =>
            {
                var score = new OcrAccuracyScore();

                if (string.IsNullOrEmpty(groundTruth))
                {
                    _logger.LogWarning("Ground truth is empty, cannot calculate accuracy");
                    return score;
                }

                // Normalize texts for comparison
                var normalizedRecognized = NormalizeText(recognizedText ?? "");
                var normalizedGroundTruth = NormalizeText(groundTruth);

                // Character-level accuracy
                score.CharacterAccuracy = CalculateCharacterAccuracy(normalizedRecognized, normalizedGroundTruth, score);
                
                // Word-level accuracy
                score.WordAccuracy = CalculateWordAccuracy(normalizedRecognized, normalizedGroundTruth, score);
                
                // Line-level accuracy
                score.LineAccuracy = CalculateLineAccuracy(normalizedRecognized, normalizedGroundTruth, score);
                
                // Confidence score based on text characteristics
                score.ConfidenceScore = CalculateConfidenceScore(normalizedRecognized, normalizedGroundTruth);
                
                // Overall score (weighted average)
                score.OverallScore = CalculateOverallScore(score);

                // Add detailed metrics
                score.DetailedMetrics["CharacterErrorRate"] = 1.0 - score.CharacterAccuracy;
                score.DetailedMetrics["WordErrorRate"] = 1.0 - score.WordAccuracy;
                score.DetailedMetrics["LineErrorRate"] = 1.0 - score.LineAccuracy;
                score.DetailedMetrics["ConfidenceScore"] = score.ConfidenceScore;

                _logger.LogInformation($"Accuracy calculated - Overall: {score.OverallScore:P}, Character: {score.CharacterAccuracy:P}, Word: {score.WordAccuracy:P}");
                
                return score;
            });
        }

        public async Task<OcrAccuracyScore> CalculateAccuracyWithImageAsync(Bitmap image, string recognizedText, string groundTruth)
        {
            var score = await CalculateAccuracyAsync(recognizedText, groundTruth);
            
            // Add image-based confidence factors
            if (image != null)
            {
                var imageConfidence = CalculateImageBasedConfidence(image, recognizedText);
                score.ConfidenceScore = (score.ConfidenceScore + imageConfidence) / 2.0;
                score.DetailedMetrics["ImageConfidence"] = imageConfidence;
            }

            return score;
        }

        public async Task<OcrAccuracyReport> GenerateDetailedReportAsync(List<OcrTestResult> testResults)
        {
            return await Task.Run(() =>
            {
                var report = new OcrAccuracyReport
                {
                    GeneratedAt = DateTime.Now,
                    TotalTests = testResults.Count
                };

                if (!testResults.Any())
                {
                    _logger.LogWarning("No test results provided for accuracy report");
                    return report;
                }

                // Group by engine type
                var engineGroups = testResults.GroupBy(t => t.EngineType);

                foreach (var group in engineGroups)
                {
                    var summary = new EngineAccuracySummary
                    {
                        EngineType = group.Key,
                        TestCount = group.Count()
                    };

                    var accuracies = group.Where(t => t.AccuracyScore != null).Select(t => t.AccuracyScore.OverallScore).ToList();
                    
                    if (accuracies.Any())
                    {
                        summary.AverageAccuracy = accuracies.Average();
                        summary.BestAccuracy = accuracies.Max();
                        summary.WorstAccuracy = accuracies.Min();
                    }

                    var processingTimes = group.Select(t => t.ProcessingTime.TotalMilliseconds).ToList();
                    if (processingTimes.Any())
                    {
                        summary.AverageProcessingTime = processingTimes.Average();
                    }

                    // Calculate detailed accuracies
                    var characterAccuracies = group.Where(t => t.AccuracyScore != null).Select(t => t.AccuracyScore.CharacterAccuracy).ToList();
                    var wordAccuracies = group.Where(t => t.AccuracyScore != null).Select(t => t.AccuracyScore.WordAccuracy).ToList();
                    var lineAccuracies = group.Where(t => t.AccuracyScore != null).Select(t => t.AccuracyScore.LineAccuracy).ToList();

                    if (characterAccuracies.Any()) summary.CharacterAccuracy = characterAccuracies.Average();
                    if (wordAccuracies.Any()) summary.WordAccuracy = wordAccuracies.Average();
                    if (lineAccuracies.Any()) summary.LineAccuracy = lineAccuracies.Average();

                    // Find common errors
                    summary.CommonErrors = FindCommonErrors(group);

                    report.EngineSummaries[group.Key] = summary;
                }

                // Calculate overall accuracy
                var allAccuracies = testResults.Where(t => t.AccuracyScore != null).Select(t => t.AccuracyScore.OverallScore).ToList();
                if (allAccuracies.Any())
                {
                    report.OverallAccuracy = allAccuracies.Average();
                }

                // Find best performing engine
                report.BestPerformingEngine = report.EngineSummaries
                    .OrderByDescending(kvp => kvp.Value.AverageAccuracy)
                    .First().Key;

                // Generate trends
                report.Trends = GenerateTrends(testResults);

                // Generate recommendations
                report.Recommendations = GenerateRecommendations(report);

                _logger.LogInformation($"Detailed accuracy report generated: {report.TotalTests} tests, overall accuracy: {report.OverallAccuracy:P}");
                
                return report;
            });
        }

        public async Task<OcrAccuracyScore> CalculateConfidenceScoreAsync(string recognizedText, Bitmap sourceImage)
        {
            return await Task.Run(() =>
            {
                var score = new OcrAccuracyScore();
                
                if (string.IsNullOrEmpty(recognizedText))
                {
                    return score;
                }

                // Text-based confidence factors
                var textConfidence = CalculateTextBasedConfidence(recognizedText);
                
                // Image-based confidence factors
                var imageConfidence = sourceImage != null ? CalculateImageBasedConfidence(sourceImage, recognizedText) : 0.5;
                
                // Combine confidences
                score.ConfidenceScore = (textConfidence + imageConfidence) / 2.0;
                score.OverallScore = score.ConfidenceScore;
                
                score.DetailedMetrics["TextConfidence"] = textConfidence;
                score.DetailedMetrics["ImageConfidence"] = imageConfidence;
                
                return score;
            });
        }

        private string NormalizeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            // Remove extra whitespace and normalize
            text = Regex.Replace(text, @"\s+", " ").Trim();
            
            // Convert to lowercase for comparison
            text = text.ToLowerInvariant();
            
            // Remove punctuation for some calculations
            text = Regex.Replace(text, @"[^\w\s]", "");
            
            return text;
        }

        private double CalculateCharacterAccuracy(string recognized, string groundTruth, OcrAccuracyScore score)
        {
            if (string.IsNullOrEmpty(groundTruth))
                return 0;

            var recognizedChars = recognized.ToCharArray();
            var groundTruthChars = groundTruth.ToCharArray();
            
            score.TotalCharacters = groundTruthChars.Length;
            
            if (recognizedChars.Length == 0)
            {
                score.CharacterErrors = groundTruthChars.Length;
                return 0;
            }

            // Use Levenshtein distance for character-level accuracy
            var distance = LevenshteinDistance(recognizedChars, groundTruthChars);
            score.CharacterErrors = distance;
            
            return Math.Max(0, 1.0 - (double)distance / groundTruthChars.Length);
        }

        private double CalculateWordAccuracy(string recognized, string groundTruth, OcrAccuracyScore score)
        {
            if (string.IsNullOrEmpty(groundTruth))
                return 0;

            var recognizedWords = recognized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var groundTruthWords = groundTruth.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            
            score.TotalWords = groundTruthWords.Length;
            
            if (recognizedWords.Length == 0)
            {
                score.WordErrors = groundTruthWords.Length;
                return 0;
            }

            // Use Levenshtein distance for word-level accuracy
            var distance = LevenshteinDistance(recognizedWords, groundTruthWords);
            score.WordErrors = distance;
            
            return Math.Max(0, 1.0 - (double)distance / groundTruthWords.Length);
        }

        private double CalculateLineAccuracy(string recognized, string groundTruth, OcrAccuracyScore score)
        {
            if (string.IsNullOrEmpty(groundTruth))
                return 0;

            var recognizedLines = recognized.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var groundTruthLines = groundTruth.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            
            score.TotalLines = groundTruthLines.Length;
            
            if (recognizedLines.Length == 0)
            {
                score.LineErrors = groundTruthLines.Length;
                return 0;
            }

            // Use Levenshtein distance for line-level accuracy
            var distance = LevenshteinDistance(recognizedLines, groundTruthLines);
            score.LineErrors = distance;
            
            return Math.Max(0, 1.0 - (double)distance / groundTruthLines.Length);
        }

        private double CalculateConfidenceScore(string recognized, string groundTruth)
        {
            if (string.IsNullOrEmpty(recognized))
                return 0;

            double confidence = 0.5; // Base confidence

            // Length similarity
            if (!string.IsNullOrEmpty(groundTruth))
            {
                var lengthRatio = Math.Min(recognized.Length, groundTruth.Length) / (double)Math.Max(recognized.Length, groundTruth.Length);
                confidence += lengthRatio * 0.3;
            }

            // Character diversity
            var uniqueChars = recognized.Distinct().Count();
            var totalChars = recognized.Length;
            if (totalChars > 0)
            {
                var diversity = (double)uniqueChars / totalChars;
                confidence += diversity * 0.1;
            }

            // Word count
            var wordCount = recognized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount > 3) confidence += 0.1;

            return Math.Min(1.0, confidence);
        }

        private double CalculateTextBasedConfidence(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            double confidence = 0.5;

            // Text length factor
            if (text.Length > 10) confidence += 0.2;
            else if (text.Length > 5) confidence += 0.1;

            // Character diversity
            var uniqueChars = text.Distinct().Count();
            var diversity = (double)uniqueChars / text.Length;
            confidence += diversity * 0.2;

            // Word count
            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 2) confidence += 0.1;

            // Common character ratio
            var commonChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var commonCharCount = text.Count(c => commonChars.Contains(c));
            confidence += (double)commonCharCount / text.Length * 0.2;

            return Math.Min(1.0, confidence);
        }

        private double CalculateImageBasedConfidence(Bitmap image, string recognizedText)
        {
            if (image == null || string.IsNullOrEmpty(recognizedText))
                return 0.5;

            double confidence = 0.5;

            // Image size factor
            var imageArea = image.Width * image.Height;
            if (imageArea > 100000) confidence += 0.2;
            else if (imageArea > 50000) confidence += 0.1;

            // Aspect ratio factor (text images are usually wider than tall)
            var aspectRatio = (double)image.Width / image.Height;
            if (aspectRatio > 1.5) confidence += 0.1;
            else if (aspectRatio < 0.5) confidence -= 0.1;

            // Text length vs image size ratio
            var textToImageRatio = (double)recognizedText.Length / imageArea * 1000000;
            if (textToImageRatio > 0.1 && textToImageRatio < 10) confidence += 0.1;

            return Math.Max(0, Math.Min(1.0, confidence));
        }

        private double CalculateOverallScore(OcrAccuracyScore score)
        {
            // Weighted average: Character (40%), Word (40%), Line (10%), Confidence (10%)
            return (score.CharacterAccuracy * 0.4) + 
                   (score.WordAccuracy * 0.4) + 
                   (score.LineAccuracy * 0.1) + 
                   (score.ConfidenceScore * 0.1);
        }

        private int LevenshteinDistance<T>(T[] source, T[] target) where T : IEquatable<T>
        {
            if (source.Length == 0) return target.Length;
            if (target.Length == 0) return source.Length;

            var matrix = new int[source.Length + 1, target.Length + 1];

            for (int i = 0; i <= source.Length; i++)
                matrix[i, 0] = i;

            for (int j = 0; j <= target.Length; j++)
                matrix[0, j] = j;

            for (int i = 1; i <= source.Length; i++)
            {
                for (int j = 1; j <= target.Length; j++)
                {
                    var cost = source[i - 1].Equals(target[j - 1]) ? 0 : 1;
                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost);
                }
            }

            return matrix[source.Length, target.Length];
        }

        private List<string> FindCommonErrors(IEnumerable<OcrTestResult> testResults)
        {
            var errorPatterns = new Dictionary<string, int>();

            foreach (var result in testResults.Where(t => t.AccuracyScore != null))
            {
                foreach (var error in result.AccuracyScore.ErrorDetails)
                {
                    if (errorPatterns.ContainsKey(error))
                        errorPatterns[error]++;
                    else
                        errorPatterns[error] = 1;
                }
            }

            return errorPatterns
                .OrderByDescending(kvp => kvp.Value)
                .Take(5)
                .Select(kvp => kvp.Key)
                .ToList();
        }

        private List<AccuracyTrend> GenerateTrends(List<OcrTestResult> testResults)
        {
            var trends = new List<AccuracyTrend>();

            var dailyGroups = testResults
                .Where(t => t.AccuracyScore != null)
                .GroupBy(t => t.TestTime.Date)
                .OrderBy(g => g.Key);

            foreach (var group in dailyGroups)
            {
                var engineGroups = group.GroupBy(t => t.EngineType);
                
                foreach (var engineGroup in engineGroups)
                {
                    var averageAccuracy = engineGroup.Average(t => t.AccuracyScore.OverallScore);
                    
                    trends.Add(new AccuracyTrend
                    {
                        Date = group.Key,
                        Accuracy = averageAccuracy,
                        EngineType = engineGroup.Key,
                        TestCategory = "Daily Average"
                    });
                }
            }

            return trends;
        }

        private Dictionary<string, object> GenerateRecommendations(OcrAccuracyReport report)
        {
            var recommendations = new Dictionary<string, object>();

            // Best engine recommendation
            recommendations["BestEngine"] = report.BestPerformingEngine.ToString();

            // Performance recommendations
            if (report.OverallAccuracy < 0.8)
            {
                recommendations["AccuracyImprovement"] = "Overall accuracy is below 80%. Consider image preprocessing improvements or different OCR engines.";
            }

            // Engine-specific recommendations
            foreach (var summary in report.EngineSummaries.Values)
            {
                if (summary.AverageAccuracy < 0.7)
                {
                    recommendations[$"{summary.EngineType}Improvement"] = $"{summary.EngineType} has low accuracy ({summary.AverageAccuracy:P}). Consider tuning parameters or using different preprocessing.";
                }
            }

            return recommendations;
        }
    }
}
