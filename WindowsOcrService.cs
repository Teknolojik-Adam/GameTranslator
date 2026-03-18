using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Tesseract;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using OpenCvSharp.Extensions;
using System.Drawing.Imaging;


namespace GameTranslatorUltimate
{
    public class WindowsOcrService : IOcrService
    {
        private readonly OcrEngine _ocrEngine;
        private readonly ILogger _logger;

        public WindowsOcrService(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            try
            {
                _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_ocrEngine == null)
                {
                    _logger.LogWarning("Windows OCR Altyapısı kullanıcı profili dilleriyle başlatılamadı. İngilizceye geriye...");
                    var lang = new Language("en-US");
                    if (OcrEngine.IsLanguageSupported(lang))
                    {
                        _ocrEngine = OcrEngine.TryCreateFromLanguage(lang);
                    }
                }
                if (_ocrEngine != null)
                {
                    _logger.LogInformation($"Dil için başlatılan Windows OCR Motoru: {_ocrEngine.RecognizerLanguage.DisplayName}");
                }
                else
                {
                    _logger.LogError("Windows OCR Altyapısı başlatılamadı...");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Windows OCR Altyapısı başlatılamadı.", ex);
                _ocrEngine = null;
            }
        }

        public async Task<string> RecognizeTextInRegionsAsync(Bitmap image, string language = "eng")
        {
            if (_ocrEngine == null || image == null) return string.Empty;
            var allRecognizedText = new StringBuilder();
            try
            {
                using (Bitmap processedImage = PreprocessImage(image))
                {
                    List<Rectangle> textRegions = FindTextRegions(processedImage);
                    if (!textRegions.Any())
                    {
                        textRegions.Add(new Rectangle(0, 0, image.Width, image.Height));
                        _logger.LogWarning("Hassas metin bölgesi bulunamadı, tüm görüntü işlenecek.");
                    }
                    foreach (var region in textRegions)
                    {
                        using (var croppedImage = CropImage(processedImage, region))
                        {
                            if (croppedImage == null) continue;
                            using (SoftwareBitmap softwareBitmap = await CreateSoftwareBitmapFromBitmap(croppedImage))
                            {
                                if (softwareBitmap == null)
                                {
                                    _logger.LogWarning("SoftwareBitmap kaynak görüntüden oluşturulamadı.");
                                    continue;
                                }
                                OcrResult ocrResult = await _ocrEngine.RecognizeAsync(softwareBitmap);
                                string rawOcrText = ocrResult.Text?.Trim() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(rawOcrText))
                                {
                                    allRecognizedText.Append(rawOcrText).Append(" ");
                                }
                            }
                        }
                    }
                }
                return allRecognizedText.ToString().Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError("Windows OCR tanıma sırasında bir hata oluştu.", ex);
                return string.Empty;
            }
        }

        public Task<string> GetTextAdaptiveAsync(Bitmap image, string language) => RecognizeTextInRegionsAsync(image, language);
        public Task<string> GetTextFromImage(Bitmap image, string language = "eng", bool invertColors = false) => RecognizeTextInRegionsAsync(image, language);

        public List<Rectangle> FindTextRegions(Bitmap sourceImage)
        {
            if (sourceImage == null) return new List<Rectangle>();
            try
            {
                using (Mat mat = BitmapConverter.ToMat(sourceImage))
                using (Mat gray = new Mat())
                {
                    Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);

                    using (Mat morphKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(15, 3)))
                    using (Mat grad = new Mat())
                    {
                        Cv2.MorphologyEx(gray, grad, MorphTypes.Gradient, morphKernel);
                        Cv2.Threshold(grad, grad, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

                        Cv2.FindContours(grad, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                        var textRegions = new List<Rectangle>();

                        foreach (OpenCvSharp.Point[] contour in contours)
                        {

                            OpenCvSharp.Rect rect = Cv2.BoundingRect(contour);
                            if (rect.Width > 10 && rect.Height > 5 && rect.Width < sourceImage.Width / 2)
                            {
                                textRegions.Add(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height));
                            }
                        }
                        return textRegions;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"FindTextRegions failed (OpenCV issue?): {ex.Message}");
                return new List<Rectangle> { new Rectangle(0, 0, sourceImage.Width, sourceImage.Height) };
            }
        }

