using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public class RegionProcessResult
    {
        public Rectangle Region { get; set; }
        public string RecognizedText { get; set; }
        public string TranslatedText { get; set; }
        public DateTime ProcessedAt { get; set; }
    }

    public class OcrRegionProcessor : IDisposable
    {
        private readonly IOcrService _ocrService;
        private readonly ITranslationService _translationService;
        private readonly string _ocrLanguage;
        private readonly string _targetLanguage;
        private readonly double _changeThreshold;
        private readonly int _mergeTolerance;
        private Bitmap _previousImage;
        private bool _disposed = false;

        public OcrRegionProcessor(IOcrService ocrService, ITranslationService translationService, string ocrLanguage, string targetLanguage, double changeThreshold = 0.01, int mergeTolerance = 15)
        {
            _ocrService = ocrService ?? throw new ArgumentNullException(nameof(ocrService));
            _translationService = translationService ?? throw new ArgumentNullException(nameof(translationService));
            _ocrLanguage = ocrLanguage;
            _targetLanguage = targetLanguage;
            _changeThreshold = changeThreshold;
            _mergeTolerance = mergeTolerance;
        }

        public async Task<List<RegionProcessResult>> ProcessChangedRegionsAsync(Bitmap currentImage)
        {
            var results = new List<RegionProcessResult>();

            if (currentImage == null)
                return results;

            if (_previousImage == null)
            {
                _previousImage = new Bitmap(currentImage);
                return results;
            }

            try
            {
                // Değişen bölgeleri filtrele
                var changedRegions = _ocrService
                    .FindTextRegions(currentImage)
                    .Where(r => IsRegionChanged(_previousImage, currentImage, r))
                    .ToList();

                if (!changedRegions.Any())
                {
                    _previousImage?.Dispose();
                    _previousImage = new Bitmap(currentImage);
                    return results;
                }

                // Bitişik bölgeleri birleştir
                var mergedRegions = MergeAdjacentRegions(changedRegions, _mergeTolerance);

                var tasks = mergedRegions.Select(async region =>
                {
                    try
                    {
                        using (var regionBmp = _ocrService.CropImage(currentImage, region))
                        {
                            string recognized = await _ocrService.GetTextAdaptiveAsync(regionBmp, _ocrLanguage);
                            if (!string.IsNullOrWhiteSpace(recognized))
                            {
                                string translated = await _translationService.TranslateAsync(recognized, _targetLanguage, null);

                                var result = new RegionProcessResult
                                {
                                    Region = region,
                                    RecognizedText = recognized,
                                    TranslatedText = translated,
                                    ProcessedAt = DateTime.Now
                                };

                                lock (results)
                                {
                                    results.Add(result);
                                }

                                OnOcrRegionProcessed(region, recognized, translated);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        OnOcrRegionProcessError(region, ex);
                    }
                }).ToList();

                await Task.WhenAll(tasks);

                _previousImage?.Dispose();
                _previousImage = new Bitmap(currentImage);
            }
            catch (Exception ex)
            {
                OnOcrRegionProcessError(Rectangle.Empty, ex);
            }

            return results;
        }

        private List<Rectangle> MergeAdjacentRegions(List<Rectangle> regions, int mergeTolerance)
        {
            if (regions.Count <= 1)
                return regions;

            bool merged;
            do
            {
                merged = false;
                var mergedRegions = new List<Rectangle>();
                var used = new bool[regions.Count];

                for (int i = 0; i < regions.Count; i++)
                {
                    if (used[i]) continue;

                    var currentRect = regions[i];
                    for (int j = i + 1; j < regions.Count; j++)
                    {
                        if (used[j]) continue;

                        var inflatedRect = currentRect;
                        inflatedRect.Inflate(mergeTolerance, mergeTolerance);

                        if (inflatedRect.IntersectsWith(regions[j]))
                        {
                            currentRect = Rectangle.Union(currentRect, regions[j]);
                            used[j] = true;
                            merged = true;
                        }
                    }
                    mergedRegions.Add(currentRect);
                }
                regions = mergedRegions;

            } while (merged);

            return regions;
        }

        private bool IsRegionChanged(Bitmap prev, Bitmap curr, Rectangle region)
        {
            try
            {
                using (var prevRoi = prev.Clone(region, prev.PixelFormat))
                using (var currRoi = curr.Clone(region, curr.PixelFormat))
                using (var prevMat = BitmapConverter.ToMat(prevRoi))
                using (var currMat = BitmapConverter.ToMat(currRoi))
                using (var diff = new Mat())
                {
                    Cv2.Absdiff(prevMat, currMat, diff);
                    if (diff.Channels() > 1)
                        Cv2.CvtColor(diff, diff, ColorConversionCodes.BGR2GRAY);
                    return Cv2.CountNonZero(diff) > (region.Width * region.Height * _changeThreshold);
                }
            }
            catch
            {
                // If comparison fails (e.g. OpenCV missing), assume region changed so we process it
                return true;
            }
        }

        protected virtual void OnOcrRegionProcessed(Rectangle region, string recognizedText, string translatedText)
        {
            if (!string.IsNullOrWhiteSpace(recognizedText) && !string.IsNullOrWhiteSpace(translatedText))
            {
                Console.WriteLine($"[Bölge: {region}] \"{recognizedText}\" → \"{translatedText}\"");
            }
            else if (!string.IsNullOrWhiteSpace(recognizedText))
            {
                Console.WriteLine($"[Bölge: {region}] \"{recognizedText}\" → (çevrilemedi)");
            }
        }

        protected virtual void OnOcrRegionProcessError(Rectangle region, Exception exception)
        {
            Console.WriteLine($"[Hata - Bölge: {region}] {exception.Message}");
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
                    _previousImage?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}
