using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.IO;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Tesseract;

namespace GameTranslatorUltimate
{
    public class TesseractOcrEngine : IOcrEngine
    {
        private readonly ILogger _logger;

        public OcrEngineType EngineType => OcrEngineType.Tesseract;

        private readonly AppSettings _appSettings;

        public TesseractOcrEngine(ILogger logger, AppSettings appSettings = null)
        {
            _logger = logger;
            _appSettings = appSettings;
        }

        public async Task<string> RecognizeTextAsync(Bitmap image, string language, PageSegMode psm = PageSegMode.Auto)
        {
            if (image == null)
            {
                _logger.LogWarning("Görsel verisi sağlanmadı, tanıma işlemi atlandı.");
                return string.Empty;
            }

            var adaptiveResult = await TryRecognizeWithStrategy(image, language, psm, Preprocess_AdaptiveThreshold);
            _logger.LogInformation($"[Tesseract-Adaptif] Sonuç: '{adaptiveResult.Text}', Güvenilirlik: {adaptiveResult.Confidence:P}");

            if (adaptiveResult.Confidence > 0.82)
            {
                return adaptiveResult.Text;
            }

            var optimalResult = await TryRecognizeWithStrategy(image, language, PageSegMode.Auto, Preprocess_OptimalThreshold);
            _logger.LogInformation($"[Tesseract-Optimal] Sonuç: '{optimalResult.Text}', Güvenilirlik: {optimalResult.Confidence:P}");

            string finalResult = adaptiveResult.Confidence > optimalResult.Confidence ? adaptiveResult.Text : optimalResult.Text;
            float finalConfidence = Math.Max(adaptiveResult.Confidence, optimalResult.Confidence);

            // Güven skoru aşırı düşükse halüsinasyonları (olmayan şeyleri okumayı) önlemek için boş döndür
            if (finalConfidence < 0.65)
            {
                _logger.LogWarning($"OCR metin buldu ({finalResult}) ancak güven skoru (%{finalConfidence * 100:F0}) çok düşük olduğu için metin çöpe atıldı.");
                return string.Empty;
            }

            return finalResult;
        }

        private async Task<(string Text, float Confidence)> TryRecognizeWithStrategy(Bitmap image, string language, PageSegMode psm, Func<Bitmap, Pix> preprocessStrategy)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var preprocessedPix = preprocessStrategy(image))
                    {
                        // Prefer absolute tessdata path from app base directory to avoid relative path issues
                        string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
                        string tessPath = Path.Combine(baseDir, "tessdata");
                        _logger?.LogInformation($"Tesseract attempt - process bitness: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
                        _logger?.LogInformation($"Trying tessdata path: {tessPath}");
                        if (!Directory.Exists(tessPath)) _logger?.LogWarning($"tessdata directory not found at: {tessPath}");

                        TesseractEngine engine = null;
                        Exception creationException = null;
                        try
                        {
                            engine = new TesseractEngine(tessPath, language, EngineMode.Default);
                        }
                        catch (Exception ex)
                        {
                            creationException = ex;
                            _logger?.LogError($"TesseractEngine creation failed for path '{tessPath}'. Will try relative './tessdata'.", ex);
                        }

                        if (engine == null)
                        {
                            try
                            {
                                engine = new TesseractEngine("./tessdata", language, EngineMode.Default);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError($"TesseractEngine creation failed for relative path './tessdata' as well.", ex);
                                // log earlier exception if present
                                if (creationException != null)
                                {
                                    _logger?.LogError("Earlier engine creation error:", creationException);
                                }
                                return (string.Empty, 0f);
                            }
                        }

                        using (engine)
                        {
                            engine.DefaultPageSegMode = psm;
                            engine.SetVariable("user_defined_dpi", "300");

                            // El yazısı modu ayarları
                            if (_appSettings?.EnableHandwritingMode == true)
                            {
                                ConfigureHandwritingMode(engine);
                            }

                            using (var page = engine.Process(preprocessedPix))
                            {
                                var text = page.GetText()?.Trim().Replace("\n", " ").Replace("  ", " ") ?? string.Empty;
                                var confidence = page.GetMeanConfidence();
                                return (text, confidence);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Be explicit about common native issues
                    if (ex is DllNotFoundException || ex is BadImageFormatException)
                    {
                        _logger?.LogError($"Native Tesseract library load failed (possible VC++ redistributable or bitness mismatch): {ex.Message}", ex);
                    }
                    else
                    {
                        _logger?.LogError($"Tesseract OCR stratejisi {preprocessStrategy.Method.Name} başarısız oldu.", ex);
                    }
                    return (string.Empty, 0f);
                }
            });
        }

        private Pix Preprocess_AdaptiveThreshold(Bitmap image)
        {
            try
            {
                using (var mat = BitmapConverter.ToMat(image))
                using (var gray = mat.CvtColor(ColorConversionCodes.BGR2GRAY))
                using (var thresholded = new Mat())
                {
                    Cv2.AdaptiveThreshold(gray, thresholded, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 11, 2);
                    return PixConverter.ToPix(BitmapConverter.ToBitmap(thresholded));
                }
            }
            catch
            {
                return PixConverter.ToPix(image);
            }
        }

        private Pix Preprocess_OptimalThreshold(Bitmap image)
        {
            try
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
            catch
            {
                return PixConverter.ToPix(image);
            }
        }

        private int FindOptimalThreshold(Bitmap image)
        {
            try
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
            catch
            {
                return -1;
            }
        }
        /// Tesseract'ı el yazısı tanıma için yapılandırır
        private void ConfigureHandwritingMode(TesseractEngine engine)
        {
            try
            {
                // El yazısı için optimize edilmiş ayarlar
                engine.SetVariable("tessedit_char_whitelist", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,!?;:()[]{}\"'");
                engine.SetVariable("classify_bln_numeric_mode", "0");
                engine.SetVariable("textord_min_linesize", "2.5");
                engine.SetVariable("textord_old_baselines", "1");
                engine.SetVariable("textord_old_xheight", "1");
                engine.SetVariable("textord_min_xheight", "8");
                engine.SetVariable("textord_force_make_prop_words", "F");
                engine.SetVariable("tessedit_enable_doc_dict", "0");
                engine.SetVariable("load_system_dawg", "0");
                engine.SetVariable("load_freq_dawg", "0");
                engine.SetVariable("load_punc_dawg", "0");
                engine.SetVariable("load_number_dawg", "0");
                engine.SetVariable("load_unambig_dawg", "0");
                engine.SetVariable("load_bigram_dawg", "0");
                engine.SetVariable("load_fixed_length_dawgs", "0");

                _logger?.LogInformation("El yazısı modu etkinleştirildi");
            }
            catch (Exception ex)
            {
                _logger?.LogError("El yazısı modu yapılandırılırken hata oluştu", ex);
            }
        }
    }
}
