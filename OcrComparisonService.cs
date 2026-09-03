using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public class OcrComparisonService : IOcrComparisonService, IDisposable
    {
        private readonly ILogger _logger;
        private readonly AppSettings _appSettings;

        private readonly Dictionary<OcrEngineType, IOcrEngine> _ocrEngines;
        private readonly Dictionary<OcrEngineType, SemaphoreSlim> _engineLocks;

        private readonly SemaphoreSlim _comparisonLimiter;

        private int _disposed;

        public event EventHandler<OcrComparisonCompletedEventArgs> ComparisonCompleted;

        public OcrComparisonService(
            ILogger logger,
            AppSettings appSettings)
        {
            _logger =
                logger ?? throw new ArgumentNullException(nameof(logger));

            _appSettings =
                appSettings ?? throw new ArgumentNullException(nameof(appSettings));

            _ocrEngines =
                new Dictionary<OcrEngineType, IOcrEngine>
                {
                    {
                        OcrEngineType.Tesseract,
                        new TesseractOcrEngine(
                            logger,
                            appSettings)
                    },
                    {
                        OcrEngineType.WindowsOcr,
                        new WindowsOcrEngine(logger)
                    }
                };

            _engineLocks =
                new Dictionary<OcrEngineType, SemaphoreSlim>();

            foreach (OcrEngineType engineType in _ocrEngines.Keys)
            {
                _engineLocks[engineType] =
                    new SemaphoreSlim(1, 1);
            }

            int maxComparisons =
                Math.Max(
                    1,
                    Math.Min(
                        Environment.ProcessorCount,
                        4));

            _comparisonLimiter =
                new SemaphoreSlim(
                    maxComparisons,
                    maxComparisons);
        }

        public async Task<OcrComparisonResult> CompareEnginesAsync(
            Bitmap image,
            string language)
        {
            ThrowIfDisposed();

            var result =
                CreateResult(
                    image,
                    language);

            Stopwatch stopwatch =
                Stopwatch.StartNew();

            try
            {
                if (image == null)
                    return result;

                await _comparisonLimiter
                    .WaitAsync()
                    .ConfigureAwait(false);

                try
                {
                    result.EngineResults =
                        await CompareWholeImageCoreAsync(
                                image,
                                language)
                            .ConfigureAwait(false);

                    CompleteBestEngine(
                        result);
                }
                finally
                {
                    _comparisonLimiter.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "OCR karşılaştırması sırasında hata oluştu.",
                    ex);
            }
            finally
            {
                stopwatch.Stop();

                result.TotalProcessingTime =
                    stopwatch.Elapsed;

                OnComparisonCompleted(
                    new OcrComparisonCompletedEventArgs(
                        result));
            }

            return result;
        }

        public async Task<OcrComparisonResult> CompareEnginesWithRegionsAsync(
            Bitmap image,
            string language)
        {
            ThrowIfDisposed();

            var result =
                CreateResult(
                    image,
                    language);

            Stopwatch stopwatch =
                Stopwatch.StartNew();

            try
            {
                if (image == null)
                    return result;

                await _comparisonLimiter
                    .WaitAsync()
                    .ConfigureAwait(false);

                try
                {
                    List<Rectangle> regions =
                        DetectTextRegions(
                            image);

                    if (regions == null ||
                        regions.Count == 0)
                    {
                        result.EngineResults =
                            await CompareWholeImageCoreAsync(
                                    image,
                                    language)
                                .ConfigureAwait(false);
                    }
                    else
                    {
                        result.EngineResults =
                            await CompareRegionsCoreAsync(
                                    image,
                                    regions,
                                    language)
                                .ConfigureAwait(false);
                    }

                    CompleteBestEngine(
                        result);
                }
                finally
                {
                    _comparisonLimiter.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Bölgeli OCR karşılaştırması sırasında hata oluştu.",
                    ex);
            }
            finally
            {
                stopwatch.Stop();

                result.TotalProcessingTime =
                    stopwatch.Elapsed;

                OnComparisonCompleted(
                    new OcrComparisonCompletedEventArgs(
                        result));
            }

            return result;
        }

        private async Task<Dictionary<OcrEngineType, OcrEngineResult>>
            CompareWholeImageCoreAsync(
                Bitmap image,
                string language)
        {
            Task<OcrEngineResult>[] tasks =
                _ocrEngines
                    .Select(
                        pair =>
                            ProcessEngineAsync(
                                pair.Key,
                                pair.Value,
                                image,
                                language))
                    .ToArray();

            OcrEngineResult[] results =
                await Task.WhenAll(tasks)
                    .ConfigureAwait(false);

            return results
                .Where(
                    item => item != null)
                .GroupBy(
                    item => item.EngineType)
                .ToDictionary(
                    group => group.Key,
                    group => group.First());
        }

        private async Task<Dictionary<OcrEngineType, OcrEngineResult>>
            CompareRegionsCoreAsync(
                Bitmap image,
                List<Rectangle> regions,
                string language)
        {
            var tasks =
                new List<Task<KeyValuePair<OcrEngineType, OcrEngineResult>>>();

            foreach (KeyValuePair<OcrEngineType, IOcrEngine> enginePair
                     in _ocrEngines)
            {
                OcrEngineType engineType =
                    enginePair.Key;

                IOcrEngine engine =
                    enginePair.Value;

                tasks.Add(
                    ProcessAllRegionsForEngineAsync(
                        engineType,
                        engine,
                        image,
                        regions,
                        language));
            }

            KeyValuePair<OcrEngineType, OcrEngineResult>[] results =
                await Task.WhenAll(tasks)
                    .ConfigureAwait(false);

            return results.ToDictionary(
                pair => pair.Key,
                pair => pair.Value);
        }

        private async Task<KeyValuePair<OcrEngineType, OcrEngineResult>>
            ProcessAllRegionsForEngineAsync(
                OcrEngineType engineType,
                IOcrEngine engine,
                Bitmap source,
                List<Rectangle> regions,
                string language)
        {
            var regionResults =
                new List<OcrEngineResult>();

            foreach (Rectangle region in regions)
            {
                Rectangle safeRegion =
                    ClampRegion(
                        region,
                        source.Width,
                        source.Height);

                if (safeRegion.Width <= 0 ||
                    safeRegion.Height <= 0)
                {
                    continue;
                }

                try
                {
                    using (Bitmap regionImage =
                           CropImage(
                               source,
                               safeRegion))
                    {
                        if (regionImage == null)
                            continue;

                        OcrEngineResult regionResult =
                            await ProcessEngineAsync(
                                    engineType,
                                    engine,
                                    regionImage,
                                    language)
                                .ConfigureAwait(false);

                        if (regionResult != null)
                        {
                            regionResults.Add(
                                regionResult);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        $"{engineType} bölgesi işlenemedi: {ex.Message}");
                }
            }

            OcrEngineResult combined =
                CombineSingleEngineRegionResults(
                    engineType,
                    regionResults);

            return new KeyValuePair<OcrEngineType, OcrEngineResult>(
                engineType,
                combined);
        }

        private async Task<OcrEngineResult> ProcessEngineAsync(
            OcrEngineType engineType,
            IOcrEngine engine,
            Bitmap image,
            string language)
        {
            var result =
                new OcrEngineResult
                {
                    EngineType =
                        engineType
                };

            if (engine == null ||
                image == null)
            {
                return result;
            }

            Stopwatch stopwatch =
                Stopwatch.StartNew();

            SemaphoreSlim engineLock =
                _engineLocks[engineType];

            await engineLock
                .WaitAsync()
                .ConfigureAwait(false);

            try
            {
                string recognizedText =
                    await engine
                        .RecognizeTextAsync(
                            image,
                            language)
                        .ConfigureAwait(false);

                recognizedText =
                    OcrTextCorrector.CorrectText(
                        recognizedText,
                        language,
                        true,
                        _logger);

                result.RecognizedText =
                    recognizedText ?? string.Empty;

                result.IsSuccessful =
                    !string.IsNullOrWhiteSpace(
                        recognizedText);

                result.Confidence =
                    CalculateConfidence(
                        recognizedText,
                        image,
                        language);
            }
            catch (Exception ex)
            {
                result.IsSuccessful =
                    false;

                result.ErrorMessage =
                    ex.Message;

                result.Confidence =
                    0;

                _logger.LogError(
                    $"{engineType} OCR işlemi sırasında hata oluştu.",
                    ex);
            }
            finally
            {
                stopwatch.Stop();

                result.ProcessingTime =
                    stopwatch.Elapsed;

                engineLock.Release();
            }

            return result;
        }

        public OcrEngineType GetBestEngine(
            OcrComparisonResult result)
        {
            if (result == null ||
                result.EngineResults == null ||
                result.EngineResults.Count == 0)
            {
                return GetDefaultEngine();
            }

            List<OcrEngineResult> successful =
                result.EngineResults
                    .Values
                    .Where(
                        engineResult =>
                            engineResult != null &&
                            engineResult.IsSuccessful &&
                            !string.IsNullOrWhiteSpace(
                                engineResult.RecognizedText))
                    .ToList();

            if (successful.Count == 0)
            {
                OcrEngineResult fallback =
                    result.EngineResults
                        .Values
                        .Where(
                            engineResult =>
                                engineResult != null)
                        .OrderBy(
                            engineResult =>
                                string.IsNullOrEmpty(
                                    engineResult.ErrorMessage)
                                    ? 0
                                    : 1)
                        .ThenBy(
                            engineResult =>
                                engineResult.ProcessingTime)
                        .FirstOrDefault();

                if (fallback != null)
                    return fallback.EngineType;

                return GetDefaultEngine();
            }

            return successful
                .OrderByDescending(
                    engineResult =>
                        engineResult.Confidence)
                .ThenBy(
                    engineResult =>
                        engineResult.ProcessingTime)
                .First()
                .EngineType;
        }

        private void CompleteBestEngine(
            OcrComparisonResult result)
        {
            if (result == null)
                return;

            result.BestEngine =
                GetBestEngine(
                    result);

            OcrEngineResult bestResult;

            if (result.EngineResults != null &&
                result.EngineResults.TryGetValue(
                    result.BestEngine,
                    out bestResult) &&
                bestResult != null)
            {
                result.BestConfidence =
                    bestResult.Confidence;
            }
            else
            {
                result.BestConfidence =
                    0;
            }
        }

        public async Task<OcrComparisonReport> GenerateComparisonReportAsync(
            List<OcrComparisonResult> results)
        {
            ThrowIfDisposed();

            return await Task.Run(
                () => GenerateComparisonReport(results))
                .ConfigureAwait(false);
        }

        private OcrComparisonReport GenerateComparisonReport(
            List<OcrComparisonResult> results)
        {
            var safeResults =
                results == null
                    ? new List<OcrComparisonResult>()
                    : results
                        .Where(result => result != null)
                        .ToList();

            var report =
                new OcrComparisonReport
                {
                    GeneratedAt =
                        DateTime.Now,

                    TotalComparisons =
                        safeResults.Count
                };

            foreach (OcrEngineType engineType in _ocrEngines.Keys)
            {
                report.EngineStats[engineType] =
                    new EngineAccuracyStats
                    {
                        EngineType =
                            engineType,

                        WorstConfidence =
                            double.MaxValue
                    };
            }

            if (safeResults.Count == 0)
            {
                NormalizeWorstConfidence(
                    report);

                GenerateRecommendations(
                    report);

                return report;
            }

            foreach (OcrComparisonResult comparison in safeResults)
            {
                if (comparison.EngineResults == null)
                    continue;

                foreach (KeyValuePair<OcrEngineType, OcrEngineResult> pair
                         in comparison.EngineResults)
                {
                    EngineAccuracyStats stats;

                    if (!report.EngineStats.TryGetValue(
                            pair.Key,
                            out stats))
                    {
                        stats =
                            new EngineAccuracyStats
                            {
                                EngineType =
                                    pair.Key,

                                WorstConfidence =
                                    double.MaxValue
                            };

                        report.EngineStats[pair.Key] =
                            stats;
                    }

                    OcrEngineResult engineResult =
                        pair.Value;

                    if (engineResult == null)
                        continue;

                    stats.TotalTests++;

                    stats.AverageProcessingTime +=
                        engineResult
                            .ProcessingTime
                            .TotalMilliseconds;

                    if (!engineResult.IsSuccessful)
                        continue;

                    stats.SuccessfulTests++;

                    stats.AverageConfidence +=
                        engineResult.Confidence;

                    if (engineResult.Confidence >
                        stats.BestConfidence)
                    {
                        stats.BestConfidence =
                            engineResult.Confidence;
                    }

                    if (engineResult.Confidence <
                        stats.WorstConfidence)
                    {
                        stats.WorstConfidence =
                            engineResult.Confidence;
                    }
                }

                if (comparison.EngineResults.ContainsKey(
                    comparison.BestEngine))
                {
                    EngineAccuracyStats bestStats;

                    if (report.EngineStats.TryGetValue(
                            comparison.BestEngine,
                            out bestStats))
                    {
                        bestStats.Wins++;
                    }
                }
            }

            foreach (EngineAccuracyStats stats
                     in report.EngineStats.Values)
            {
                if (stats.TotalTests > 0)
                {
                    stats.SuccessRate =
                        (double)stats.SuccessfulTests /
                        stats.TotalTests;

                    stats.AverageProcessingTime =
                        stats.AverageProcessingTime /
                        stats.TotalTests;
                }

                if (stats.SuccessfulTests > 0)
                {
                    stats.AverageConfidence =
                        stats.AverageConfidence /
                        stats.SuccessfulTests;
                }

                stats.WinRate =
                    report.TotalComparisons > 0
                        ? (double)stats.Wins /
                          report.TotalComparisons
                        : 0;

                if (stats.WorstConfidence ==
                    double.MaxValue)
                {
                    stats.WorstConfidence =
                        0;
                }
            }

            if (report.EngineStats.Count > 0)
            {
                report.OverallBestEngine =
                    report.EngineStats
                        .OrderByDescending(
                            pair =>
                                pair.Value.WinRate)
                        .ThenByDescending(
                            pair =>
                                pair.Value.AverageConfidence)
                        .ThenBy(
                            pair =>
                                pair.Value.AverageProcessingTime)
                        .First()
                        .Key;
            }

            report.AverageProcessingTime =
                safeResults.Count > 0
                    ? safeResults.Average(
                        comparison =>
                            comparison
                                .TotalProcessingTime
                                .TotalMilliseconds)
                    : 0;

            GenerateRecommendations(
                report);

            return report;
        }

        private static void NormalizeWorstConfidence(
            OcrComparisonReport report)
        {
            foreach (EngineAccuracyStats stats
                     in report.EngineStats.Values)
            {
                if (stats.WorstConfidence ==
                    double.MaxValue)
                {
                    stats.WorstConfidence = 0;
                }
            }
        }

        private void GenerateRecommendations(
            OcrComparisonReport report)
        {
            var recommendations =
                new Dictionary<string, object>();

            if (report.EngineStats != null &&
                report.EngineStats.Count > 0)
            {
                recommendations["EnIyiMotor"] =
                    report
                        .OverallBestEngine
                        .ToString();

                KeyValuePair<OcrEngineType, EngineAccuracyStats> fastest =
                    report.EngineStats
                        .OrderBy(
                            pair =>
                                pair.Value.AverageProcessingTime)
                        .First();

                recommendations["EnHizliMotor"] =
                    fastest.Key.ToString();

                KeyValuePair<OcrEngineType, EngineAccuracyStats> accurate =
                    report.EngineStats
                        .OrderByDescending(
                            pair =>
                                pair.Value.AverageConfidence)
                        .First();

                recommendations["EnDogruMotor"] =
                    accurate.Key.ToString();
            }

            if (report.AverageProcessingTime >
                1000)
            {
                recommendations["PerformansUyarisi"] =
                    "OCR işlem süresi yüksek.";
            }

            if (report.EngineStats != null &&
                report.EngineStats.Values.Any(
                    stats =>
                        stats.TotalTests > 0 &&
                        stats.SuccessRate < 0.70))
            {
                recommendations["DogrulukUyarisi"] =
                    "Bazı OCR motorlarının başarı oranı düşük.";
            }

            report.Recommendations =
                recommendations;
        }

        private OcrEngineResult CombineSingleEngineRegionResults(
            OcrEngineType engineType,
            List<OcrEngineResult> results)
        {
            var combined =
                new OcrEngineResult
                {
                    EngineType =
                        engineType
                };

            if (results == null ||
                results.Count == 0)
            {
                return combined;
            }

            List<OcrEngineResult> successful =
                results
                    .Where(
                        result =>
                            result != null &&
                            result.IsSuccessful &&
                            !string.IsNullOrWhiteSpace(
                                result.RecognizedText))
                    .ToList();

            combined.ProcessingTime =
                TimeSpan.FromMilliseconds(
                    results
                        .Where(
                            result =>
                                result != null)
                        .Sum(
                            result =>
                                result
                                    .ProcessingTime
                                    .TotalMilliseconds));

            if (successful.Count == 0)
            {
                OcrEngineResult firstError =
                    results
                        .FirstOrDefault(
                            result =>
                                result != null &&
                                !string.IsNullOrWhiteSpace(
                                    result.ErrorMessage));

                combined.ErrorMessage =
                    firstError != null
                        ? firstError.ErrorMessage
                        : null;

                return combined;
            }

            combined.RecognizedText =
                string.Join(
                    " ",
                    successful
                        .Select(
                            result =>
                                result.RecognizedText.Trim()));

            combined.Confidence =
                successful.Average(
                    result =>
                        result.Confidence);

            combined.IsSuccessful =
                true;

            return combined;
        }

        private double CalculateConfidence(
            string recognizedText,
            Bitmap image,
            string language)
        {
            if (string.IsNullOrWhiteSpace(
                recognizedText))
            {
                return 0;
            }

            string text =
                recognizedText.Trim();

            double confidence =
                0.40;

            int length =
                text.Length;

            if (length >= 20)
                confidence += 0.15;
            else if (length >= 8)
                confidence += 0.10;
            else if (length >= 3)
                confidence += 0.05;

            int letterOrDigitCount =
                text.Count(
                    character =>
                        char.IsLetterOrDigit(character));

            int whitespaceCount =
                text.Count(
                    char.IsWhiteSpace);

            int punctuationCount =
                text.Count(
                    char.IsPunctuation);

            int controlCount =
                text.Count(
                    char.IsControl);

            if (length > 0)
            {
                double readableRatio =
                    (double)(
                        letterOrDigitCount +
                        whitespaceCount +
                        punctuationCount) /
                    length;

                confidence +=
                    readableRatio * 0.25;

                double controlRatio =
                    (double)controlCount /
                    length;

                confidence -=
                    controlRatio * 0.30;
            }

            int replacementCharacters =
                text.Count(
                    character =>
                        character == '\uFFFD');

            if (replacementCharacters > 0)
            {
                confidence -=
                    Math.Min(
                        0.25,
                        replacementCharacters *
                        0.05);
            }

            int repeatedSymbols =
                CountRepeatedSymbols(
                    text);

            if (repeatedSymbols > 0)
            {
                confidence -=
                    Math.Min(
                        0.15,
                        repeatedSymbols *
                        0.02);
            }

            int lineCount =
                text.Count(
                    character =>
                        character == '\n') +
                1;

            if (lineCount <= 8)
            {
                confidence +=
                    0.05;
            }

            if (image != null)
            {
                long imageArea =
                    (long)image.Width *
                    image.Height;

                if (imageArea >= 50000)
                {
                    confidence +=
                        0.05;
                }
            }

            return Clamp01(
                confidence);
        }

        private static int CountRepeatedSymbols(
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
                char current =
                    text[i];

                if (char.IsLetterOrDigit(current) ||
                    char.IsWhiteSpace(current))
                {
                    continue;
                }

                if (text[i - 1] == current &&
                    text[i - 2] == current)
                {
                    count++;
                }
            }

            return count;
        }

        private List<Rectangle> DetectTextRegions(
            Bitmap image)
        {
            var regions =
                new List<Rectangle>();

            if (image == null)
                return regions;

            try
            {
                using (Mat source =
                       BitmapConverter.ToMat(image))
                using (Mat gray =
                       ConvertToGray(source))
                using (Mat binary =
                       new Mat())
                using (Mat kernel =
                       Cv2.GetStructuringElement(
                           MorphShapes.Rect,
                           new OpenCvSharp.Size(15, 3)))
                {
                    Cv2.AdaptiveThreshold(
                        gray,
                        binary,
                        255,
                        AdaptiveThresholdTypes.GaussianC,
                        ThresholdTypes.Binary,
                        11,
                        2);

                    Cv2.MorphologyEx(
                        binary,
                        binary,
                        MorphTypes.Close,
                        kernel);

                    OpenCvSharp.Point[][] contours;
                    HierarchyIndex[] hierarchy;

                    Cv2.FindContours(
                        binary,
                        out contours,
                        out hierarchy,
                        RetrievalModes.External,
                        ContourApproximationModes.ApproxSimple);

                    if (contours == null)
                        return regions;

                    foreach (OpenCvSharp.Point[] contour
                             in contours)
                    {
                        if (contour == null ||
                            contour.Length == 0)
                        {
                            continue;
                        }

                        OpenCvSharp.Rect cvRect =
                            Cv2.BoundingRect(
                                contour);

                        if (cvRect.Width < 12 ||
                            cvRect.Height < 6)
                        {
                            continue;
                        }

                        Rectangle region =
                            ClampRegion(
                                new Rectangle(
                                    cvRect.X,
                                    cvRect.Y,
                                    cvRect.Width,
                                    cvRect.Height),
                                image.Width,
                                image.Height);

                        if (region.Width > 0 &&
                            region.Height > 0)
                        {
                            regions.Add(
                                region);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Metin bölgeleri tespit edilirken hata oluştu.",
                    ex);
            }

            return MergeRegions(
                regions,
                12);
        }

        private static List<Rectangle> MergeRegions(
            List<Rectangle> regions,
            int tolerance)
        {
            if (regions == null ||
                regions.Count <= 1)
            {
                return regions ??
                       new List<Rectangle>();
            }

            var working =
                regions
                    .OrderBy(
                        region =>
                            region.Top)
                    .ThenBy(
                        region =>
                            region.Left)
                    .ToList();

            bool changed;

            do
            {
                changed = false;

                var merged =
                    new List<Rectangle>();

                var used =
                    new bool[working.Count];

                for (int i = 0;
                     i < working.Count;
                     i++)
                {
                    if (used[i])
                        continue;

                    Rectangle current =
                        working[i];

                    used[i] =
                        true;

                    bool expanded;

                    do
                    {
                        expanded = false;

                        for (int j = 0;
                             j < working.Count;
                             j++)
                        {
                            if (used[j])
                                continue;

                            Rectangle expandedCurrent =
                                current;

                            expandedCurrent.Inflate(
                                tolerance,
                                tolerance);

                            if (!expandedCurrent.IntersectsWith(
                                    working[j]))
                            {
                                continue;
                            }

                            current =
                                Rectangle.Union(
                                    current,
                                    working[j]);

                            used[j] =
                                true;

                            expanded =
                                true;

                            changed =
                                true;
                        }
                    }
                    while (expanded);

                    merged.Add(
                        current);
                }

                working =
                    merged;
            }
            while (changed);

            return working
                .OrderBy(
                    region =>
                        region.Top)
                .ThenBy(
                    region =>
                        region.Left)
                .ToList();
        }

        private Bitmap CropImage(
            Bitmap image,
            Rectangle region)
        {
            if (image == null)
                return null;

            Rectangle safeRegion =
                ClampRegion(
                    region,
                    image.Width,
                    image.Height);

            if (safeRegion.Width <= 0 ||
                safeRegion.Height <= 0)
            {
                return null;
            }

            return image.Clone(
                safeRegion,
                image.PixelFormat);
        }

        private static Rectangle ClampRegion(
            Rectangle region,
            int width,
            int height)
        {
            return Rectangle.Intersect(
                new Rectangle(
                    0,
                    0,
                    width,
                    height),
                region);
        }

        private static Mat ConvertToGray(
            Mat source)
        {
            var gray =
                new Mat();

            if (source == null ||
                source.Empty())
            {
                return gray;
            }

            int channels =
                source.Channels();

            if (channels == 1)
            {
                source.CopyTo(
                    gray);

                return gray;
            }

            if (channels == 4)
            {
                Cv2.CvtColor(
                    source,
                    gray,
                    ColorConversionCodes.BGRA2GRAY);

                return gray;
            }

            Cv2.CvtColor(
                source,
                gray,
                ColorConversionCodes.BGR2GRAY);

            return gray;
        }

        private static double Clamp01(
            double value)
        {
            if (value < 0)
                return 0;

            if (value > 1)
                return 1;

            return value;
        }

        private OcrComparisonResult CreateResult(
            Bitmap image,
            string language)
        {
            return new OcrComparisonResult
            {
                Timestamp =
                    DateTime.Now,

                SourceImage =
                    image,

                Language =
                    language ?? string.Empty,

                EngineResults =
                    new Dictionary<OcrEngineType, OcrEngineResult>()
            };
        }

        private OcrEngineType GetDefaultEngine()
        {
            if (_ocrEngines.ContainsKey(
                    _appSettings.OcrEngine))
            {
                return _appSettings.OcrEngine;
            }

            if (_ocrEngines.Count > 0)
            {
                return _ocrEngines.Keys.First();
            }

            return OcrEngineType.Tesseract;
        }

        protected virtual void OnComparisonCompleted(
            OcrComparisonCompletedEventArgs e)
        {
            EventHandler<OcrComparisonCompletedEventArgs> handler =
                ComparisonCompleted;

            if (handler == null)
                return;

            foreach (Delegate subscriber in
                     handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler<OcrComparisonCompletedEventArgs>)subscriber)
                        .Invoke(
                            this,
                            e);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        $"ComparisonCompleted event hatası: {ex.Message}");
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(
                    ref _disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(OcrComparisonService));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) != 0)
            {
                return;
            }

            foreach (SemaphoreSlim engineLock
                     in _engineLocks.Values)
            {
                try
                {
                    engineLock.Dispose();
                }
                catch
                {
                }
            }

            _engineLocks.Clear();

            foreach (IOcrEngine engine
                     in _ocrEngines.Values)
            {
                IDisposable disposable =
                    engine as IDisposable;

                if (disposable == null)
                    continue;

                try
                {
                    disposable.Dispose();
                }
                catch
                {
                }
            }

            _ocrEngines.Clear();

            try
            {
                _comparisonLimiter.Dispose();
            }
            catch
            {
            }
        }
    }
}