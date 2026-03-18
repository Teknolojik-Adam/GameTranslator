using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public class OcrComparisonService : IOcrComparisonService, IDisposable
    {
        private readonly ILogger _logger;
        private readonly Dictionary<OcrEngineType, IOcrEngine> _ocrEngines;
        private readonly AppSettings _appSettings;
        private readonly SemaphoreSlim _concurrencyLimiter;
        private bool _disposed = false;

        public event EventHandler<OcrComparisonCompletedEventArgs> ComparisonCompleted;

        public OcrComparisonService(ILogger logger, AppSettings appSettings)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
            _ocrEngines = new Dictionary<OcrEngineType, IOcrEngine>
            {
                { OcrEngineType.Tesseract, new TesseractOcrEngine(logger, appSettings) },
                { OcrEngineType.WindowsOcr, new WindowsOcrEngine(logger) }
            };
            _concurrencyLimiter = new SemaphoreSlim(Environment.ProcessorCount);
        }

        public async Task<OcrComparisonResult> CompareEnginesAsync(Bitmap image, string language)
        {
            var result = new OcrComparisonResult { Timestamp = DateTime.Now, SourceImage = image, Language = language };
            var stopwatch = Stopwatch.StartNew();
            try
            {
                _logger.LogInformation($"{language} dili için OCR karşılaştırması başlatılıyor.");
                var tasks = _ocrEngines.Select(kvp => ProcessEngineAsync(kvp.Key, kvp.Value, image, language));
                var engineResults = await Task.WhenAll(tasks);

                result.EngineResults = engineResults.Where(r => r != null).ToDictionary(r => r.EngineType);
                result.BestEngine = GetBestEngine(result);
                if (result.EngineResults.TryGetValue(result.BestEngine, out var bestResult))
                {
                    result.BestConfidence = bestResult.Confidence;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("OCR karşılaştırması sırasında hata oluştu", ex);
            }
            finally
            {
                stopwatch.Stop();
                result.TotalProcessingTime = stopwatch.Elapsed;
                _logger.LogInformation($"Karşılaştırma {result.TotalProcessingTime.TotalMilliseconds:F2}ms'de tamamlandı. En iyi motor: {result.BestEngine}");
                OnComparisonCompleted(new OcrComparisonCompletedEventArgs(result));
            }
            return result;
        }

        public async Task<OcrComparisonResult> CompareEnginesWithRegionsAsync(Bitmap image, string language)
        {
            var result = new OcrComparisonResult { Timestamp = DateTime.Now, SourceImage = image, Language = language };
            var stopwatch = Stopwatch.StartNew();
            try
            {
                _logger.LogInformation($"{language} dili iÃ§in bÃ¶lgelerle OCR karÅŸÄ±laÅŸtÄ±rmasÄ± baÅŸlatÄ±lÄ±yor.");
                var regions = DetectTextRegions(image);
                if (!regions.Any())
                {
                    _logger.LogWarning("Metin bÃ¶lgesi tespit edilemedi, tam gÃ¶rÃ¼ntÃ¼ karÅŸÄ±laÅŸtÄ±rmasÄ±na geÃ§iliyor.");
                    return await CompareEnginesAsync(image, language);
                }
                
                _logger.LogInformation($"{regions.Count} metin bÃ¶lgesiyle paralel karÅŸÄ±laÅŸtÄ±rma baÅŸlatÄ±lÄ±yor.");
                var allTasks = new List<Task<OcrEngineResult>>();
                foreach(var region in regions)
                {
                    foreach(var kvp in _ocrEngines)
                    {
                        allTasks.Add(ProcessSingleRegionForEngine(kvp.Key, kvp.Value, image, region, language));
                    }
                }
                var allResults = await Task.WhenAll(allTasks);

                var groupedResults = allResults.Where(r => r != null).GroupBy(r => r.EngineType)
                                               .ToDictionary(g => g.Key, g => g.ToList());

                result.EngineResults = CombineRegionResults(groupedResults);
                result.BestEngine = GetBestEngine(result);
                 if(result.EngineResults.TryGetValue(result.BestEngine, out var bestResult))
                {
                    result.BestConfidence = bestResult.Confidence;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("BÃ¶lgelerle OCR karÅŸÄ±laÅŸtÄ±rmasÄ± sÄ±rasÄ±nda hata oluÅŸtu", ex);
            }
            finally
            {
                stopwatch.Stop();
                result.TotalProcessingTime = stopwatch.Elapsed;
                _logger.LogInformation($"BÃ¶lgeli karÅŸÄ±laÅŸtÄ±rma {result.TotalProcessingTime.TotalMilliseconds:F2}ms'de tamamlandÄ±. En iyi motor: {result.BestEngine}");
                OnComparisonCompleted(new OcrComparisonCompletedEventArgs(result));
            }
            return result;
        }

        private async Task<OcrEngineResult> ProcessSingleRegionForEngine(OcrEngineType type, IOcrEngine engine, Bitmap source, Rectangle region, string lang)
        {
            await _concurrencyLimiter.WaitAsync();
            try
            {
                using(var regionImage = CropImage(source, region))
                {
                    return await ProcessEngineAsync(type, engine, regionImage, lang);
                }
            }
            finally
            {
                _concurrencyLimiter.Release();
            }
        }
        
        private async Task<OcrEngineResult> ProcessEngineAsync(OcrEngineType engineType, IOcrEngine engine, Bitmap image, string language)
        {
            var result = new OcrEngineResult { EngineType = engineType };
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var recognizedText = await engine.RecognizeTextAsync(image, language);
                stopwatch.Stop();
                result.RecognizedText = recognizedText;
                result.ProcessingTime = stopwatch.Elapsed;
                result.IsSuccessful = !string.IsNullOrWhiteSpace(recognizedText);
                result.Confidence = CalculateConfidence(recognizedText, image);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.ProcessingTime = stopwatch.Elapsed;
                result.IsSuccessful = false;
                result.ErrorMessage = ex.Message;
                result.Confidence = 0;
                _logger.LogError($"{engineType} iÅŸlenirken hata oluÅŸtu", ex);
            }
            return result;
        }
        
        public OcrEngineType GetBestEngine(OcrComparisonResult result)
        {
            if (result?.EngineResults == null || !result.EngineResults.Any())
            {
                return OcrEngineType.Tesseract; 
            }

            var successfulResults = result.EngineResults.Values
                .Where(r => r.IsSuccessful && !string.IsNullOrWhiteSpace(r.RecognizedText))
                .ToList();

            if (!successfulResults.Any())
            {
                // BaÅŸarÄ±lÄ± sonuÃ§ yoksa.
                return result.EngineResults.Values
                    .OrderBy(r => string.IsNullOrEmpty(r.ErrorMessage))
                    .ThenBy(r=>r.ProcessingTime)
                    .FirstOrDefault()?.EngineType ?? _ocrEngines.Keys.First();
            }

            return successfulResults
                .OrderByDescending(r => r.Confidence)
                .ThenBy(r => r.ProcessingTime) 
                .First()
                .EngineType;
        }

        public async Task<OcrComparisonReport> GenerateComparisonReportAsync(List<OcrComparisonResult> results)
        {
            return await Task.Run(() =>
            {
                var report = new OcrComparisonReport
                {
                    GeneratedAt = DateTime.Now,
                    TotalComparisons = results.Count
                };

                if (!results.Any())
                {
                    return report;
                }

                //  istatistikleri baÅŸlat
                foreach (var engineType in _ocrEngines.Keys)
                {
                    report.EngineStats[engineType] = new EngineAccuracyStats
                    {
                        EngineType = engineType
                    };
                }

                // Her istatistikleri hesapla
                foreach (var result in results)
                {
                    foreach (var kvp in result.EngineResults)
                    {
                        var engineType = kvp.Key;
                        var engineResult = kvp.Value;
                        var stats = report.EngineStats[engineType];

                        stats.TotalTests++;

                        if (engineResult.IsSuccessful)
                        {
                            stats.SuccessfulTests++;
                            stats.AverageConfidence += engineResult.Confidence;

                            if (engineResult.Confidence > stats.BestConfidence)
                                stats.BestConfidence = engineResult.Confidence;

                            if (engineResult.Confidence < stats.WorstConfidence)
                                stats.WorstConfidence = engineResult.Confidence;
                        }

                        stats.AverageProcessingTime += engineResult.ProcessingTime.TotalMilliseconds;
                    }

                    if (result.EngineResults.ContainsKey(result.BestEngine))
                    {
                        report.EngineStats[result.BestEngine].Wins++;
                    }
                }

                foreach (var stats in report.EngineStats.Values)
                {
                    if (stats.TotalTests > 0)
                    {
                        stats.SuccessRate = (double)stats.SuccessfulTests / stats.TotalTests;
                        if (stats.SuccessfulTests > 0)
                        {
                            stats.AverageConfidence = stats.AverageConfidence / stats.SuccessfulTests;
                        }
                        stats.AverageProcessingTime = stats.AverageProcessingTime / stats.TotalTests;
                        stats.WinRate = (double)stats.Wins / report.TotalComparisons;
                    }
                }

                // Genel olarak en iyi motoru belirlemek iÃ§in
                if (report.EngineStats.Any())
                {
                    report.OverallBestEngine = report.EngineStats
                        .OrderByDescending(kvp => kvp.Value.WinRate)
                        .ThenByDescending(kvp => kvp.Value.AverageConfidence)
                        .First().Key;
                }

                report.AverageProcessingTime = results.Average(r => r.TotalProcessingTime.TotalMilliseconds);
                //
                // Ã–neriler oluÅŸturmak iÃ§in
                GenerateRecommendations(report);

                _logger.LogInformation($"DoÄŸruluk raporu oluÅŸturuldu: {report.TotalComparisons} karÅŸÄ±laÅŸtÄ±rma, en iyi motor: {report.OverallBestEngine}");
                return report;
            });
        }
        
        private void GenerateRecommendations(OcrComparisonReport report)
        {
            var recommendations = new Dictionary<string, object>();

            // En iyi motor Ã¶nerisi
            recommendations["EnIyiMotor"] = report.OverallBestEngine.ToString();

            // Performans Ã¶nerileri
            if (report.EngineStats.Any())
            {
                var fastestEngine = report.EngineStats
                    .OrderBy(kvp => kvp.Value.AverageProcessingTime)
                    .First();
                recommendations["EnHizliMotor"] = fastestEngine.Key.ToString();

                // DoÄŸruluk Ã¶nerileri
                var mostAccurateEngine = report.EngineStats
                    .OrderByDescending(kvp => kvp.Value.AverageConfidence)
                    .First();
                recommendations["EnDogruMotor"] = mostAccurateEngine.Key.ToString();
            }

            // Genel Ã¶neriler
            if (report.AverageProcessingTime > 1000)
            {
                recommendations["PerformansUyarisi"] = "Ä°ÅŸlem sÃ¼resi yÃ¼ksek. GÃ¶rÃ¼ntÃ¼ Ã§Ã¶zÃ¼nÃ¼rlÃ¼ÄŸÃ¼nÃ¼ dÃ¼ÅŸÃ¼rmeyi veya daha hÄ±zlÄ± motorlar kullanmayÄ± dÃ¼ÅŸÃ¼nÃ¼n.";
            }

            if (report.EngineStats.Values.Any(s => s.SuccessRate < 0.7))
            {
                recommendations["DogrulukUyarisi"] = "BazÄ± motorlarÄ±n baÅŸarÄ± oranlarÄ± dÃ¼ÅŸÃ¼k. GÃ¶rÃ¼ntÃ¼ Ã¶n iÅŸleme iyileÅŸtirmelerini dÃ¼ÅŸÃ¼nÃ¼n.";
            }

            report.Recommendations = recommendations;
        }

        private Dictionary<OcrEngineType, OcrEngineResult> CombineRegionResults(Dictionary<OcrEngineType, List<OcrEngineResult>> groupedResults)
        {
            var finalResults = new Dictionary<OcrEngineType, OcrEngineResult>();

            foreach (var kvp in groupedResults)
            {
                var engineType = kvp.Key;
                var successfulResults = kvp.Value.Where(r => r.IsSuccessful).ToList();

                var combinedResult = new OcrEngineResult { EngineType = engineType };

                if (successfulResults.Any())
                {
                    combinedResult.RecognizedText = string.Join(" ", successfulResults.Select(r => r.RecognizedText));
                    combinedResult.Confidence = successfulResults.Average(r => r.Confidence);
                    combinedResult.ProcessingTime = TimeSpan.FromMilliseconds(successfulResults.Sum(r => r.ProcessingTime.TotalMilliseconds));
                    combinedResult.IsSuccessful = true;
                }
                finalResults[engineType] = combinedResult;
            }
            return finalResults;
        }

        private double CalculateConfidence(string recognizedText, Bitmap image)
        {
            if (string.IsNullOrWhiteSpace(recognizedText)) return 0;
            double confidence = 0.5;
            var textLength = recognizedText.Length;
            if (textLength > 10) confidence += 0.2;
            else if (textLength > 5) confidence += 0.1;

            var commonChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var commonCharCount = recognizedText.Count(c => commonChars.Contains(c));
            if(textLength > 0) confidence += (double)commonCharCount / textLength * 0.2;

            var imageArea = image.Width * image.Height;
            if (imageArea > 100000) confidence += 0.1;
            else if (imageArea > 50000) confidence += 0.05;

            var specialCharCount = recognizedText.Count(c => !commonChars.Contains(c) && !char.IsWhiteSpace(c));
            if (textLength > 0 && specialCharCount > 0)
            {
                confidence -= (double)specialCharCount / textLength * 0.1;
            }
            return Math.Min(1.0, Math.Max(0.0, confidence));
        }

        private List<Rectangle> DetectTextRegions(Bitmap image)
        {
            var regions = new List<Rectangle>();
            try
            {
                using (var mat = BitmapConverter.ToMat(image))
                using (var gray = mat.CvtColor(ColorConversionCodes.BGR2GRAY))
                using (var binary = new Mat())
                {
                    Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 11, 2);
                    Cv2.FindContours(binary, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                    foreach (var contour in contours)
                    {
                        var rect = Cv2.BoundingRect(contour);
                        if (rect.Width > 20 && rect.Height > 10)
                        {
                            regions.Add(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Metin bÃ¶lgeleri tespit edilirken hata oluÅŸtu", ex);
            }
            return regions;
        }

        private Bitmap CropImage(Bitmap image, Rectangle region)
        {
            return image.Clone(region, image.PixelFormat);
        }

        protected virtual void OnComparisonCompleted(OcrComparisonCompletedEventArgs e)
        {
            ComparisonCompleted?.Invoke(this, e);
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
                    _concurrencyLimiter?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}

