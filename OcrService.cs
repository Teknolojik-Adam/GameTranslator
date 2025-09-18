using OpenCvSharp;
using OpenCvSharp.Dnn;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Tesseract;
using CvPoint = OpenCvSharp.Point; // OpenCV Point'ı için alias

namespace P5S_ceviri
{
    public class OcrService : IOcrService
    {
        #region Win32 Imports and Constants
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        #endregion

        private readonly ILogger _logger;
        private readonly AppSettings _appSettings;
        private readonly Dictionary<OcrEngineType, IOcrEngine> _ocrEngines;
        private readonly Net _eastNet;
        private const string EastModelPath = "frozen_east_text_detection.pb";

        public OcrService(ILogger logger, AppSettings appSettings)
        {
            _logger = logger;
            _appSettings = appSettings;
            _ocrEngines = new Dictionary<OcrEngineType, IOcrEngine>
            {
                { OcrEngineType.Tesseract, new TesseractOcrEngine(logger, appSettings) },
                { OcrEngineType.WindowsOcr, new WindowsOcrEngine(logger) }
            };

            if (File.Exists(EastModelPath))
            {
                _eastNet = CvDnn.ReadNet(EastModelPath);
                _logger.LogInformation("EAST metin algılama modeli başarıyla yüklendi.");
            }
            else
            {
                _logger.LogError($"EAST model bulunamadı: {Path.GetFullPath(EastModelPath)}. Metin algılama yapılamayacak.");
                _eastNet = null;
            }
        }

        public async Task<string> GetTextFromImage(Bitmap image, string language, bool invertColors = false)
        {
            // Ön işleme: Süper çözünürlük ve skew correction
            using (var processedImage = PreprocessImageForOcr(image))
            {
                return await GetTextAdaptiveAsync(processedImage, language);
            }
        }

        public async Task<string> GetTextAdaptiveAsync(Bitmap image, string language, PageSegMode psm = PageSegMode.Auto)
        {
            if (image == null) return string.Empty;
            if (_ocrEngines.TryGetValue(_appSettings.OcrEngine, out var engine))
            {
                return await engine.RecognizeTextAsync(image, language);
            }
            _logger.LogError($"Seçilen OCR motoru '{_appSettings.OcrEngine}' bulunamadı.");
            return string.Empty;
        }

