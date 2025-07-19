using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace P5S_ceviri
{
    public class OcrRegionProcessor : IDisposable
    {
        private readonly IOcrService _ocrService;
        private readonly ITranslationService _translationService;
        private Bitmap _previousImage;

        public OcrRegionProcessor(IOcrService ocrService, ITranslationService translationService)
        {
            _ocrService = ocrService ?? throw new ArgumentNullException(nameof(ocrService));
            _translationService = translationService ?? throw new ArgumentNullException(nameof(translationService));
        }

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

            // Her bölge için paralel OCR ile çeviri yapmak için
            var tasks = changedRegions.Select(async region =>
            {
                var regionBmp = _ocrService.CropImage(currentImage, region);
                string recognized = await _ocrService.GetTextAdaptiveAsync(regionBmp, "eng");
                if (!string.IsNullOrWhiteSpace(recognized))
                {
                    string translated = await _translationService.TranslateAsync(recognized, "tr");
                    OnOcrRegionProcessed(region, recognized, translated);
                }
            });

            await Task.WhenAll(tasks);

            _previousImage.Dispose();
            _previousImage = new Bitmap(currentImage);
        }

        private bool IsRegionChanged(Bitmap prev, Bitmap curr, Rectangle region)
        {
            var prevRoi = prev.Clone(region, prev.PixelFormat);
            var currRoi = curr.Clone(region, curr.PixelFormat);
            var prevMat = BitmapConverter.ToMat(prevRoi);
            var currMat = BitmapConverter.ToMat(currRoi);
            var diff = new Mat();
            Cv2.Absdiff(prevMat, currMat, diff);
            if (diff.Channels() > 1)
                Cv2.CvtColor(diff, diff, ColorConversionCodes.BGR2GRAY);
            return Cv2.CountNonZero(diff) > (region.Width * region.Height * 0.01);
        }

        protected virtual void OnOcrRegionProcessed(Rectangle region, string recognizedText, string translatedText)
        {
            Console.WriteLine($"[Bölge: {region}] “{recognizedText}” → “{translatedText}”");
        }

        public void Dispose()
        {
            _previousImage?.Dispose();
        }
    }
}