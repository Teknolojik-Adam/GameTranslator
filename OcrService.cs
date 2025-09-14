using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using OpenCvSharp.Extensions;
using Tesseract;

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
                { OcrEngineType.Tesseract, new TesseractOcrEngine(logger) },
                { OcrEngineType.WindowsOcr, new WindowsOcrEngine(logger) }
            };

            if (File.Exists(EastModelPath))
            {
                _eastNet = CvDnn.ReadNet(EastModelPath);
                _logger.LogInformation("EAST metin algýlama modeli baþarýyla yüklendi.");
            }
            else
            {
                _logger.LogError($"EAST model bulunamadý: {Path.GetFullPath(EastModelPath)}. Metin algýlama yapýlamayacak.");
                _eastNet = null;
            }
        }

        public async Task<string> GetTextFromImage(Bitmap image, string language, bool invertColors = false)
        {
            return await GetTextAdaptiveAsync(image, language);
        }

        public async Task<string> GetTextAdaptiveAsync(Bitmap image, string language, PageSegMode psm = PageSegMode.Auto)
        {
            if (image == null) return string.Empty;
            if (_ocrEngines.TryGetValue(_appSettings.OcrEngine, out var engine))
            {
                return await engine.RecognizeTextAsync(image, language);
            }
            _logger.LogError($"Seçilen OCR motoru '{_appSettings.OcrEngine}' bulunamadý.");
            return string.Empty;
        }

        public async Task<string> RecognizeTextInRegionsAsync(Bitmap image, string language, PageSegMode psm = PageSegMode.Auto)
        {
            if (image == null) return string.Empty;

            // Metin algýlama yöntemine göre optimizasyon
            if (_appSettings.TextDetectionMethod == TextDetectionMethod.None)
            {
                // Tam ekran modunda doðrudan OCR yap
                return await GetTextAdaptiveAsync(image, language, psm);
            }

            var regions = FindTextRegions(image);
            if (!regions.Any())
            {
                _logger.LogWarning("Metin bölgesi algýlanamadý. Tam görüntü taranýyor.");
                return await GetTextAdaptiveAsync(image, language, psm);
            }

            // Paralel bölge iþleme
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
            // Kullanýcý tercihi yoksa tam ekran tara
            if (_appSettings.TextDetectionMethod == TextDetectionMethod.None)
            {
                return new List<System.Drawing.Rectangle> { new System.Drawing.Rectangle(0, 0, sourceImage.Width, sourceImage.Height) };
            }

            // EAST modeli kullanýlacaksa
            if (_appSettings.TextDetectionMethod == TextDetectionMethod.East)
            {
                if (_eastNet == null || sourceImage == null)
                {
                    _logger?.LogWarning("EAST modeli yüklenmedi, tam ekran taranacak.");
                    return new List<System.Drawing.Rectangle> { new System.Drawing.Rectangle(0, 0, sourceImage.Width, sourceImage.Height) };
                }

                return FindTextRegionsWithEast(sourceImage);
            }

            // OpenCV ile genel metin algýlama
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
                            Point2f[] vertices = box.Points();
                            for (int j = 0; j < 4; j++)
                            {
                                vertices[j].X = (int)(vertices[j].X * rW);
                                vertices[j].Y = (int)(vertices[j].Y * rH);
                            }
                            var boundingBox = Cv2.BoundingRect(vertices);
                            // OpenCV Rect'i System.Drawing Rectangle'a dönüþtür
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
            using (Mat binary = new Mat())
            {
                // Adaptif eþikleme
                Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.GaussianC,
                    ThresholdTypes.Binary, 11, 2);

                // Gürültü azaltma
                Cv2.MedianBlur(binary, binary, 3);

               
                Cv2.FindContours(binary, out var contours, out _, RetrievalModes.External,
                    ContourApproximationModes.ApproxSimple);

                var regions = new List<System.Drawing.Rectangle>();
                foreach (var contour in contours)
                {
                    // OpenCV Rect'i System.Drawing Rectangle'a dönüþtür
                    var cvRect = Cv2.BoundingRect(contour);
                    var rect = new System.Drawing.Rectangle(cvRect.X, cvRect.Y, cvRect.Width, cvRect.Height);

                    // Filtreleme: çok küçük veya çok büyük alanlarý çýkar
                    if (rect.Width > 20 && rect.Height > 10 &&
                        rect.Width < src.Width * 0.9 && rect.Height < src.Height * 0.9)
                    {
                        // En-boy oraný kontrolü (metinler genelde yatay)
                        double aspectRatio = (double)rect.Width / rect.Height;
                        if (aspectRatio > 1.5 || aspectRatio < 0.67)
                        {
                            regions.Add(rect);
                        }
                    }
                }

                return regions;
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

        public Bitmap IsolateTextByColor(Bitmap sourceImage)
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
    }
}