        public async Task<string> RecognizeTextInRegionsAsync(Bitmap image, string language, PageSegMode psm = PageSegMode.Auto)
        {
            if (image == null) return string.Empty;

            // Metin algılama yöntemine göre optimizasyon
            if (_appSettings.TextDetectionMethod == TextDetectionMethod.None)
            {
                // Tam ekran modunda doğrudan OCR yap
                return await GetTextAdaptiveAsync(image, language, psm);
            }

            var regions = FindTextRegions(image);
            if (!regions.Any())
            {
                _logger.LogWarning("Metin bölgesi algılanamadı. Tam görüntü taranıyor.");
                return await GetTextAdaptiveAsync(image, language, psm);
            }

            // Paralel bölge işleme
            var tasks = regions.Select(region => RecognizeTextInSingleRegionAsync(image, region, language, psm));
            var recognizedTexts = await Task.WhenAll(tasks);

            return string.Join(" ", recognizedTexts.Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        private async Task<string> RecognizeTextInSingleRegionAsync(Bitmap sourceImage, System.Drawing.Rectangle region, string language, PageSegMode psm)
        {
            using (var regionImage = CropImage(sourceImage, region))
            {
                return await GetTextAdaptiveAsync(regionImage, language, psm);
            }
        }

        public List<System.Drawing.Rectangle> FindTextRegions(Bitmap sourceImage)
        {
            // Kullanıcı tercihi yoksa tam ekran tara
            if (_appSettings.TextDetectionMethod == TextDetectionMethod.None)
            {
                return new List<System.Drawing.Rectangle> { new System.Drawing.Rectangle(0, 0, sourceImage.Width, sourceImage.Height) };
            }

            // EAST modeli kullanılacaksa
            if (_appSettings.TextDetectionMethod == TextDetectionMethod.East)
            {
                if (_eastNet == null || sourceImage == null)
                {
                    _logger?.LogWarning("EAST modeli yüklenmedi, tam ekran taranacak.");
                    return new List<System.Drawing.Rectangle> { new System.Drawing.Rectangle(0, 0, sourceImage.Width, sourceImage.Height) };
                }

                return FindTextRegionsWithEast(sourceImage);
            }

            // OpenCV ile genel metin algılama
            if (_appSettings.TextDetectionMethod == TextDetectionMethod.OpenCV)
            {
                return FindTextRegionsWithOpenCV(sourceImage);
            }

            return new List<System.Drawing.Rectangle> { new System.Drawing.Rectangle(0, 0, sourceImage.Width, sourceImage.Height) };
        }

        private List<System.Drawing.Rectangle> FindTextRegionsWithEast(Bitmap sourceImage)
        {
            using (Mat src = BitmapConverter.ToMat(sourceImage))
            {
                int newW = (int)(src.Width / 32.0) * 32;
                int newH = (int)(src.Height / 32.0) * 32;
                if (newW <= 0 || newH <= 0)
                {
                    _logger?.LogWarning($"Resim boyutu ({src.Width}x{src.Height}) EAST modeli için çok küçük.");
                    return new List<System.Drawing.Rectangle>();
                }
                double rW = (double)src.Width / newW;
                double rH = (double)src.Height / newH;
                using (Mat blob = CvDnn.BlobFromImage(src, 1.0, new OpenCvSharp.Size(newW, newH), new Scalar(123.68, 116.78, 103.94), true, false))
                {
                    _eastNet.SetInput(blob);
                    string[] outNames = { "feature_fusion/Conv_7/Sigmoid", "feature_fusion/GELU_2/Sigmoid" };
                    var output = new Mat[outNames.Length];
                    _eastNet.Forward(output, outNames);
                    using (Mat scores = output[0])
                    using (Mat geometry = output[1])
                    {
                        var (boxes, confidences) = Decode(scores, geometry, 0.5f);
                        CvDnn.NMSBoxes(boxes, confidences, 0.5f, 0.4f, out int[] indices);
                        var finalRects = new List<System.Drawing.Rectangle>();
                        foreach (int i in indices)
                        {
                            RotatedRect box = boxes[i];
                            OpenCvSharp.Point2f[] vertices = box.Points();
                            for (int j = 0; j < 4; j++)
                            {
                                vertices[j].X = vertices[j].X * (float)rW;
                                vertices[j].Y = vertices[j].Y * (float)rH;
                            }
                            var boundingBox = Cv2.BoundingRect(vertices);
                            // OpenCV Rect'i System.Drawing Rectangle'a dönüştür
                            int x = Math.Max(0, boundingBox.X);
                            int y = Math.Max(0, boundingBox.Y);
                            int width = Math.Min(sourceImage.Width - x, boundingBox.Width);
                            int height = Math.Min(sourceImage.Height - y, boundingBox.Height);
                            int padding = (int)(height * 0.1);
                            x = Math.Max(0, x - padding);
                            y = Math.Max(0, y - padding);
                            width = Math.Min(sourceImage.Width - x, width + 2 * padding);
                            height = Math.Min(sourceImage.Height - y, height + 2 * padding);
                            if (width > 10 && height > 5)
                                finalRects.Add(new System.Drawing.Rectangle(x, y, width, height));
                        }
                        foreach (var mat in output) mat.Dispose();
                        return finalRects;
                    }
                }
            }
        }

        private List<System.Drawing.Rectangle> FindTextRegionsWithOpenCV(Bitmap sourceImage)
        {
            using (Mat src = BitmapConverter.ToMat(sourceImage))
            using (Mat gray = src.CvtColor(ColorConversionCodes.BGR2GRAY))
            using (Mat binary = ApplyDynamicThresholding(gray))
            {
                // Gürültü azaltma
                Mat denoised = new Mat();
                Cv2.MedianBlur(binary, denoised, 3);

                // Morfolojik işlemler
                Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(3, 3));
                Cv2.MorphologyEx(denoised, denoised, MorphTypes.Open, kernel);
                Cv2.MorphologyEx(denoised, denoised, MorphTypes.Close, kernel);

                // Kontur bulma
                Cv2.FindContours(denoised, out var contours, out _, RetrievalModes.External,
                    ContourApproximationModes.ApproxSimple);

                var regions = new List<System.Drawing.Rectangle>();
                foreach (var contour in contours)
                {
                    // OpenCV Rect'i System.Drawing Rectangle'a dönüştür
                    var cvRect = Cv2.BoundingRect(contour);
                    var rect = new System.Drawing.Rectangle(cvRect.X, cvRect.Y, cvRect.Width, cvRect.Height);

                    // Filtreleme: çok küçük veya çok büyük alanları çıkar
                    if (rect.Width > 20 && rect.Height > 10 &&
                        rect.Width < src.Width * 0.9 && rect.Height < src.Height * 0.9)
                    {
                        // En-boy oranı kontrolü (metinler genelde yatay)
                        double aspectRatio = (double)rect.Width / rect.Height;
                        if (aspectRatio > 1.5 || aspectRatio < 0.67)
                        {
                            // Kontur alanını kontrol et
                            double area = Cv2.ContourArea(contour);
                            double boundingArea = rect.Width * rect.Height;
                            double solidity = area / boundingArea;

                            // Doldurma oranı yüksekse (metinler genelde dolgudur)
                            if (solidity > 0.3)
                            {
                                regions.Add(rect);
                            }
                        }
                    }
                }

                denoised.Dispose();
                kernel.Dispose();

                return regions;
            }
        }

        private Mat ApplyDynamicThresholding(Mat grayImage)
        {
            Mat binary = new Mat();

            if (_appSettings.EnableDynamicThresholding)
            {
                // Görüntü kalitesini analiz et
                double mean = Cv2.Mean(grayImage).Val0;
                double stdDev = CalculateStandardDeviation(grayImage);

                // Görüntü koşullarına göre eşikleme yöntemi seç
                if (stdDev < 30) // Düşük kontrastlı görüntü
                {
                    // Global eşikleme
                    Cv2.Threshold(grayImage, binary, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);
                }
                else if (stdDev > 80) // Yüksek kontrastlı görüntü
                {
                    // Adaptif eşikleme
                    int blockSize = _appSettings.AdaptiveThresholdBlockSize;
                    int C = _appSettings.AdaptiveThresholdC;

                    // Blok boyutunun tek sayı olduğundan emin ol
                    if (blockSize % 2 == 0) blockSize++;

                    Cv2.AdaptiveThreshold(grayImage, binary, 255,
                        AdaptiveThresholdTypes.GaussianC,
                        ThresholdTypes.Binary,
                        blockSize, C);
                }
                else // Orta kontrastlı görüntü
                {
                    // Hibrit yaklaşım
                    Mat globalBinary = new Mat();
                    Mat adaptiveBinary = new Mat();

                    Cv2.Threshold(grayImage, globalBinary, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);

                    int blockSize = _appSettings.AdaptiveThresholdBlockSize;
                    int C = _appSettings.AdaptiveThresholdC;
                    if (blockSize % 2 == 0) blockSize++;

                    Cv2.AdaptiveThreshold(grayImage, adaptiveBinary, 255,
                        AdaptiveThresholdTypes.GaussianC,
                        ThresholdTypes.Binary,
                        blockSize, C);

                    // İki sonucu birleştir
                    Cv2.BitwiseOr(globalBinary, adaptiveBinary, binary);

                    globalBinary.Dispose();
                    adaptiveBinary.Dispose();
                }
            }
            else
            {
                // Standart adaptif eşikleme
                int blockSize = _appSettings.AdaptiveThresholdBlockSize;
                int C = _appSettings.AdaptiveThresholdC;
                if (blockSize % 2 == 0) blockSize++;

                Cv2.AdaptiveThreshold(grayImage, binary, 255,
                    AdaptiveThresholdTypes.GaussianC,
                    ThresholdTypes.Binary,
                    blockSize, C);
            }

            return binary;
        }

        private double CalculateStandardDeviation(Mat grayImage)
        {
            Mat mean = new Mat();
            Mat stdDev = new Mat();
            Cv2.MeanStdDev(grayImage, mean, stdDev);

            double result = stdDev.Get<double>(0, 0);

            mean.Dispose();
            stdDev.Dispose();

            return result;
        }

        private (List<RotatedRect> boxes, List<float> confidences) Decode(Mat scores, Mat geometry, float confidenceThreshold)
        {
            var boxes = new List<RotatedRect>();
            var confidences = new List<float>();
            int height = scores.Size(2);
            int width = scores.Size(3);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float score = scores.At<float>(0, 0, y, x);
                    if (score < confidenceThreshold) continue;
                    float offsetX = x * 4.0f;
                    float offsetY = y * 4.0f;
                    float angle = geometry.At<float>(0, 4, y, x);
                    float h = geometry.At<float>(0, 0, y, x) + geometry.At<float>(0, 2, y, x);
                    float w = geometry.At<float>(0, 1, y, x) + geometry.At<float>(0, 3, y, x);
                    var center = new CvPoint(
                        offsetX + (float)(Math.Cos(angle) * geometry.At<float>(0, 1, y, x)) + (float)(Math.Sin(angle) * geometry.At<float>(0, 2, y, x)),
                        offsetY - (float)(Math.Sin(angle) * geometry.At<float>(0, 1, y, x)) + (float)(Math.Cos(angle) * geometry.At<float>(0, 2, y, x))
                    );
                    var size = new Size2f(w, h);
                    boxes.Add(new RotatedRect(center, size, -angle * 180 / (float)Math.PI));
                    confidences.Add(score);
                }
            }
            return (boxes, confidences);
        }

