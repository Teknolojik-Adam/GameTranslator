using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Drawing;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public class OcrRegionProcessor
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

            var regions = _ocrService.FindTextRegions(currentImage);

            foreach (var region in regions)
            {
                if (IsRegionChanged(_previousImage, currentImage, region))
                {
                    using (var regionImage = _ocrService.CropImage(currentImage, region))
                    {
                        string recognizedText = await _ocrService.GetTextAdaptiveAsync(regionImage, "eng");

                        if (!string.IsNullOrWhiteSpace(recognizedText))
                        {
                            string translatedText = await _translationService.TranslateAsync(recognizedText, "tr");
                            OnOcrRegionProcessed(region, recognizedText, translatedText);
                        }
                    }
                }
            }

            _previousImage?.Dispose();
            _previousImage = new Bitmap(currentImage);
        }

        private bool IsRegionChanged(Bitmap previous, Bitmap current, Rectangle region)
        {
            using (var prevRoi = previous.Clone(region, previous.PixelFormat))
            using (var currentRoi = current.Clone(region, current.PixelFormat))
            using (var prevMat = BitmapConverter.ToMat(prevRoi))
            using (var currentMat = BitmapConverter.ToMat(currentRoi))
            using (var diff = new Mat())
            {
                Cv2.Absdiff(prevMat, currentMat, diff);

                if (diff.Channels() > 1)
                {
                    Cv2.CvtColor(diff, diff, ColorConversionCodes.BGR2GRAY);
                }

                return Cv2.CountNonZero(diff) > (region.Width * region.Height * 0.01);
            }
        }

        protected virtual void OnOcrRegionProcessed(Rectangle region, string recognizedText, string translatedText)
        {
            Console.WriteLine($"[Bölge Tespiti] Bölge: {region}");
            Console.WriteLine($"[Tanınan Metin] {recognizedText}");
            Console.WriteLine($"[Çevrilmiş Metin] {translatedText}");
        }

        public void Dispose()
        {
            _previousImage?.Dispose();
            _previousImage = null;
        }
    }
}