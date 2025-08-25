using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace P5S_ceviri
{
    /// <summary>
    /// Görüntüler arasındaki değişen metin bölgelerini tespit eder, işler ve çevirir.
    /// Bitişik bölgeleri birleştirerek performansı ve doğruluğu artırır.
    /// </summary>
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

        /// <summary>
        /// OcrRegionProcessor sınıfının yeni bir örneğini başlatır.
        /// </summary>
        /// <param name="ocrService">OCR işlemleri için kullanılacak servis.</param>
        /// <param name="translationService">Çeviri işlemleri için kullanılacak servis.</param>
        /// <param name="ocrLanguage">OCR için kaynak dil.</param>
        /// <param name="targetLanguage">Çeviri için hedef dil.</param>
        /// <param name="changeThreshold">Bir bölgenin değişmiş sayılması için gereken piksel farkı oranı.</param>
        /// <param name="mergeTolerance">Bitişik bölgelerin birleştirilmesi için piksel cinsinden tolerans.</param>
        public OcrRegionProcessor(IOcrService ocrService, ITranslationService translationService, string ocrLanguage, string targetLanguage, double changeThreshold = 0.01, int mergeTolerance = 15)
        {
            _ocrService = ocrService ?? throw new ArgumentNullException(nameof(ocrService));
            _translationService = translationService ?? throw new ArgumentNullException(nameof(translationService));
            _ocrLanguage = ocrLanguage;
            _targetLanguage = targetLanguage;
            _changeThreshold = changeThreshold;
            _mergeTolerance = mergeTolerance;
        }

        /// <summary>
        /// Mevcut görüntüyü önceki görüntüyle karşılaştırır, değişen ve birleştirilmiş metin bölgelerini işler.
        /// </summary>
        /// <param name="currentImage">İşlenecek mevcut görüntü.</param>
        public async Task ProcessChangedRegionsAsync(Bitmap currentImage)
        {
            if (currentImage == null) return;

            if (_previousImage == null)
            {
                _previousImage = new Bitmap(currentImage);
                return;
            }

            // Değişen bölgeleri filtreleme
            var changedRegions = _ocrService
                .FindTextRegions(currentImage)
                .Where(r => IsRegionChanged(_previousImage, currentImage, r))
                .ToList();

            // Bitişik bölgeleri birleştir
            var mergedRegions = MergeAdjacentRegions(changedRegions, _mergeTolerance);

            var tasks = mergedRegions.Select(async region =>
            {
                using (var regionBmp = _ocrService.CropImage(currentImage, region))
                {
                    string recognized = await _ocrService.GetTextAdaptiveAsync(regionBmp, _ocrLanguage);
                    if (!string.IsNullOrWhiteSpace(recognized))
                    {
                        string translated = await _translationService.TranslateAsync(recognized, _targetLanguage);
                        OnOcrRegionProcessed(region, recognized, translated);
                    }
                }
            });

            await Task.WhenAll(tasks);

            _previousImage?.Dispose();
            _previousImage = new Bitmap(currentImage);
        }

        /// <summary>
        /// Verilen dikdörtgen listesindeki bitişik veya örtüşen bölgeleri birleştirir.
        /// Birleştirme, bölgeler arasındaki mesafe `mergeTolerance` değerinden azsa gerçekleşir.
        /// </summary>
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
        protected virtual void OnOcrRegionProcessed(Rectangle region, string recognizedText, string translatedText)
        {
            Console.WriteLine($"[Bölge: {region}] “{recognizedText}” → “{translatedText}”");
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