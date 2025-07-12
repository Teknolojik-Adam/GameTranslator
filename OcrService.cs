using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Tesseract; 
using OpenCvSharp;
using OpenCvSharp.Extensions;
using CvPoint = OpenCvSharp.Point;
using CvSize = OpenCvSharp.Size;

namespace P5S_ceviri
{
    public class OcrService : IOcrService
    {
        #region Win32 Imports and Constants
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        #endregion

        private int _lastOptimalThreshold = 128;
        private readonly ILogger _logger;

        public OcrService(ILogger logger)
        {
            _logger = logger;
        }

 
        public async Task<string> RecognizeTextInRegionsAsync(Bitmap image, string language = "eng", PageSegMode psm = PageSegMode.Auto)
        {
            if (image == null) return string.Empty;

            var regions = FindTextRegions(image);
            if (!regions.Any())
            {
                // Eğer bölge bulunamazsa, tüm görüntüyü tek bir blok olarak okumayı dene.
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
                // GÜNCELLENDİ: PSM parametresini iletiyoruz.
                return await GetTextAdaptiveAsync(regionImage, language, psm);
            }
        }

        // GÜNCELLENDİ: Metot artık PageSegMode parametresi alıyor.
        public async Task<string> GetTextAdaptiveAsync(Bitmap image, string language, PageSegMode psm = PageSegMode.Auto)
        {
            string recognizedText = GetTextWithPreprocessing(image, language, _lastOptimalThreshold, psm);

            if (string.IsNullOrWhiteSpace(recognizedText) || recognizedText.Length < 3)
            {
                int newThreshold = FindOptimalThreshold(image);
                if (newThreshold != -1 && newThreshold != _lastOptimalThreshold)
                {
                    _logger.LogInformation($"Yeni optimal OCR ayarı bulundu: {newThreshold}");
                    _lastOptimalThreshold = newThreshold;
                    recognizedText = GetTextWithPreprocessing(image, language, newThreshold, psm);
                }
            }
            return recognizedText;
        }

        // GÜNCELLENDİ: Metot artık PageSegMode parametresi alıyor ve bunu Tesseract'a iletiyor.
        private string GetTextWithPreprocessing(Bitmap image, string language, int threshold, PageSegMode psm)
        {
            try
            {
                using (var preprocessedImage = PreprocessImageForOcr(image, threshold))
                // GÜNCELLENDİ: Tesseract motorunu belirtilen PSM ile başlatıyoruz.
                using (var engine = new TesseractEngine(@"./tessdata", language, EngineMode.LstmOnly, null, null, false) { DefaultPageSegMode = psm })
                {
                    engine.SetVariable("user_defined_dpi", "300");
                    using (var page = engine.Process(preprocessedImage))
                    {
                        return page.GetText()?.Trim().Replace("\n", " ").Replace("  ", " ") ?? string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"OCR işlemi sırasında hata (Threshold: {threshold}, PSM: {psm})", ex);
                return string.Empty;
            }
        }

        // Arayüze uyumluluk için bu metodu da güncelliyoruz.
        public async Task<string> GetTextFromImage(Bitmap image, string language = "eng", bool invertColors = false)
        {
            return await GetTextAdaptiveAsync(image, language);
        }

        #region Mevcut Yardımcı Metotlar (Değişiklik Yok)
        private int FindOptimalThreshold(Bitmap image)
        {
            using (var grayMat = BitmapConverter.ToMat(image).CvtColor(ColorConversionCodes.BGR2GRAY))
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

        private Bitmap PreprocessImageForOcr(Bitmap image, int threshold)
        {
            using (var mat = BitmapConverter.ToMat(image))
            {
                using (var deskewedMat = Deskew(mat))
                {
                    const double idealHeight = 100.0;
                    double scale = idealHeight / deskewedMat.Height;
                    Mat resizedMat;
                    if (scale > 1.1)
                    {
                        resizedMat = deskewedMat.Resize(new CvSize(), scale, scale, InterpolationFlags.Cubic);
                    }
                    else
                    {
                        resizedMat = deskewedMat.Clone();
                    }

                    using (resizedMat)
                    using (var gray = resizedMat.CvtColor(ColorConversionCodes.BGR2GRAY))
                    {
                        Cv2.MedianBlur(gray, gray, 3);
                        Cv2.Threshold(gray, gray, threshold, 255, ThresholdTypes.Binary);
                        return BitmapConverter.ToBitmap(gray);
                    }
                }
            }
        }

        private Mat Deskew(Mat src)
        {
            try
            {
                using (var gray = src.CvtColor(ColorConversionCodes.BGR2GRAY))
                using (var inverted = new Mat())
                {
                    Cv2.BitwiseNot(gray, inverted);
                    var element = Cv2.GetStructuringElement(MorphShapes.Rect, new CvSize(25, 5));
                    Cv2.Erode(inverted, inverted, element);

                    using (var coords = new Mat())
                    {
                        Cv2.FindNonZero(inverted, coords);
                        if (coords.Rows < 10) return src.Clone();

                        var rect = Cv2.MinAreaRect(coords.FindNonZero());
                        var angle = rect.Angle;
                        if (angle < -45.0) angle = 90.0f + angle;

                        var center = new Point2f(src.Width / 2f, src.Height / 2f);
                        using (var rotMatrix = Cv2.GetRotationMatrix2D(center, angle, 1.0))
                        {
                            var result = new Mat();
                            Cv2.WarpAffine(src, result, rotMatrix, src.Size(), InterpolationFlags.Cubic);
                            return result;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Görüntü düzleştirme (deskew) sırasında hata oluştu.", ex);
                return src.Clone();
            }
        }

        public List<Rectangle> FindTextRegions(Bitmap sourceImage)
        {
            using (Mat src = BitmapConverter.ToMat(sourceImage))
            using (Mat gray = src.CvtColor(ColorConversionCodes.BGR2GRAY))
            using (Mat edges = gray.Canny(100, 200, 3))
            using (Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new CvSize(5, 5)))
            {
                Cv2.Dilate(edges, edges, kernel, iterations: 2);
                Cv2.FindContours(edges, out CvPoint[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                return contours.Select(c => Cv2.BoundingRect(c))
                               .Where(r => r.Width > 50 && r.Height > 20 && r.Width < src.Width * 0.95 && r.Height < src.Height * 0.95)
                               .Select(r => new Rectangle(r.X, r.Y, r.Width, r.Height))
                               .ToList();
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
        #endregion
    }
}