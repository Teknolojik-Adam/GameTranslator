using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public sealed class RegionProcessResult
    {
        public Rectangle Region { get; set; }
        public string RecognizedText { get; set; } = string.Empty;
        public string TranslatedText { get; set; } = string.Empty;
        public double Score { get; set; }
        public DateTime ProcessedAt { get; set; }
    }

    public sealed class OcrRegionResult
    {
        public Rectangle Region { get; set; }
        public string RecognizedText { get; set; } = string.Empty;
        public double Score { get; set; }
    }

    public class OcrRegionProcessor : IDisposable
    {
        private readonly IOcrService _ocrService;
        private readonly ITranslationService _translationService;
        private readonly AppSettings _appSettings;

        private readonly string _fallbackOcrLanguage;
        private readonly string _fallbackTargetLanguage;

        private readonly double _changeThreshold;
        private readonly int _mergeTolerance;

        private readonly SemaphoreSlim _processLock =
            new SemaphoreSlim(1, 1);

        private Bitmap _previousImage;

        private int _disposed;

        public OcrRegionProcessor(
            IOcrService ocrService,
            ITranslationService translationService,
            AppSettings appSettings,
            double changeThreshold = 0.01,
            int mergeTolerance = 15)
        {
            _ocrService =
                ocrService ??
                throw new ArgumentNullException(nameof(ocrService));

            _translationService =
                translationService ??
                throw new ArgumentNullException(nameof(translationService));

            _appSettings =
                appSettings ??
                throw new ArgumentNullException(nameof(appSettings));

            _fallbackOcrLanguage =
                null;

            _fallbackTargetLanguage =
                null;

            _changeThreshold =
                NormalizeChangeThreshold(
                    changeThreshold);

            _mergeTolerance =
                Math.Max(
                    0,
                    mergeTolerance);
        }

        public OcrRegionProcessor(
            IOcrService ocrService,
            ITranslationService translationService,
            string ocrLanguage,
            string targetLanguage,
            double changeThreshold = 0.01,
            int mergeTolerance = 15)
        {
            _ocrService =
                ocrService ??
                throw new ArgumentNullException(nameof(ocrService));

            _translationService =
                translationService ??
                throw new ArgumentNullException(nameof(translationService));

            _fallbackOcrLanguage =
                ocrLanguage;

            _fallbackTargetLanguage =
                targetLanguage;

            try
            {
                _appSettings =
                    ServiceContainer.GetService<AppSettings>();
            }
            catch
            {
                _appSettings = null;
            }

            _changeThreshold =
                NormalizeChangeThreshold(
                    changeThreshold);

            _mergeTolerance =
                Math.Max(
                    0,
                    mergeTolerance);
        }

        public Task<List<RegionProcessResult>> ProcessChangedRegionsAsync(Bitmap currentImage)
        {
            return ProcessChangedRegionsAsync(currentImage, null);
        }

        public async Task<List<RegionProcessResult>> ProcessChangedRegionsAsync(
            Bitmap currentImage,
            Rectangle? manualOcrRegion)
        {
            ThrowIfDisposed();

            var results =
                new List<RegionProcessResult>();

            if (currentImage == null)
                return results;

            await _processLock
                .WaitAsync()
                .ConfigureAwait(false);

            try
            {
                ThrowIfDisposed();

                if (_previousImage == null ||
                    _previousImage.Width != currentImage.Width ||
                    _previousImage.Height != currentImage.Height)
                {
                    ReplacePreviousImage(
                        currentImage);

                    return results;
                }

                List<Rectangle> detectedRegions =
                    _ocrService.FindTextRegions(
                        currentImage);

                bool isFullScreenFallback = IsFullScreenRegion(detectedRegions, currentImage.Width, currentImage.Height);

                if (detectedRegions == null ||
                    detectedRegions.Count == 0)
                {
                    ReplacePreviousImage(
                        currentImage);

                    return results;
                }

                if (manualOcrRegion.HasValue)
                {
                    Rectangle manual = ClampRegion(manualOcrRegion.Value, currentImage.Width, currentImage.Height);
                    if (manual.Width > 0 && manual.Height > 0)
                    {
                        var filtered = new List<Rectangle>();
                        foreach (var r in detectedRegions)
                        {
                            double ratio = RegionScorer.IntersectionRatio(r, manual);
                            if (ratio > 0.05)
                                filtered.Add(r);
                        }
                        if (filtered.Count > 0)
                        {
                            detectedRegions = filtered;
                            isFullScreenFallback = false;
                        }
                        else
                        {
                            detectedRegions = new List<Rectangle> { manual };
                            isFullScreenFallback = false;
                        }
                    }
                }
                else if (isFullScreenFallback)
                {
                    ReplacePreviousImage(currentImage);
                    return results;
                }

                var changedRegions =
                    new List<Rectangle>();

                foreach (Rectangle region in detectedRegions)
                {
                    Rectangle safeRegion =
                        ClampRegion(
                            region,
                            currentImage.Width,
                            currentImage.Height);

                    if (safeRegion.Width <= 0 ||
                        safeRegion.Height <= 0)
                    {
                        continue;
                    }

                    if (isFullScreenFallback)
                    {
                        changedRegions.Add(safeRegion);
                    }
                    else if (IsRegionChanged(
                        _previousImage,
                        currentImage,
                        safeRegion))
                    {
                        changedRegions.Add(
                            safeRegion);
                    }
                }

                if (changedRegions.Count == 0)
                {
                    ReplacePreviousImage(
                        currentImage);

                    return results;
                }

                List<Rectangle> mergedRegions =
                    MergeAdjacentRegions(
                        changedRegions,
                        _mergeTolerance);

                mergedRegions =
                    mergedRegions
                        .OrderBy(
                            region =>
                                region.Top)
                        .ThenBy(
                            region =>
                                region.Left)
                        .ToList();

                string ocrLanguage =
                    GetCurrentOcrLanguage();

                if (string.IsNullOrWhiteSpace(
                    ocrLanguage))
                {
                    ocrLanguage = "eng";
                }

                var scoredResults = new List<RegionProcessResult>();

                foreach (Rectangle region in mergedRegions)
                {
                    try
                    {
                        using (Bitmap regionBitmap =
                               _ocrService.CropImage(
                                   currentImage,
                                   region))
                        {
                            if (regionBitmap == null)
                                continue;

                            string recognized =
                                await _ocrService
                                    .GetTextAdaptiveAsync(
                                        regionBitmap,
                                        ocrLanguage)
                                    .ConfigureAwait(false);

                            if (string.IsNullOrWhiteSpace(
                                recognized))
                            {
                                continue;
                            }

                            recognized =
                                recognized.Trim();

                            if (recognized.Length < 2)
                                continue;

                            if (recognized.Length > 500)
                                recognized = recognized.Substring(0, 500).Trim();

                            double score = RegionScorer.ScoreRegion(region, recognized, currentImage.Width, currentImage.Height, manualOcrRegion);
                            RegionScorer.RegisterTextSeen(recognized);

                            var result =
                                new RegionProcessResult
                                {
                                    Region =
                                        region,

                                    RecognizedText =
                                        recognized,

                                    TranslatedText =
                                        string.Empty,

                                    Score =
                                        score,

                                    ProcessedAt =
                                        DateTime.Now
                                };

                            scoredResults.Add(result);

                            OnOcrRegionProcessed(
                                region,
                                recognized,
                                score);
                        }
                    }
                    catch (Exception ex)
                    {
                        OnOcrRegionProcessError(
                            region,
                            ex);
                    }
                }

                if (scoredResults.Count == 0)
                {
                    ReplacePreviousImage(currentImage);
                    return results;
                }

                var ranked = RegionScorer.RankAndFilter(scoredResults);

                if (ranked.Count == 0)
                {
                    var best = scoredResults.OrderByDescending(x => x.Score).FirstOrDefault();
                    if (best != null && best.Score > -80)
                        ranked.Add(best);
                }

                foreach (var r in ranked)
                {
                    Console.WriteLine($"[OCR Rank] Region={r.Region} Score={r.Score:F1} Text=\"{r.RecognizedText.Substring(0, Math.Min(60, r.RecognizedText.Length))}\" {(ranked.Contains(r) ? "SELECTED" : "REJECTED")}");
                }

                ReplacePreviousImage(
                    currentImage);

                return ranked;
            }
            catch (Exception ex)
            {
                OnOcrRegionProcessError(
                    Rectangle.Empty,
                    ex);

                ReplacePreviousImage(
                    currentImage);

                return results;
            }
            finally
            {
                _processLock.Release();
            }
        }

        private static bool IsFullScreenRegion(List<Rectangle> regions, int w, int h)
        {
            if (regions == null || regions.Count != 1)
                return false;
            var r = regions[0];
            return r.X == 0 && r.Y == 0 && r.Width == w && r.Height == h;
        }

        private string GetCurrentOcrLanguage()
        {
            if (_appSettings != null)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(
                        _appSettings.OcrLanguage))
                    {
                        return _appSettings.OcrLanguage
                            .Trim();
                    }
                }
                catch
                {
                }
            }

            return string.IsNullOrWhiteSpace(
                    _fallbackOcrLanguage)
                ? "eng"
                : _fallbackOcrLanguage.Trim();
        }

        private string GetCurrentTargetLanguage()
        {
            if (_appSettings != null)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(
                        _appSettings.TargetLanguage))
                    {
                        return _appSettings.TargetLanguage
                            .Trim();
                    }
                }
                catch
                {
                }
            }

            return string.IsNullOrWhiteSpace(
                    _fallbackTargetLanguage)
                ? string.Empty
                : _fallbackTargetLanguage.Trim();
        }

        private List<Rectangle> MergeAdjacentRegions(
            List<Rectangle> regions,
            int mergeTolerance)
        {
            if (regions == null ||
                regions.Count == 0)
            {
                return new List<Rectangle>();
            }

            if (regions.Count == 1)
            {
                return new List<Rectangle>(
                    regions);
            }

            var working =
                new List<Rectangle>(
                    regions);

            bool changed;

            do
            {
                changed = false;

                var result =
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

                            Rectangle comparison =
                                current;

                            comparison.Inflate(
                                mergeTolerance,
                                mergeTolerance);

                            if (!comparison.IntersectsWith(
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

                    result.Add(
                        current);
                }

                working =
                    result;
            }
            while (changed);

            return working;
        }

        private bool IsRegionChanged(
            Bitmap previous,
            Bitmap current,
            Rectangle region)
        {
            if (previous == null ||
                current == null)
            {
                return true;
            }

            Rectangle safeRegion =
                Rectangle.Intersect(
                    new Rectangle(
                        0,
                        0,
                        Math.Min(
                            previous.Width,
                            current.Width),
                        Math.Min(
                            previous.Height,
                            current.Height)),
                    region);

            if (safeRegion.Width <= 0 ||
                safeRegion.Height <= 0)
            {
                return true;
            }

            try
            {
                using (Bitmap previousRoi =
                       previous.Clone(
                           safeRegion,
                           previous.PixelFormat))
                using (Bitmap currentRoi =
                       current.Clone(
                           safeRegion,
                           current.PixelFormat))
                using (Mat previousMat =
                       BitmapConverter.ToMat(
                           previousRoi))
                using (Mat currentMat =
                       BitmapConverter.ToMat(
                           currentRoi))
                using (Mat previousGray =
                       ConvertToGray(
                           previousMat))
                using (Mat currentGray =
                       ConvertToGray(
                           currentMat))
                using (Mat diff =
                       new Mat())
                {
                    Cv2.Absdiff(
                        previousGray,
                        currentGray,
                        diff);

                    Cv2.Threshold(
                        diff,
                        diff,
                        15,
                        255,
                        ThresholdTypes.Binary);

                    int changedPixels =
                        Cv2.CountNonZero(
                            diff);

                    long totalPixels =
                        (long)safeRegion.Width *
                        safeRegion.Height;

                    if (totalPixels <= 0)
                        return true;

                    double changeRatio =
                        (double)changedPixels /
                        totalPixels;

                    return changeRatio >=
                           _changeThreshold;
                }
            }
            catch
            {
                return true;
            }
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

        private static double NormalizeChangeThreshold(
            double threshold)
        {
            if (double.IsNaN(threshold) ||
                double.IsInfinity(threshold))
            {
                return 0.01;
            }

            if (threshold < 0.001)
                return 0.001;

            if (threshold > 1.0)
                return 1.0;

            return threshold;
        }

        private void ReplacePreviousImage(
            Bitmap image)
        {
            Bitmap newImage =
                image != null
                    ? new Bitmap(image)
                    : null;

            Bitmap oldImage =
                _previousImage;

            _previousImage =
                newImage;

            if (oldImage != null)
            {
                oldImage.Dispose();
            }
        }

        protected void OnOcrRegionProcessed(
            Rectangle region,
            string recognizedText,
            double score)
        {
            if (!string.IsNullOrWhiteSpace(recognizedText))
            {
                Console.WriteLine($"[Bölge: {region} Score:{score:F1}] \"{recognizedText}\"");
            }
        }

        protected virtual void OnOcrRegionProcessed(
            Rectangle region,
            string recognizedText,
            string translatedText)
        {
            if (!string.IsNullOrWhiteSpace(
                    recognizedText) &&
                !string.IsNullOrWhiteSpace(
                    translatedText))
            {
                Console.WriteLine(
                    $"[Bölge: {region}] \"{recognizedText}\" → \"{translatedText}\"");
            }
            else if (!string.IsNullOrWhiteSpace(
                         recognizedText))
            {
                Console.WriteLine(
                    $"[Bölge: {region}] \"{recognizedText}\" → (çevrilemedi)");
            }
        }

        protected virtual void OnOcrRegionProcessError(
            Rectangle region,
            Exception exception)
        {
            Console.WriteLine(
                $"[Hata - Bölge: {region}] {exception?.Message}");
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(
                    ref _disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(OcrRegionProcessor));
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

            Bitmap previous =
                _previousImage;

            _previousImage =
                null;

            if (previous != null)
            {
                previous.Dispose();
            }

            try
            {
                _processLock.Dispose();
            }
            catch
            {
            }
        }
    }
}
