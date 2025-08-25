using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Tesseract;

namespace P5S_ceviri
{
    public class TesseractOcrEngine : IOcrEngine
    {
        private readonly ILogger _logger;

        public OcrEngineType EngineType => OcrEngineType.Tesseract;

        public TesseractOcrEngine(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<string> RecognizeTextAsync(Bitmap image, string language)
        {
            if (image == null) return string.Empty;

            var adaptiveResult = await TryRecognizeWithStrategy(image, language, PageSegMode.Auto, Preprocess_AdaptiveThreshold);
            _logger.LogInformation($"[Tesseract-Adaptif] Sonuç: '{adaptiveResult.Text}', Güvenilirlik: {adaptiveResult.Confidence:P}");

            if (adaptiveResult.Confidence > 0.70)
            {
                return adaptiveResult.Text;
            }

            var optimalResult = await TryRecognizeWithStrategy(image, language, PageSegMode.Auto, Preprocess_OptimalThreshold);
            _logger.LogInformation($"[Tesseract-Optimal] Sonuç: '{optimalResult.Text}', Güvenilirlik: {optimalResult.Confidence:P}");

            return adaptiveResult.Confidence > optimalResult.Confidence ? adaptiveResult.Text : optimalResult.Text;
        }

        private async Task<(string Text, float Confidence)> TryRecognizeWithStrategy(Bitmap image, string language, PageSegMode psm, Func<Bitmap, Pix> preprocessStrategy)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var preprocessedPix = preprocessStrategy(image))
                    using (var engine = new TesseractEngine(@"./tessdata", language, EngineMode.Default))
                    {
                        engine.DefaultPageSegMode = psm;
                        engine.SetVariable("user_defined_dpi", "300");

                        using (var page = engine.Process(preprocessedPix))
                        {
                            var text = page.GetText()?.Trim().Replace("\n", " ").Replace("  ", " ") ?? string.Empty;
                            var confidence = page.GetMeanConfidence();
                            return (text, confidence);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Tesseract OCR stratejisi {preprocessStrategy.Method.Name} başarısız oldu.", ex);
                    return (string.Empty, 0f);
                }
            });
        }

        private Pix Preprocess_AdaptiveThreshold(Bitmap image)
        {
            using (var mat = BitmapConverter.ToMat(image))
            using (var gray = mat.CvtColor(ColorConversionCodes.BGR2GRAY))
            using (var thresholded = new Mat())
            {
                Cv2.AdaptiveThreshold(gray, thresholded, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 11, 2);
                return PixConverter.ToPix(BitmapConverter.ToBitmap(thresholded));
            }
        }

        private Pix Preprocess_OptimalThreshold(Bitmap image)
        {
            int optimalThreshold = FindOptimalThreshold(image);
            if (optimalThreshold == -1) optimalThreshold = 128;

            using (var mat = BitmapConverter.ToMat(image))
            using (var gray = mat.CvtColor(ColorConversionCodes.BGR2GRAY))
            using (var blurred = new Mat())
            using (var thresholded = new Mat())
            {
                Cv2.MedianBlur(gray, blurred, 3);
                Cv2.Threshold(blurred, thresholded, optimalThreshold, 255, ThresholdTypes.Binary);
                return PixConverter.ToPix(BitmapConverter.ToBitmap(thresholded));
            }
        }

        private int FindOptimalThreshold(Bitmap image)
        {
            if (image == null) return -1;
            using (var mat = BitmapConverter.ToMat(image))
            using (var grayMat = mat.CvtColor(ColorConversionCodes.BGR2GRAY))
            {
                return Enumerable.Range(8, 11)
                                 .Select(i => i * 10)
                                 .Select(threshold =>
                                 {
                                     using (var binary = grayMat.Threshold(threshold, 255, ThresholdTypes.Binary))
                                     using (var laplacian = binary.Laplacian(MatType.CV_64F))
                                     {
                                         Cv2.MeanStdDev(laplacian, out _, out Scalar stddev);
                                         return new { Threshold = threshold, Variance = stddev.Val0 * stddev.Val0 };
                                     }
                                 })
                                 .OrderByDescending(x => x.Variance)
                                 .FirstOrDefault()?.Threshold ?? -1;
            }
        }
    }
}