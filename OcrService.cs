using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Tesseract;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using OpenCvSharp.Extensions;

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
        private readonly Net _eastNet;
        private const string EastModelPath = "frozen_east_text_detection.pb";

        public OcrService(ILogger logger)
        {
            _logger = logger;

            if (File.Exists(EastModelPath))
            {
                _eastNet = CvDnn.ReadNet(EastModelPath);
                _logger.LogInformation("EAST metin tespit modeli başarıyla yüklendi.");
            }
            else
            {
                _logger.LogError($"EAST modeli bulunamadı: {Path.GetFullPath(EastModelPath)}. Metin tespiti bu yöntemle çalışmayacak.");
                _eastNet = null;
            }
        }

        public async Task<string> RecognizeTextInRegionsAsync(Bitmap image, string language = "eng", PageSegMode psm = PageSegMode.Auto)
        {
            if (image == null) return string.Empty;

            var regions = FindTextRegions(image);

            if (!regions.Any())
            {
                _logger.LogWarning("Görüntüde metin bölgesi tespit edilemedi. Görüntünün tamamı taranacak.");
                return await GetTextAdaptiveAsync(image, language, PageSegMode.SingleBlock);
            }

            var tasks = regions.Select(region => RecognizeTextInSingleRegionAsync(image, region, language, psm));
            var recognizedTexts = await Task.WhenAll(tasks);

            return string.Join(" ", recognizedTexts.Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        private async Task<string> RecognizeTextInSingleRegionAsync(Bitmap sourceImage, Rectangle region, string language, PageSegMode psm)
        {
            using (var regionImage = CropImage(sourceImage, region))
            {
                return await GetTextAdaptiveAsync(regionImage, language, psm);
            }
        }

        public async Task<string> GetTextAdaptiveAsync(Bitmap image, string language, PageSegMode psm = PageSegMode.Auto)
        {
            if (image == null) return string.Empty;

            var adaptiveResult = await TryRecognizeWithStrategy(image, language, psm, Preprocess_AdaptiveThreshold);
            _logger.LogInformation($"[Strateji 1: Adaptif] Sonuç: '{adaptiveResult.Text}', Güvenilirlik: {adaptiveResult.Confidence:P}");

            // Eğer sonuç yeterince iyiyse, hemen döndür.
            if (adaptiveResult.Confidence > 0.70) // %70 güvenilirlik 
            {
                return adaptiveResult.Text;
            }

            // 2. Deneme: Derin analiz (mevcut çalışan yöntem)
            var optimalResult = await TryRecognizeWithStrategy(image, language, psm, Preprocess_OptimalThreshold);
            _logger.LogInformation($"[Strateji 2: Optimal] Sonuç: '{optimalResult.Text}', Güvenilirlik: {optimalResult.Confidence:P}");

            // İki stratejiden en iyi sonucu seç ve döndür.
            if (adaptiveResult.Confidence > optimalResult.Confidence)
            {
                _logger.LogInformation("Karar: Adaptif strateji sonucu daha güvenilir.");
                return adaptiveResult.Text;
            }
            else
            {
                _logger.LogInformation("Karar: Optimal eşik stratejisi sonucu daha güvenilir.");
                return optimalResult.Text;
            }
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
                    _logger.LogError($"OCR stratejisi {preprocessStrategy.Method.Name} başarısız oldu.", ex);
                    return (string.Empty, 0f);
                }
            });
        }

        #region Preprocessing Strategies

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

        #endregion

        public async Task<string> GetTextFromImage(Bitmap image, string language = "eng", bool invertColors = false)
        {
            return await GetTextAdaptiveAsync(image, language);
        }

        public List<Rectangle> FindTextRegions(Bitmap sourceImage)
        {
            if (_eastNet == null || sourceImage == null)
            {
                if (_eastNet == null) _logger.LogWarning("EAST modeli yüklenmediği için metin tespiti atlanıyor.");
                return new List<Rectangle>();
            }

            using (Mat src = BitmapConverter.ToMat(sourceImage))
            {
                int newW = (int)(src.Width / 32.0) * 32;
                int newH = (int)(src.Height / 32.0) * 32;

                if (newW <= 0 || newH <= 0)
                {
                    _logger.LogWarning($"Görüntü boyutu ({src.Width}x{src.Height}) EAST modeli için çok küçük.");
                    return new List<Rectangle>();
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

                        var finalRects = new List<Rectangle>();
                        foreach (int i in indices)
                        {
                            RotatedRect box = boxes[i];
                            Point2f[] vertices = box.Points();

                            for (int j = 0; j < 4; j++)
                            {
                                vertices[j].X = (int)(vertices[j].X * rW);
                                vertices[j].Y = (int)(vertices[j].Y * rH);
                            }

                            var boundingBox = Cv2.BoundingRect(vertices);

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
                                finalRects.Add(new Rectangle(x, y, width, height));
                        }

                        foreach (var mat in output) mat.Dispose();
                        return finalRects;
                    }
                }
            }
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

                    var center = new Point2f(
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

        #region Mevcut Yardımcı Metotlar 
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

        public Bitmap CropImage(Bitmap image, Rectangle region) => image.Clone(region, image.PixelFormat);

        public Bitmap IsolateTextByColor(Bitmap sourceImage)
        {
            using (Mat src = BitmapConverter.ToMat(sourceImage))
            using (Mat hsv = new Mat())
            {
                Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);

                // Beyaz ve açık sarı tonları
                Scalar lowerWhite = new Scalar(0, 0, 180);
                Scalar upperWhite = new Scalar(255, 50, 255);

                Scalar lowerYellow = new Scalar(20, 100, 100);
                Scalar upperYellow = new Scalar(30, 255, 255);

                using (Mat whiteMask = new Mat())
                using (Mat yellowMask = new Mat())
                using (Mat combinedMask = new Mat())
                using (Mat result = new Mat())
                {
                    // Renk aralıklarına göre oluşturuyor
                    Cv2.InRange(hsv, lowerWhite, upperWhite, whiteMask);
                    Cv2.InRange(hsv, lowerYellow, upperYellow, yellowMask);

                    // İki resmi birleştiriyor
                    Cv2.BitwiseOr(whiteMask, yellowMask, combinedMask);

                    // Orijinal görüntüden sadece maskelenen kısımları aliyoor
                    Cv2.BitwiseAnd(src, src, result, combinedMask);

                    return BitmapConverter.ToBitmap(result);
                }
            }
        }
        #endregion
    }
}