        public Bitmap IsolateTextByColor(Bitmap sourceImage) => sourceImage;

        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        public Bitmap CaptureWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return null;
            GetWindowRect(hWnd, out RECT rect);
            if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return null;
            var bmp = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top, PixelFormat.Format32bppArgb);
            using (var gfx = Graphics.FromImage(bmp))
            {
                IntPtr hdc = gfx.GetHdc();
                PrintWindow(hWnd, hdc, 2);
                gfx.ReleaseHdc(hdc);
            }
            return bmp;
        }

        public Bitmap CropImage(Bitmap image, Rectangle region) => image?.Clone(region, image.PixelFormat);

        private async Task<SoftwareBitmap> CreateSoftwareBitmapFromBitmap(Bitmap bitmap)
        {
            if (bitmap == null) return null;
            using (var stream = new InMemoryRandomAccessStream())
            {
              
                bitmap.Save(stream.AsStreamForWrite(), System.Drawing.Imaging.ImageFormat.Bmp);
                stream.Seek(0);
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync();
                if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || softwareBitmap.BitmapAlphaMode == BitmapAlphaMode.Straight)
                {
                    var convertedBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    softwareBitmap.Dispose(); // Eski objeyi temizle
                    softwareBitmap = convertedBitmap;
                }
                return softwareBitmap;
            }
        }

        public Bitmap PreprocessImage(Bitmap image)
        {
            if (image == null) return null;
            Bitmap processedBitmap = new Bitmap(image);
            OptimizeImageForOcr(processedBitmap);
            return processedBitmap;
        }

        private void OptimizeImageForOcr(Bitmap bitmap)
        {
            try
            {
                using (Mat src = BitmapConverter.ToMat(bitmap))
                using (Mat gray = new Mat())
                {
                    Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
                    Cv2.EqualizeHist(gray, gray);
                    Cv2.Threshold(gray, gray, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                    Cv2.CvtColor(gray, src, ColorConversionCodes.GRAY2BGR);
                    BitmapConverter.ToBitmap(src, bitmap);
                }
            }
            catch (Exception ex)
            {
                // Fallback: do nothing to the bitmap if OpenCV fails
                _logger.LogWarning($"OptimizeImageForOcr failed (OpenCV issue?): {ex.Message}");
            }
        }

        public Mat CreateContrastMask(Mat sourceImage)
        {
            using (Mat gray = new Mat())
            {
                Cv2.CvtColor(sourceImage, gray, ColorConversionCodes.BGR2GRAY);
                Mat mask = new Mat();
                Cv2.AdaptiveThreshold(gray, mask, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, 11, 2);
                return mask;
            }
        }

        public Mat CreateEdgeMask(Mat sourceImage)
        {
            using (Mat gray = new Mat())
            {
                Cv2.CvtColor(sourceImage, gray, ColorConversionCodes.BGR2GRAY);
            
                Cv2.Blur(gray, gray, new OpenCvSharp.Size(3, 3));
                Mat edges = new Mat();
                Cv2.Canny(gray, edges, 100, 200);
                return edges;
            }
        }

        public Scalar[] DetectTextColors(Mat sourceImage)
        {
            using (Mat hsv = new Mat())
            {
                Cv2.CvtColor(sourceImage, hsv, ColorConversionCodes.BGR2HSV);
                using (Mat hist = new Mat())
                {
                    int[] channels = { 0 };
                    int[] histSize = { 180 };
                    Rangef[] ranges = { new Rangef(0, 180) };

                    Cv2.CalcHist(new Mat[] { hsv }, channels, null, hist, 1, histSize, ranges);
                    
                    Scalar[] dominantColors = new Scalar[3];
                    for (int i = 0; i < 3; i++)
                    {
                        Cv2.MinMaxLoc(hist, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);
                        dominantColors[i] = new Scalar(maxLoc.Y, 255, 255);
                        
                        hist.Set<float>(maxLoc.Y, 0, 0f);
                    }
                    
                    return dominantColors;
                }
            }
        }
    }
}