        public Bitmap CaptureWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return null;
            GetWindowRect(hWnd, out RECT rect);
            if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return null;
            var bmp = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var gfx = Graphics.FromImage(bmp))
            {
                IntPtr hdc = gfx.GetHdc();
                PrintWindow(hWnd, hdc, 2);
                gfx.ReleaseHdc(hdc);
            }
            return bmp;
        }

        public Bitmap CropImage(Bitmap image, System.Drawing.Rectangle region) => image.Clone(region, image.PixelFormat);
        /// OCR için görüntüyü ön işleme tabi tutar (süper çözünürlük, skew correction)
        private Bitmap PreprocessImageForOcr(Bitmap originalImage)
        {
            if (originalImage == null) return null;

            Bitmap processedImage = originalImage;

            try
            {
                // 1. Süper çözünürlük uygula (küçük görüntüler için)
                if (_appSettings.EnableSuperResolution && ShouldApplySuperResolution(originalImage))
                {
                    processedImage = ApplySuperResolution(processedImage);
                    _logger?.LogInformation($"Süper çözünürlük uygulandı: {originalImage.Width}x{originalImage.Height} -> {processedImage.Width}x{processedImage.Height}");
                }

                // 2. Skew correction uygula
                if (_appSettings.EnableSkewCorrection)
                {
                    float skewAngle = DetectSkewAngle(processedImage);
                    if (Math.Abs(skewAngle) > _appSettings.SkewCorrectionThreshold)
                    {
                        var correctedImage = CorrectSkew(processedImage, skewAngle);
                        if (processedImage != originalImage) processedImage.Dispose();
                        processedImage = correctedImage;
                        _logger?.LogInformation($"Skew correction uygulandı: {skewAngle:F2}°");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Görüntü ön işleme sırasında hata oluştu", ex);
                if (processedImage != originalImage) processedImage.Dispose();
                return originalImage;
            }

            return processedImage;
        }
        /// Süper çözünürlük uygulanıp uygulanmayacağını belirler
        private bool ShouldApplySuperResolution(Bitmap image)
        {
            return image.Width < _appSettings.MinImageSizeForSuperResolution ||
                   image.Height < _appSettings.MinImageSizeForSuperResolution;
        }
        /// Süper çözünürlük uygular
        private Bitmap ApplySuperResolution(Bitmap image)
        {
            if (image == null) return null;

            using (Mat src = BitmapConverter.ToMat(image))
            {
                var upscaled = new Mat();
                Cv2.Resize(src, upscaled, new OpenCvSharp.Size(0, 0), _appSettings.SuperResolutionScale, _appSettings.SuperResolutionScale,
                    InterpolationFlags.Cubic);

                // Gürültü azaltma
                Mat denoised = new Mat();
                Cv2.BilateralFilter(upscaled, denoised, 9, 75, 75);

                return BitmapConverter.ToBitmap(denoised);
            }
        }
        /// Görüntüdeki skew açısını tespit eder
        private float DetectSkewAngle(Bitmap image)
        {
            if (image == null) return 0f;

            using (Mat src = BitmapConverter.ToMat(image))
            using (Mat gray = src.CvtColor(ColorConversionCodes.BGR2GRAY))
            using (Mat binary = new Mat())
            {
                // Binary görüntü oluştur
                Cv2.Threshold(gray, binary, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);

                // Morfolojik işlemler
                Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
                Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kernel);

                // Konturları bul
                Cv2.FindContours(binary, out var contours, out _, RetrievalModes.External,
                    ContourApproximationModes.ApproxSimple);

                if (contours.Length == 0) return 0f;

                // En büyük konturu bul
                var largestContour = contours.OrderByDescending(contour => Cv2.ContourArea(contour)).First();

                // Minimum alan dikdörtgeni
                var rect = Cv2.MinAreaRect(largestContour);
                float angle = rect.Angle;

                // Açıyı normalize et (-45 ile +45 arasında)
                if (angle < -45) angle += 90;
                if (angle > 45) angle -= 90;

                kernel.Dispose();
                return angle;
            }
        }
        /// Skew correction uygular
        private Bitmap CorrectSkew(Bitmap image, float angle)
        {
            if (image == null || Math.Abs(angle) < 0.1f) return image;

            using (Mat src = BitmapConverter.ToMat(image))
            {
                var center = new OpenCvSharp.Point2f(src.Width / 2.0f, src.Height / 2.0f);
                var rotationMatrix = Cv2.GetRotationMatrix2D(center, -angle, 1.0);

                // Yeni boyutları hesapla
                var cos = Math.Abs(rotationMatrix.At<double>(0, 0));
                var sin = Math.Abs(rotationMatrix.At<double>(0, 1));
                var newWidth = (int)(src.Height * sin + src.Width * cos);
                var newHeight = (int)(src.Height * cos + src.Width * sin);

                // Merkezi ayarla
                rotationMatrix.Set(0, 2, rotationMatrix.At<double>(0, 2) + (newWidth / 2.0) - center.X);
                rotationMatrix.Set(1, 2, rotationMatrix.At<double>(1, 2) + (newHeight / 2.0) - center.Y);

                var rotated = new Mat();
                Cv2.WarpAffine(src, rotated, rotationMatrix, new OpenCvSharp.Size(newWidth, newHeight));

                return BitmapConverter.ToBitmap(rotated);
            }
        }

        public Bitmap IsolateTextByColor(Bitmap sourceImage)
        {
            if (sourceImage == null) return null;

            try
            {
                if (_appSettings.EnableAutoColorDetection)
                {
                    return AutoDetectAndIsolateTextByColor(sourceImage);
                }
                else
                {
                    return ManualColorIsolation(sourceImage);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Renk filtresi uygulanırken hata oluştu, orijinal görüntü döndürülüyor", ex);
                return sourceImage; // Hata durumunda orijinal görüntüyü döndür
            }
        }

        private Bitmap AutoDetectAndIsolateTextByColor(Bitmap sourceImage)
        {
            using (Mat src = BitmapConverter.ToMat(sourceImage))
            using (Mat hsv = new Mat())
            {
                Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);

                // Gelişmiş metin rengi algılama
                Scalar[] textColors = DetectTextColors(hsv);

                if (textColors == null || textColors.Length == 0)
                {
                    _logger?.LogWarning("Metin rengi bulunamadı, orijinal görüntü döndürülüyor");
                    return sourceImage;
                }

                // Her metin rengi için maske oluştur
                using (Mat combinedMask = new Mat())
                {
                    bool firstMask = true;
                    for (int i = 0; i < textColors.Length; i++)
                    {
                        var color = textColors[i];
                        // Daha geniş tolerans aralığı kullan
                        Scalar lower = new Scalar(
                            Math.Max(0, color.Val0 - 30),
                            Math.Max(0, color.Val1 - 80),
                            Math.Max(0, color.Val2 - 80));
                        Scalar upper = new Scalar(
                            Math.Min(180, color.Val0 + 30),
                            Math.Min(255, color.Val1 + 80),
                            Math.Min(255, color.Val2 + 80));

                        using (Mat colorMask = new Mat())
                        {
                            Cv2.InRange(hsv, lower, upper, colorMask);

                            if (firstMask)
                            {
                                colorMask.CopyTo(combinedMask);
                                firstMask = false;
                            }
                            else
                            {
                                Cv2.BitwiseOr(combinedMask, colorMask, combinedMask);
                            }
                        }
                    }

                    // Maskeyi uygula
                    using (Mat result = new Mat())
                    {
                        Cv2.BitwiseAnd(src, src, result, combinedMask);
                        return BitmapConverter.ToBitmap(result);
                    }
                }
            }
        }

        private Scalar[] FindDominantColors(Mat hsvImage)
        {
            try
            {
                // Basit ve güvenilir renk algılama yöntemi
                using (Mat gray = hsvImage.CvtColor(ColorConversionCodes.HSV2BGR).CvtColor(ColorConversionCodes.BGR2GRAY))
                {
                    // Histogram hesapla
                    Mat hist = new Mat();
                    int[] histSize = { 256 };
                    Rangef[] ranges = { new Rangef(0, 256) };
                    Mat[] channels = { gray };
                    Cv2.CalcHist(channels, new int[] { 0 }, null, hist, 1, histSize, ranges);

                    // En yüksek değerli renkleri bul
                    List<Scalar> dominantColors = new List<Scalar>();

                    // Beyaz ve sarı renkleri varsayılan olarak ekle (metin renkleri)
                    dominantColors.Add(new Scalar(0, 0, 200)); // Beyaz benzeri
                    dominantColors.Add(new Scalar(30, 255, 255)); // Sarı benzeri

                    // Histogramdan en yüksek değerli renkleri bul
                    for (int i = 0; i < 2; i++) // En fazla 2 ek renk
                    {
                        double minVal, maxVal;
                        CvPoint minLoc, maxLoc;
                        Cv2.MinMaxLoc(hist, out minVal, out maxVal, out minLoc, out maxLoc);

                        if (maxVal > 100) // Yeterince yaygın olan renkler
                        {
                            int intensity = maxLoc.X;
                            // Yoğunluğu HSV'ye dönüştür
                            dominantColors.Add(new Scalar(0, 0, intensity));

                            // Bu rengi histogramdan kaldır
                            hist.Set<float>(intensity, 0, 0);
                        }
                        else
                        {
                            break;
                        }
                    }

                    return dominantColors.ToArray();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Renk algılama sırasında hata oluştu", ex);
                // Varsayılan renkler döndür
                return new Scalar[]
                {
                    new Scalar(0, 0, 200), // Beyaz benzeri
                    new Scalar(30, 255, 255) // Sarı benzeri
                };
            }
        }

        private Bitmap ManualColorIsolation(Bitmap sourceImage)
        {
            using (Mat src = BitmapConverter.ToMat(sourceImage))
            using (Mat hsv = new Mat())
            {
                Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);
                Scalar lowerWhite = new Scalar(0, 0, 180);
                Scalar upperWhite = new Scalar(255, 50, 255);
                Scalar lowerYellow = new Scalar(20, 100, 100);
                Scalar upperYellow = new Scalar(30, 255, 255);
                using (Mat whiteMask = new Mat())
                using (Mat yellowMask = new Mat())
                using (Mat combinedMask = new Mat())
                using (Mat result = new Mat())
                {
                    Cv2.InRange(hsv, lowerWhite, upperWhite, whiteMask);
                    Cv2.InRange(hsv, lowerYellow, upperYellow, yellowMask);
                    Cv2.BitwiseOr(whiteMask, yellowMask, combinedMask);
                    Cv2.BitwiseAnd(src, src, result, combinedMask);
                    return BitmapConverter.ToBitmap(result);
                }
            }
        }

        /// Gelişmiş metin rengi algılama
        public Scalar[] DetectTextColors(Mat hsvImage)
        {
            try
            {
                // Histogram analizi
                Scalar[] dominantColors = FindDominantColors(hsvImage);

                if (dominantColors == null || dominantColors.Length == 0)
                {
                    _logger?.LogWarning("Dominant renk bulunamadı, varsayılan metin renkleri kullanılıyor");
                    // Varsayılan metin renkleri (beyaz, sarı, açık gri)
                    return new Scalar[]
                    {
                        new Scalar(0, 0, 200),    // Beyaz benzeri
                        new Scalar(30, 255, 255), // Sarı benzeri
                        new Scalar(0, 0, 150)     // Açık gri benzeri
                    };
                }

                // Metin rengi kriterleri
                var textColors = new List<Scalar>();
                foreach (var color in dominantColors)
                {
                    // Gelişmiş metin rengi kriterleri
                    bool isTextColor = false;

                    // Yüksek parlaklık (Val2) - metin genelde parlak renklerde
                    if (color.Val2 > 120)
                    {
                        isTextColor = true;
                    }
                    // Orta parlaklık ama yüksek doygunluk (Val1) - renkli metinler
                    else if (color.Val2 > 80 && color.Val1 > 50)
                    {
                        isTextColor = true;
                    }
                    // Düşük doygunluk ama yüksek parlaklık - gri tonlarında metin
                    else if (color.Val1 < 30 && color.Val2 > 100)
                    {
                        isTextColor = true;
                    }

                    if (isTextColor)
                    {
                        textColors.Add(color);
                        _logger?.LogInformation($"Metin rengi tespit edildi: H={color.Val0:F1}, S={color.Val1:F1}, V={color.Val2:F1}");
                    }
                }

                // Hiç metin rengi bulunamazsa varsayılan renkleri döndür
                if (textColors.Count == 0)
                {
                    _logger?.LogWarning("Hiçbir metin rengi tespit edilemedi, varsayılan renkler kullanılıyor");
                    return new Scalar[]
                    {
                        new Scalar(0, 0, 200),    // Beyaz benzeri
                        new Scalar(30, 255, 255)  // Sarı benzeri
                    };
                }

                return textColors.ToArray();
            }
            catch (Exception ex)
            {
                _logger?.LogError("Metin rengi algılama sırasında hata oluştu", ex);
                // Hata durumunda varsayılan renkler
                return new Scalar[]
                {
                    new Scalar(0, 0, 200),    // Beyaz benzeri
                    new Scalar(30, 255, 255)  // Sarı benzeri
                };
            }
        }
        /// Kontrast tabanlı maske oluşturur
        public Mat CreateContrastMask(Mat src)
        {
            using (Mat gray = src.CvtColor(ColorConversionCodes.BGR2GRAY))
            {
                // Adaptif eşikleme
                Mat binary = new Mat();
                Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.GaussianC,
                    ThresholdTypes.Binary, 11, 2);

                // Gürültü azaltma
                Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(2, 2));
                Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kernel);
                kernel.Dispose();

                return binary;
            }
        }

        /// Edge tabanlı maske oluşturur
        public Mat CreateEdgeMask(Mat src)
        {
            using (Mat gray = src.CvtColor(ColorConversionCodes.BGR2GRAY))
            using (Mat blurred = new Mat())
            {
                // Gürültü azaltma
                Cv2.GaussianBlur(gray, blurred, new OpenCvSharp.Size(3, 3), 0);

                // Canny edge detection
                Mat edges = new Mat();
                Cv2.Canny(blurred, edges, 50, 150);

                // Edge'leri genişlet
                Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(2, 2));
                Cv2.Dilate(edges, edges, kernel);
                kernel.Dispose();

                return edges;
            }
        }

        Mat IOcrService.CreateEdgeMask(Mat imageMat)
        {
            return CreateEdgeMask(imageMat);
        }

        Mat IOcrService.CreateContrastMask(Mat imageMat)
        {
            return CreateContrastMask(imageMat);
        }
    }
}
