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
using CvPoint = OpenCvSharp.Point;

namespace P5S_ceviri
{
    public class OcrService : IOcrService, IDisposable
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
        private bool _disposed = false;

        public OcrService(ILogger logger, AppSettings appSettings)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
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
                _logger.LogError($"EAST model bulunamadı: {Path.GetFullPath(EastModelPath)}. Gelişmiş metin algılama yapılamayacak.");
                _eastNet = null;
            }
        }

        public async Task<string> GetTextFromImage(Bitmap image, string language, bool invertColors = false)
        {
            if (image == null) return string.Empty;

            using (var processedImage = PreprocessImageForOcr(image, invertColors))
            {
                if (processedImage == null) return string.Empty;

                return await RecognizeTextInRegionsAsync(processedImage, language);
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

            using (var processedImage = PreprocessImageForOcr(image))
            {
                if (processedImage == null) return string.Empty;

                if (_appSettings.TextDetectionMethod == TextDetectionMethod.None)
                {
                    return await GetTextAdaptiveAsync(processedImage, language, psm);
                }

                var regions = FindTextRegions(processedImage);
                if (!regions.Any())
                {
                    _logger.LogWarning("Metin bölgesi algılanamadı. Tam görüntü taranıyor.");
                    return await GetTextAdaptiveAsync(processedImage, language, psm);
                }

                _logger.LogInformation($"{regions.Count} adet metin bölgesi bulundu.");

                var tasks = regions.Select(region => RecognizeTextInSingleRegionAsync(processedImage, region, language, psm));
                var recognizedTexts = await Task.WhenAll(tasks);

                return string.Join(" ", recognizedTexts.Where(t => !string.IsNullOrWhiteSpace(t)));
            }
        }

        private async Task<string> RecognizeTextInSingleRegionAsync(Bitmap sourceImage, System.Drawing.Rectangle region, string language, PageSegMode psm)
        {
            using (var regionImage = CropImage(sourceImage, region))
            {
                if (regionImage == null) return string.Empty;
                return await GetTextAdaptiveAsync(regionImage, language, psm);
            }
        }

        public List<System.Drawing.Rectangle> FindTextRegions(Bitmap sourceImage)
        {
            if (_appSettings.TextDetectionMethod == TextDetectionMethod.None)
            {
                return new List<System.Drawing.Rectangle> { new System.Drawing.Rectangle(0, 0, sourceImage.Width, sourceImage.Height) };
            }

            if (_appSettings.TextDetectionMethod == TextDetectionMethod.East)
            {
                if (_eastNet == null || sourceImage == null)
                {
                    _logger.LogWarning("EAST modeli yüklenmedi, tam ekran taranacak.");
                    return new List<System.Drawing.Rectangle> { new System.Drawing.Rectangle(0, 0, sourceImage.Width, sourceImage.Height) };
                }

                return FindTextRegionsWithEast(sourceImage);
            }

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
                    _logger.LogWarning($"Resim boyutu ({src.Width}x{src.Height}) EAST modeli için çok küçük.");
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
                Mat denoised = new Mat();
                Cv2.MedianBlur(binary, denoised, 3);

                Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(3, 3));
                Cv2.MorphologyEx(denoised, denoised, MorphTypes.Open, kernel);
                Cv2.MorphologyEx(denoised, denoised, MorphTypes.Close, kernel);

                Cv2.FindContours(denoised, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                var regions = new List<System.Drawing.Rectangle>();
                foreach (var contour in contours)
                {
                    var cvRect = Cv2.BoundingRect(contour);
                    var rect = new System.Drawing.Rectangle(cvRect.X, cvRect.Y, cvRect.Width, cvRect.Height);

                    if (rect.Width > 20 && rect.Height > 10 &&
                        rect.Width < src.Width * 0.9 && rect.Height < src.Height * 0.9)
                    {
                        double aspectRatio = (double)rect.Width / rect.Height;
                        if (aspectRatio > 1.5 || aspectRatio < 0.67)
                        {
                            double area = Cv2.ContourArea(contour);
                            double boundingArea = rect.Width * rect.Height;
                            double solidity = area / boundingArea;

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
                double mean = Cv2.Mean(grayImage).Val0;
                double stdDev = CalculateStandardDeviation(grayImage);

                if (stdDev < 30)
                {
                    Cv2.Threshold(grayImage, binary, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);
                }
                else if (stdDev > 80)
                {
                    int blockSize = _appSettings.AdaptiveThresholdBlockSize;
                    int C = _appSettings.AdaptiveThresholdC;
                    if (blockSize % 2 == 0) blockSize++;
                    Cv2.AdaptiveThreshold(grayImage, binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, blockSize, C);
                }
                else
                {
                    Mat globalBinary = new Mat();
                    Mat adaptiveBinary = new Mat();

                    Cv2.Threshold(grayImage, globalBinary, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);

                    int blockSize = _appSettings.AdaptiveThresholdBlockSize;
                    int C = _appSettings.AdaptiveThresholdC;
                    if (blockSize % 2 == 0) blockSize++;

                    Cv2.AdaptiveThreshold(grayImage, adaptiveBinary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, blockSize, C);

                    Cv2.BitwiseOr(globalBinary, adaptiveBinary, binary);

                    globalBinary.Dispose();
                    adaptiveBinary.Dispose();
                }
            }
            else
            {
                int blockSize = _appSettings.AdaptiveThresholdBlockSize;
                int C = _appSettings.AdaptiveThresholdC;
                if (blockSize % 2 == 0) blockSize++;

                Cv2.AdaptiveThreshold(grayImage, binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, blockSize, C);
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
        //
        public Bitmap CropImage(Bitmap image, System.Drawing.Rectangle region) => image.Clone(region, image.PixelFormat);

        private Bitmap PreprocessImageForOcr(Bitmap originalImage, bool invertColors = false)
        {
            if (originalImage == null) return null;
            Bitmap processedImage = originalImage;
            try
            {
                // Renk inversiyonu
                if (invertColors)
                {
                    using (Mat src = BitmapConverter.ToMat(originalImage))
                    {
                        Mat inverted = new Mat();
                        Cv2.BitwiseNot(src, inverted);
                        processedImage = BitmapConverter.ToBitmap(inverted);
                        inverted.Dispose();
                    }
                }

                if (_appSettings.EnableSuperResolution && ShouldApplySuperResolution(processedImage))
                {
                    var tempImage = ApplySuperResolution(processedImage);
                    if (processedImage != originalImage) processedImage.Dispose();
                    processedImage = tempImage;
                }
                if (_appSettings.EnableSkewCorrection)
                {
                    float skewAngle = DetectSkewAngle(processedImage);
                    if (Math.Abs(skewAngle) > _appSettings.SkewCorrectionThreshold)
                    {
                        var correctedImage = CorrectSkew(processedImage, skewAngle);
                        if (processedImage != originalImage) processedImage.Dispose();
                        processedImage = correctedImage;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Görüntü ön işleme sırasında hata oluştu", ex);
                if (processedImage != originalImage) processedImage.Dispose();
                return new Bitmap(originalImage); 
            }
            return processedImage;
        }

        private bool ShouldApplySuperResolution(Bitmap image) => image.Width < _appSettings.MinImageSizeForSuperResolution || image.Height < _appSettings.MinImageSizeForSuperResolution;
        private Bitmap ApplySuperResolution(Bitmap image)
        {
            if (image == null) return null;
            using (Mat src = BitmapConverter.ToMat(image))
            {
                var upscaled = new Mat();
                Cv2.Resize(src, upscaled, new OpenCvSharp.Size(0, 0), _appSettings.SuperResolutionScale, _appSettings.SuperResolutionScale, InterpolationFlags.Cubic);
                Mat denoised = new Mat();
                Cv2.BilateralFilter(upscaled, denoised, 9, 75, 75);
                upscaled.Dispose();
                return BitmapConverter.ToBitmap(denoised);
            }
        }


        private float DetectSkewAngle(Bitmap image)
        {
            if (image == null) return 0f;
            using (Mat src = BitmapConverter.ToMat(image))
            using (Mat gray = src.CvtColor(ColorConversionCodes.BGR2GRAY))
            using (Mat binary = new Mat())
            {
                Cv2.Threshold(gray, binary, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);
                Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
                Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kernel);
                kernel.Dispose();
                Cv2.FindContours(binary, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                if (contours.Length == 0) return 0f;
                var largestContour = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
                var rect = Cv2.MinAreaRect(largestContour);
                float angle = rect.Angle;
                if (angle < -45) angle += 90;
                if (angle > 45) angle -= 90;
                return angle;
            }
        }

        private Bitmap CorrectSkew(Bitmap image, float angle)
        {
            if (image == null || Math.Abs(angle) < 0.1f) return image;

            using (Mat src = BitmapConverter.ToMat(image))
            {
                var center = new OpenCvSharp.Point2f(src.Width / 2.0f, src.Height / 2.0f);
                var rotationMatrix = Cv2.GetRotationMatrix2D(center, -angle, 1.0);

                var cos = Math.Abs(rotationMatrix.At<double>(0, 0));
                var sin = Math.Abs(rotationMatrix.At<double>(0, 1));
                var newWidth = (int)(src.Height * sin + src.Width * cos);
                var newHeight = (int)(src.Height * cos + src.Width * sin);

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
                _logger.LogError("Renk filtresi uygulanırken hata oluştu, orijinal görüntü döndürülüyor", ex);
                return sourceImage;
            }
        }

        private Bitmap AutoDetectAndIsolateTextByColor(Bitmap sourceImage)
        {
            using (Mat src = BitmapConverter.ToMat(sourceImage))
            using (Mat hsv = new Mat())
            {
                Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);

                Scalar[] textColors = DetectTextColors(hsv);

                if (textColors == null || textColors.Length == 0)
                {
                    _logger.LogWarning("Metin rengi bulunamadı, orijinal görüntü döndürülüyor");
                    return sourceImage;
                }

                using (Mat combinedMask = new Mat())
                {
                    bool firstMask = true;
                    for (int i = 0; i < textColors.Length; i++)
                    {
                        var color = textColors[i];
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

                    using (Mat result = new Mat())
                    {
                        Cv2.BitwiseAnd(src, src, result, combinedMask);
                        return BitmapConverter.ToBitmap(result);
                    }
                }
            }
        }

        public Scalar[] DetectTextColors(Mat hsvImage)
        {
            try
            {
                Scalar[] dominantColors = FindDominantColors(hsvImage);

                if (dominantColors == null || dominantColors.Length == 0)
                {
                    _logger.LogWarning("Dominant renk bulunamadı, varsayılan metin renkleri kullanılıyor");
                    return new Scalar[]
                    {
                        new Scalar(0, 0, 200),
                        new Scalar(30, 255, 255),
                        new Scalar(0, 0, 150)
                    };
                }

                var textColors = new List<Scalar>();
                foreach (var color in dominantColors)
                {
                    bool isTextColor = false;

                    if (color.Val2 > 120)
                    {
                        isTextColor = true;
                    }
                    else if (color.Val2 > 80 && color.Val1 > 50)
                    {
                        isTextColor = true;
                    }
                    else if (color.Val1 < 30 && color.Val2 > 100)
                    {
                        isTextColor = true;
                    }

                    if (isTextColor)
                    {
                        textColors.Add(color);
                        _logger.LogInformation($"Metin rengi tespit edildi: H={color.Val0:F1}, S={color.Val1:F1}, V={color.Val2:F1}");
                    }
                }

                if (textColors.Count == 0)
                {
                    _logger.LogWarning("Hiçbir metin rengi tespit edilemedi, varsayılan renkler kullanılıyor");
                    return new Scalar[]
                    {
                        new Scalar(0, 0, 200),
                        new Scalar(30, 255, 255)
                    };
                }

                return textColors.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError("Metin rengi algılama sırasında hata oluştu", ex);
                return new Scalar[]
                {
                    new Scalar(0, 0, 200),
                    new Scalar(30, 255, 255)
                };
            }
        }

        private Scalar[] FindDominantColors(Mat hsvImage, int k = 3)
        {
            try
            {
              
                Mat reshaped = hsvImage.Reshape(1, hsvImage.Rows * hsvImage.Cols);
                Mat floatData = new Mat();
                reshaped.ConvertTo(floatData, MatType.CV_32F);

               
                using (Mat labels = new Mat())
                using (Mat centers = new Mat())
                {
                    var criteria = new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 100, 0.2);
                    Cv2.Kmeans(floatData, k, labels, criteria, 3, KMeansFlags.PpCenters, centers);

                    
                    Scalar[] dominantColors = new Scalar[Math.Min(k, centers.Rows)];
                    for (int i = 0; i < dominantColors.Length; i++)
                    {
                        dominantColors[i] = new Scalar(
                            centers.At<float>(i, 0),
                            centers.At<float>(i, 1),
                            centers.At<float>(i, 2)
                        );
                    }

                    floatData.Dispose();
                    return dominantColors;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Dominant renk bulma sırasında hata oluştu", ex);
                return null;
            }
        }

        private Bitmap ManualColorIsolation(Bitmap sourceImage)
        {
            using (Mat src = BitmapConverter.ToMat(sourceImage))
            using (Mat hsv = new Mat())
            {
                Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);

                
                Scalar lower = new Scalar(
                    _appSettings.HueMin,
                    _appSettings.SaturationMin,
                    _appSettings.ValueMin
                );
                Scalar upper = new Scalar(
                    _appSettings.HueMax,
                    _appSettings.SaturationMax,
                    _appSettings.ValueMax
                );

                using (Mat mask = new Mat())
                {
                    Cv2.InRange(hsv, lower, upper, mask);

                    using (Mat result = new Mat())
                    {
                        Cv2.BitwiseAnd(src, src, result, mask);
                        return BitmapConverter.ToBitmap(result);
                    }
                }
            }
        }

        private Mat CreateContrastMask(Mat src)
        {
            using (Mat gray = src.CvtColor(ColorConversionCodes.BGR2GRAY))
            {
                Mat binary = new Mat();
                Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 11, 2);

                Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(2, 2));
                Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kernel);
                kernel.Dispose();

                return binary;
            }
        }

        private Mat CreateEdgeMask(Mat src)
        {
            using (Mat gray = src.CvtColor(ColorConversionCodes.BGR2GRAY))
            using (Mat blurred = new Mat())
            {
                Cv2.GaussianBlur(gray, blurred, new OpenCvSharp.Size(3, 3), 0);

                Mat edges = new Mat();
                Cv2.Canny(blurred, edges, 50, 150);

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

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                // Managed kaynakları temizle
                _eastNet?.Dispose();
                
                // OCR motorlarını temizle
                foreach (var engine in _ocrEngines.Values)
                {
                    if (engine is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
                _ocrEngines.Clear();
            }

            _disposed = true;
        }
    }
}