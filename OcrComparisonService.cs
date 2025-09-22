using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public class OcrComparisonService : IOcrComparisonService
    {
        private readonly ILogger _logger;
        private readonly Dictionary<OcrEngineType, IOcrEngine> _ocrEngines;
        private readonly AppSettings _appSettings;

        public event EventHandler<OcrComparisonCompletedEventArgs> ComparisonCompleted;

        public OcrComparisonService(ILogger logger, AppSettings appSettings)
        {
            _logger = logger;
            _appSettings = appSettings;
            _ocrEngines = new Dictionary<OcrEngineType, IOcrEngine>
            {
                { OcrEngineType.Tesseract, new TesseractOcrEngine(logger, appSettings) },
                { OcrEngineType.WindowsOcr, new WindowsOcrEngine(logger) }
            };
        }

        public async Task<OcrComparisonResult> CompareEnginesAsync(Bitmap image, string language)
        {
            var result = new OcrComparisonResult
            {
                Timestamp = DateTime.Now,
                SourceImage = image,
                Language = language
            };

            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.LogInformation($"{language} dili için OCR karþýlaþtýrmasý baþlatýlýyor");

                // Tüm motorlarý paralel olarak çalýþtýr
                var tasks = _ocrEngines.Select(kvp =>
                    ProcessEngineAsync(kvp.Key, kvp.Value, image, language));

                var engineResults = await Task.WhenAll(tasks);

                // Sonuçlarý karþýlaþtýrmaya ekle
                foreach (var engineResult in engineResults)
                {
                    result.EngineResults[engineResult.EngineType] = engineResult;
                }

                // En iyi motoru belirle
                result.BestEngine = GetBestEngine(result);
                result.BestConfidence = result.EngineResults[result.BestEngine].Confidence;

                stopwatch.Stop();
                result.TotalProcessingTime = stopwatch.Elapsed;

                _logger.LogInformation($"OCR karþýlaþtýrmasý {result.TotalProcessingTime.TotalMilliseconds:F2}ms içinde tamamlandý. En iyi motor: {result.BestEngine} (güven: {result.BestConfidence:P})");

                OnComparisonCompleted(new OcrComparisonCompletedEventArgs(result));
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError("OCR karþýlaþtýrmasý sýrasýnda hata oluþtu", ex);
                stopwatch.Stop();
                result.TotalProcessingTime = stopwatch.Elapsed;
                return result;
            }
        }

        public async Task<OcrComparisonResult> CompareEnginesWithRegionsAsync(Bitmap image, string language)
        {
            var result = new OcrComparisonResult
            {
                Timestamp = DateTime.Now,
                SourceImage = image,
                Language = language
            };

            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.LogInformation($"{language} dili için bölgelerle OCR karþýlaþtýrmasý baþlatýlýyor");

                // Ýlk olarak, OpenCV kullanarak metin bölgelerini tespit et
                var regions = DetectTextRegions(image);

                if (regions.Count == 0)
                {
                    _logger.LogWarning("Metin bölgesi tespit edilemedi, tam görüntü karþýlaþtýrmasýna geri dönülüyor");
                    return await CompareEnginesAsync(image, language);
                }

                _logger.LogInformation($"{regions.Count} metin bölgesi tespit edildi");

               
                var allRegionResults = new List<Dictionary<OcrEngineType, OcrEngineResult>>();

                foreach (var region in regions)
                {
                    using (var regionImage = CropImage(image, region))
                    {
                        var regionTasks = _ocrEngines.Select(kvp =>
                            ProcessEngineAsync(kvp.Key, kvp.Value, regionImage, language));

                        var regionEngineResults = await Task.WhenAll(regionTasks);
                        var regionResultDict = regionEngineResults.ToDictionary(r => r.EngineType, r => r);
                        allRegionResults.Add(regionResultDict);
                    }
                }

                // Tüm bölgelerden gelen sonuçlarý birleþtir
                result.EngineResults = CombineRegionResults(allRegionResults);

                // En iyi motoru belirle
                result.BestEngine = GetBestEngine(result);
                result.BestConfidence = result.EngineResults[result.BestEngine].Confidence;

                stopwatch.Stop();
                result.TotalProcessingTime = stopwatch.Elapsed;

                _logger.LogInformation($"Bölgelerle OCR karþýlaþtýrmasý {result.TotalProcessingTime.TotalMilliseconds:F2}ms içinde tamamlandý. En iyi motor: {result.BestEngine} (güven: {result.BestConfidence:P})");

                OnComparisonCompleted(new OcrComparisonCompletedEventArgs(result));
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError("Bölgelerle OCR karþýlaþtýrmasý sýrasýnda hata oluþtu", ex);
                stopwatch.Stop();
                result.TotalProcessingTime = stopwatch.Elapsed;
                return result;
            }
        }

        public OcrEngineType GetBestEngine(OcrComparisonResult result)
        {
            if (result?.EngineResults == null || !result.EngineResults.Any())
            {
                return OcrEngineType.Tesseract; // Varsayýlan 
            }

            // Baþarýlý sonuçlar arasýnda en yüksek güvene sahip motoru bul
            var successfulResults = result.EngineResults.Values
                .Where(r => r.IsSuccessful && !string.IsNullOrWhiteSpace(r.RecognizedText))
                .ToList();

            if (!successfulResults.Any())
            {
                return result.EngineResults.Keys.First(); // Mevcut ilk motoru döndür
            }

            return successfulResults
                .OrderByDescending(r => r.Confidence)
                .ThenBy(r => r.ProcessingTime) // Güven eþitse daha hýzlý motorlarý tercih et
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

                // Motor istatistiklerini baþlat
                foreach (var engineType in _ocrEngines.Keys)
                {
                    report.EngineStats[engineType] = new EngineAccuracyStats
                    {
                        EngineType = engineType
                    };
                }

                // Her motor için istatistikleri hesapla
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

                            if (stats.WorstConfidence == 0 || engineResult.Confidence < stats.WorstConfidence)
                                stats.WorstConfidence = engineResult.Confidence;
                        }

                        stats.AverageProcessingTime += engineResult.ProcessingTime.TotalMilliseconds;
                    }

                  
                    if (result.BestEngine != OcrEngineType.Tesseract || result.EngineResults.ContainsKey(result.BestEngine))
                    {
                        report.EngineStats[result.BestEngine].Wins++;
                    }
                }

                
                foreach (var stats in report.EngineStats.Values)
                {
                    if (stats.TotalTests > 0)
                    {
                        stats.SuccessRate = (double)stats.SuccessfulTests / stats.TotalTests;
                        stats.AverageConfidence = stats.AverageConfidence / stats.SuccessfulTests;
                        stats.AverageProcessingTime = stats.AverageProcessingTime / stats.TotalTests;
                        stats.WinRate = (double)stats.Wins / report.TotalComparisons;
                    }
                }

                // Genel olarak en iyi motoru belirlemek için
                report.OverallBestEngine = report.EngineStats
                    .OrderByDescending(kvp => kvp.Value.WinRate)
                    .ThenByDescending(kvp => kvp.Value.AverageConfidence)
                    .First().Key;

               
                report.AverageProcessingTime = results.Average(r => r.TotalProcessingTime.TotalMilliseconds);

                // Öneriler oluþturmak içim
                GenerateRecommendations(report);

                _logger.LogInformation($"Doðruluk raporu oluþturuldu: {report.TotalComparisons} karþýlaþtýrma, en iyi motor: {report.OverallBestEngine}");
                return report;
            });
        }

        private async Task<OcrEngineResult> ProcessEngineAsync(OcrEngineType engineType, IOcrEngine engine, Bitmap image, string language)
        {
            var result = new OcrEngineResult
            {
                EngineType = engineType
            };

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var recognizedText = await engine.RecognizeTextAsync(image, language);
                stopwatch.Stop();

                result.RecognizedText = recognizedText;
                result.ProcessingTime = stopwatch.Elapsed;
                result.IsSuccessful = !string.IsNullOrWhiteSpace(recognizedText);

                // Metin uzunluðuna ve içeriðine göre güveni hesaplama
                result.Confidence = CalculateConfidence(recognizedText, image);

                _logger.LogInformation($"{engineType} {result.ProcessingTime.TotalMilliseconds:F2}ms'de tamamlandý, güven: {result.Confidence:P}");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.ProcessingTime = stopwatch.Elapsed;
                result.IsSuccessful = false;
                result.ErrorMessage = ex.Message;
                result.Confidence = 0;

                _logger.LogError($"{engineType} iþlenirken hata oluþtu", ex);
            }

            return result;
        }

        private double CalculateConfidence(string recognizedText, Bitmap image)
        {
            if (string.IsNullOrWhiteSpace(recognizedText))
                return 0;

            double confidence = 0.5; // Temel güven

            // Metin uzunluðu (daha uzun metin genellikle daha güvenilirdir)
            var textLength = recognizedText.Length;
            if (textLength > 10) confidence += 0.2;
            else if (textLength > 5) confidence += 0.1;

          
            var commonChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var commonCharCount = recognizedText.Count(c => commonChars.Contains(c));
            confidence += (double)commonCharCount / textLength * 0.2;

            // Görüntü boyutu
            var imageArea = image.Width * image.Height;
            if (imageArea > 100000) confidence += 0.1;
            else if (imageArea > 50000) confidence += 0.05;

            return Math.Min(1.0, confidence);
        }

        private List<Rectangle> DetectTextRegions(Bitmap image)
        {
            // OpenCV kullanarak basit metin bölgesi tespiti
          
            var regions = new List<Rectangle>();

            try
            {
                using (var mat = OpenCvSharp.Extensions.BitmapConverter.ToMat(image))
                using (var gray = mat.CvtColor(OpenCvSharp.ColorConversionCodes.BGR2GRAY))
                using (var binary = new OpenCvSharp.Mat())
                {
                    OpenCvSharp.Cv2.AdaptiveThreshold(gray, binary, 255,
                        OpenCvSharp.AdaptiveThresholdTypes.GaussianC,
                        OpenCvSharp.ThresholdTypes.Binary, 11, 2);

                    OpenCvSharp.Cv2.FindContours(binary, out var contours, out _,
                        OpenCvSharp.RetrievalModes.External,
                        OpenCvSharp.ContourApproximationModes.ApproxSimple);

                    foreach (var contour in contours)
                    {
                        var rect = OpenCvSharp.Cv2.BoundingRect(contour);
                        if (rect.Width > 20 && rect.Height > 10)
                        {
                            regions.Add(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Metin bölgeleri tespit edilirken hata oluþtu", ex);
            }

            return regions;
        }

        private Bitmap CropImage(Bitmap image, Rectangle region)
        {
            return image.Clone(region, image.PixelFormat);
        }

        private Dictionary<OcrEngineType, OcrEngineResult> CombineRegionResults(List<Dictionary<OcrEngineType, OcrEngineResult>> regionResults)
        {
            var combinedResults = new Dictionary<OcrEngineType, OcrEngineResult>();

            foreach (var engineType in _ocrEngines.Keys)
            {
                var combinedResult = new OcrEngineResult
                {
                    EngineType = engineType,
                    RecognizedText = "",
                    Confidence = 0,
                    ProcessingTime = TimeSpan.Zero,
                    IsSuccessful = false
                };

                var successfulResults = regionResults
                    .Where(r => r.ContainsKey(engineType) && r[engineType].IsSuccessful)
                    .Select(r => r[engineType])
                    .ToList();

                if (successfulResults.Any())
                {
                    combinedResult.RecognizedText = string.Join(" ", successfulResults.Select(r => r.RecognizedText));
                    combinedResult.Confidence = successfulResults.Average(r => r.Confidence);
                    combinedResult.ProcessingTime = TimeSpan.FromMilliseconds(successfulResults.Sum(r => r.ProcessingTime.TotalMilliseconds));
                    combinedResult.IsSuccessful = true;
                }

                combinedResults[engineType] = combinedResult;
            }

            return combinedResults;
        }

        private void GenerateRecommendations(OcrComparisonReport report)
        {
            var recommendations = new Dictionary<string, object>();

            // En iyi motor önerisi
            recommendations["EnIyiMotor"] = report.OverallBestEngine.ToString();

            // Performans önerileri
            var fastestEngine = report.EngineStats
                .OrderBy(kvp => kvp.Value.AverageProcessingTime)
                .First();
            recommendations["EnHizliMotor"] = fastestEngine.Key.ToString();

            // Doðruluk önerileri
            var mostAccurateEngine = report.EngineStats
                .OrderByDescending(kvp => kvp.Value.AverageConfidence)
                .First();
            recommendations["EnDogruMotor"] = mostAccurateEngine.Key.ToString();

            // Genel öneriler
            if (report.AverageProcessingTime > 1000)
            {
                recommendations["PerformansUyarisi"] = "Ýþlem süresi yüksek. Görüntü çözünürlüðünü düþürmeyi veya daha hýzlý motorlar kullanmayý düþünün.";
            }

            if (report.EngineStats.Values.Any(s => s.SuccessRate < 0.7))
            {
                recommendations["DogrulukUyarisi"] = "Bazý motorlarýn baþarý oranlarý düþük. Görüntü ön iþleme iyileþtirmelerini düþünün.";
            }

            report.Recommendations = recommendations;
        }

        protected virtual void OnComparisonCompleted(OcrComparisonCompletedEventArgs e)
        {
            ComparisonCompleted?.Invoke(this, e);
        }
    }
}