using OpenCvSharp;
using OpenCvSharp.Dnn;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Tesseract;

namespace GameTranslatorUltimate
{
    public class OcrService : IOcrService, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PrintWindow(
            IntPtr hWnd,
            IntPtr hdcBlt,
            uint nFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(
            IntPtr hWnd,
            out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private readonly ILogger _logger;
        private readonly AppSettings _appSettings;
        private readonly Dictionary<OcrEngineType, IOcrEngine> _ocrEngines;
        private readonly SemaphoreSlim _eastLock;

        private readonly Net _eastNet;
        private readonly string _eastModelPath;

        private readonly object _regionCacheLock = new object();
        private List<System.Drawing.Rectangle> _cachedRegions;
        private long _cachedRegionsTicks;
        private int _cachedWidth;
        private int _cachedHeight;
        private TextDetectionMethod _cachedMethod;
        private const int RegionCacheMs = 850;

        private int _disposed;

        public OcrService(
            ILogger logger,
            AppSettings appSettings)
        {
            _logger =
                logger ?? throw new ArgumentNullException(nameof(logger));

            _appSettings =
                appSettings ?? throw new ArgumentNullException(nameof(appSettings));

            _eastLock =
                new SemaphoreSlim(1, 1);

            _ocrEngines =
                new Dictionary<OcrEngineType, IOcrEngine>
                {
                    {
                        OcrEngineType.Tesseract,
                        new TesseractOcrEngine(
                            logger,
                            appSettings)
                    },
                    {
                        OcrEngineType.WindowsOcr,
                        new WindowsOcrEngine(logger)
                    }
                };

            _eastModelPath =
                Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "frozen_east_text_detection.pb");

            if (File.Exists(_eastModelPath))
            {
                try
                {
                    _eastNet =
                        CvDnn.ReadNet(
                            _eastModelPath);

                    _logger.LogInformation(
                        "EAST metin algılama modeli yüklendi.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        $"EAST modeli yüklenemedi: {_eastModelPath}",
                        ex);

                    _eastNet = null;
                }
            }
            else
            {
                _logger.LogWarning(
                    $"EAST modeli bulunamadı: {_eastModelPath}");

                _eastNet = null;
            }
        }

        public async Task<string> GetTextFromImage(
            Bitmap image,
            string language,
            bool invertColors = false)
        {
            ThrowIfDisposed();

            if (image == null)
                return string.Empty;

            using (Bitmap processed =
                   PreprocessImageForOcr(
                       image,
                       invertColors))
            {
                if (processed == null)
                    return string.Empty;

                return await RecognizePreparedImageAsync(
                        processed,
                        language)
                    .ConfigureAwait(false);
            }
        }

        public async Task<string> GetTextAdaptiveAsync(
            Bitmap image,
            string language)
        {
            ThrowIfDisposed();

            if (image == null)
                return string.Empty;

            IOcrEngine engine;

            if (!_ocrEngines.TryGetValue(
                    _appSettings.OcrEngine,
                    out engine))
            {
                _logger.LogError(
                    $"Seçilen OCR motoru bulunamadı: {_appSettings.OcrEngine}");

                return string.Empty;
            }

            PageSegMode psm =
                (PageSegMode)_appSettings.SelectedTesseractPageSegMode;

            string result =
                await engine
                    .RecognizeTextAsync(
                        image,
                        language,
                        psm)
                    .ConfigureAwait(false);

            return OcrTextCorrector.CorrectText(
                result,
                language,
                true,
                _logger);
        }

        public async Task<string> RecognizeTextInRegionsAsync(
            Bitmap image,
            string language)
        {
            ThrowIfDisposed();

            if (image == null)
                return string.Empty;

            using (Bitmap processed =
                   PreprocessImageForOcr(image))
            {
                if (processed == null)
                    return string.Empty;

                return await RecognizePreparedImageAsync(
                        processed,
                        language)
                    .ConfigureAwait(false);
            }
        }

        private async Task<string> RecognizePreparedImageAsync(
            Bitmap processedImage,
            string language)
        {
            if (processedImage == null)
                return string.Empty;

            if (_appSettings.TextDetectionMethod ==
                TextDetectionMethod.None)
            {
                return await GetTextAdaptiveAsync(
                        processedImage,
                        language)
                    .ConfigureAwait(false);
            }

            List<System.Drawing.Rectangle> regions =
                FindTextRegions(
                    processedImage);

            if (regions == null ||
                regions.Count == 0)
            {
                return await GetTextAdaptiveAsync(
                        processedImage,
                        language)
                    .ConfigureAwait(false);
            }

            regions =
                OrderAndMergeRegions(
                    regions,
                    processedImage.Width,
                    processedImage.Height);

            if (regions.Count == 0)
            {
                return await GetTextAdaptiveAsync(
                        processedImage,
                        language)
                    .ConfigureAwait(false);
            }

            var recognizedTexts =
                new List<string>();

            foreach (System.Drawing.Rectangle region in regions)
            {
                string text =
                    await RecognizeTextInSingleRegionAsync(
                            processedImage,
                            region,
                            language)
                        .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    recognizedTexts.Add(text);
                }
            }

            if (recognizedTexts.Count == 0)
            {
                return await GetTextAdaptiveAsync(
                        processedImage,
                        language)
                    .ConfigureAwait(false);
            }

            return OcrTextCorrector.CorrectText(
                string.Join(" ", recognizedTexts),
                language,
                true,
                _logger);
        }

        private async Task<string> RecognizeTextInSingleRegionAsync(
            Bitmap sourceImage,
            System.Drawing.Rectangle region,
            string language)
        {
            using (Bitmap regionImage =
                   CropImage(
                       sourceImage,
                       region))
            {
                if (regionImage == null)
                    return string.Empty;

                int padding =
                    Math.Max(
                        6,
                        Math.Min(
                            24,
                            regionImage.Height / 5));

                using (Bitmap paddedImage =
                       AddPaddingToBitmap(
                           regionImage,
                           padding))
                {
                    if (paddedImage == null)
                        return string.Empty;

                    return await GetTextAdaptiveAsync(
                            paddedImage,
                            language)
                        .ConfigureAwait(false);
                }
            }
        }

        private Bitmap AddPaddingToBitmap(
            Bitmap original,
            int paddingSize)
        {
            if (original == null)
                return null;

            if (paddingSize < 0)
                paddingSize = 0;

            var padded =
                new Bitmap(
                    original.Width + paddingSize * 2,
                    original.Height + paddingSize * 2,
                    System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            using (Graphics graphics =
                   Graphics.FromImage(padded))
            {
                graphics.Clear(Color.White);

                graphics.DrawImageUnscaled(
                    original,
                    paddingSize,
                    paddingSize);
            }

            return padded;
        }

        public List<System.Drawing.Rectangle> FindTextRegions(
            Bitmap sourceImage)
        {
            ThrowIfDisposed();

            if (sourceImage == null)
            {
                return new List<System.Drawing.Rectangle>();
            }

            if (_appSettings.TextDetectionMethod ==
                TextDetectionMethod.None)
            {
                return FullImageRegion(
                    sourceImage);
            }

            lock (_regionCacheLock)
            {
                if (_cachedRegions != null && _cachedWidth == sourceImage.Width && _cachedHeight == sourceImage.Height && _cachedMethod == _appSettings.TextDetectionMethod)
                {
                    long elapsedMs = (Stopwatch.GetTimestamp() - _cachedRegionsTicks) * 1000 / Stopwatch.Frequency;
                    if (elapsedMs < RegionCacheMs)
                    {
                        return new List<System.Drawing.Rectangle>(_cachedRegions);
                    }
                }
            }

            List<System.Drawing.Rectangle> result;
            if (_appSettings.TextDetectionMethod ==
                TextDetectionMethod.East)
            {
                if (_eastNet == null)
                {
                    result = FullImageRegion(sourceImage);
                }
                else
                {
                    result = FindTextRegionsWithEast(sourceImage);
                }
            }
            else if (_appSettings.TextDetectionMethod ==
                TextDetectionMethod.OpenCV)
            {
                result = FindTextRegionsWithOpenCV(sourceImage);
            }
            else
            {
                result = FullImageRegion(sourceImage);
            }

            lock (_regionCacheLock)
            {
                _cachedRegions = new List<System.Drawing.Rectangle>(result);
                _cachedWidth = sourceImage.Width;
                _cachedHeight = sourceImage.Height;
                _cachedMethod = _appSettings.TextDetectionMethod;
                _cachedRegionsTicks = Stopwatch.GetTimestamp();
            }
            return result;
        }

        private List<System.Drawing.Rectangle> FindTextRegionsWithEast(
            Bitmap sourceImage)
        {
            if (sourceImage == null)
                return new List<System.Drawing.Rectangle>();

            if (_eastNet == null)
            {
                return FullImageRegion(
                    sourceImage);
            }

            try
            {
                using (Mat src =
                       BitmapConverter.ToMat(sourceImage))
                {
                    int newWidth =
                        (src.Width / 32) * 32;

                    int newHeight =
                        (src.Height / 32) * 32;

                    if (newWidth < 32)
                        newWidth = 32;

                    if (newHeight < 32)
                        newHeight = 32;

                    double ratioWidth =
                        (double)src.Width /
                        newWidth;

                    double ratioHeight =
                        (double)src.Height /
                        newHeight;

                    using (Mat blob =
                           CvDnn.BlobFromImage(
                               src,
                               1.0,
                               new OpenCvSharp.Size(
                                   newWidth,
                                   newHeight),
                               new Scalar(
                                   123.68,
                                   116.78,
                                   103.94),
                               true,
                               false))
                    {
                        Mat[] output =
                        {
                            new Mat(),
                            new Mat()
                        };

                        try
                        {
                            _eastLock.Wait();

                            try
                            {
                                _eastNet.SetInput(blob);

                                string[] outputNames =
                                {
                                    "feature_fusion/Conv_7/Sigmoid",
                                    "feature_fusion/concat_3"
                                };

                                _eastNet.Forward(
                                    output,
                                    outputNames);
                            }
                            finally
                            {
                                _eastLock.Release();
                            }

                            List<RotatedRect> boxes;
                            List<float> confidences;

                            Decode(
                                output[0],
                                output[1],
                                0.50f,
                                out boxes,
                                out confidences);

                            if (boxes.Count == 0)
                            {
                                return new List<System.Drawing.Rectangle>();
                            }

                            int[] indices;

                            CvDnn.NMSBoxes(
                                boxes,
                                confidences,
                                0.50f,
                                0.40f,
                                out indices);

                            var regions =
                                new List<System.Drawing.Rectangle>();

                            foreach (int index in indices)
                            {
                                if (index < 0 ||
                                    index >= boxes.Count)
                                {
                                    continue;
                                }

                                RotatedRect box =
                                    boxes[index];

                                Point2f[] vertices =
                                    box.Points();

                                for (int i = 0;
                                     i < vertices.Length;
                                     i++)
                                {
                                    vertices[i].X =
                                        vertices[i].X *
                                        (float)ratioWidth;

                                    vertices[i].Y =
                                        vertices[i].Y *
                                        (float)ratioHeight;
                                }

                                OpenCvSharp.Rect cvRect =
                                    Cv2.BoundingRect(
                                        vertices);

                                System.Drawing.Rectangle rect =
                                    ExpandAndClampRegion(
                                        new System.Drawing.Rectangle(
                                            cvRect.X,
                                            cvRect.Y,
                                            cvRect.Width,
                                            cvRect.Height),
                                        sourceImage.Width,
                                        sourceImage.Height,
                                        Math.Max(
                                            4,
                                            cvRect.Height / 6));

                                if (IsValidTextRegion(rect))
                                {
                                    regions.Add(rect);
                                }
                            }

                            return RemoveDuplicateRegions(
                                regions);
                        }
                        finally
                        {
                            for (int i = 0;
                                 i < output.Length;
                                 i++)
                            {
                                if (output[i] != null)
                                {
                                    output[i].Dispose();
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"EAST metin algılama başarısız oldu: {ex.Message}");

                return FullImageRegion(
                    sourceImage);
            }
        }

        private List<System.Drawing.Rectangle> FindTextRegionsWithOpenCV(
            Bitmap sourceImage)
        {
            if (sourceImage == null)
            {
                return new List<System.Drawing.Rectangle>();
            }

            try
            {
                using (Mat src =
                       BitmapConverter.ToMat(sourceImage))
                using (Mat gray =
                       ConvertToGray(src))
                using (Mat binary =
                       ApplyDynamicThresholding(gray))
                using (Mat denoised =
                       new Mat())
                using (Mat kernel =
                       Cv2.GetStructuringElement(
                           MorphShapes.Rect,
                           new OpenCvSharp.Size(15, 3)))
                {
                    Cv2.MedianBlur(
                        binary,
                        denoised,
                        3);

                    Cv2.MorphologyEx(
                        denoised,
                        denoised,
                        MorphTypes.Close,
                        kernel);

                    OpenCvSharp.Point[][] contours;
                    HierarchyIndex[] hierarchy;

                    Cv2.FindContours(
                        denoised,
                        out contours,
                        out hierarchy,
                        RetrievalModes.External,
                        ContourApproximationModes.ApproxSimple);

                    var regions =
                        new List<System.Drawing.Rectangle>();

                    if (contours == null)
                        return regions;

                    foreach (OpenCvSharp.Point[] contour in contours)
                    {
                        if (contour == null ||
                            contour.Length == 0)
                        {
                            continue;
                        }

                        OpenCvSharp.Rect cvRect =
                            Cv2.BoundingRect(
                                contour);

                        if (cvRect.Width < 12 ||
                            cvRect.Height < 6)
                        {
                            continue;
                        }

                        if (cvRect.Width >=
                                src.Width * 0.98 &&
                            cvRect.Height >=
                                src.Height * 0.98)
                        {
                            continue;
                        }

                        System.Drawing.Rectangle rect =
                            ExpandAndClampRegion(
                                new System.Drawing.Rectangle(
                                    cvRect.X,
                                    cvRect.Y,
                                    cvRect.Width,
                                    cvRect.Height),
                                sourceImage.Width,
                                sourceImage.Height,
                                Math.Max(
                                    3,
                                    cvRect.Height / 8));

                        if (IsValidTextRegion(rect))
                        {
                            regions.Add(rect);
                        }
                    }

                    return RemoveDuplicateRegions(
                        regions);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"OpenCV metin algılama başarısız oldu: {ex.Message}");

                return FullImageRegion(
                    sourceImage);
            }
        }

        private Mat ApplyDynamicThresholding(
            Mat grayImage)
        {
            var binary =
                new Mat();

            int blockSize =
                NormalizeAdaptiveBlockSize(
                    _appSettings.AdaptiveThresholdBlockSize);

            int thresholdC =
                _appSettings.AdaptiveThresholdC;

            if (!_appSettings.EnableDynamicThresholding)
            {
                Cv2.AdaptiveThreshold(
                    grayImage,
                    binary,
                    255,
                    AdaptiveThresholdTypes.GaussianC,
                    ThresholdTypes.Binary,
                    blockSize,
                    thresholdC);

                NormalizePolarity(
                    binary);

                return binary;
            }

            double stdDev =
                CalculateStandardDeviation(
                    grayImage);

            if (stdDev < 30)
            {
                Cv2.Threshold(
                    grayImage,
                    binary,
                    0,
                    255,
                    ThresholdTypes.Binary |
                    ThresholdTypes.Otsu);
            }
            else if (stdDev > 80)
            {
                Cv2.AdaptiveThreshold(
                    grayImage,
                    binary,
                    255,
                    AdaptiveThresholdTypes.GaussianC,
                    ThresholdTypes.Binary,
                    blockSize,
                    thresholdC);
            }
            else
            {
                using (Mat globalBinary =
                       new Mat())
                using (Mat adaptiveBinary =
                       new Mat())
                {
                    Cv2.Threshold(
                        grayImage,
                        globalBinary,
                        0,
                        255,
                        ThresholdTypes.Binary |
                        ThresholdTypes.Otsu);

                    Cv2.AdaptiveThreshold(
                        grayImage,
                        adaptiveBinary,
                        255,
                        AdaptiveThresholdTypes.GaussianC,
                        ThresholdTypes.Binary,
                        blockSize,
                        thresholdC);

                    Cv2.BitwiseAnd(
                        globalBinary,
                        adaptiveBinary,
                        binary);
                }
            }

            NormalizePolarity(
                binary);

            return binary;
        }

        private static int NormalizeAdaptiveBlockSize(
            int blockSize)
        {
            if (blockSize < 3)
                blockSize = 3;

            if (blockSize % 2 == 0)
                blockSize++;

            if (blockSize > 99)
                blockSize = 99;

            return blockSize;
        }

        private static double CalculateStandardDeviation(
            Mat grayImage)
        {
            Mat mean =
                new Mat();

            Mat stdDev =
                new Mat();

            try
            {
                Cv2.MeanStdDev(
                    grayImage,
                    mean,
                    stdDev);

                return stdDev.Get<double>(
                    0,
                    0);
            }
            finally
            {
                mean.Dispose();
                stdDev.Dispose();
            }
        }

        private void Decode(
            Mat scores,
            Mat geometry,
            float confidenceThreshold,
            out List<RotatedRect> boxes,
            out List<float> confidences)
        {
            boxes =
                new List<RotatedRect>();

            confidences =
                new List<float>();

            if (scores == null ||
                geometry == null ||
                scores.Empty() ||
                geometry.Empty())
            {
                return;
            }

            int height =
                scores.Size(2);

            int width =
                scores.Size(3);

            for (int y = 0;
                 y < height;
                 y++)
            {
                for (int x = 0;
                     x < width;
                     x++)
                {
                    float score =
                        scores.At<float>(
                            0,
                            0,
                            y,
                            x);

                    if (score <
                        confidenceThreshold)
                    {
                        continue;
                    }

                    float offsetX =
                        x * 4.0f;

                    float offsetY =
                        y * 4.0f;

                    float angle =
                        geometry.At<float>(
                            0,
                            4,
                            y,
                            x);

                    float top =
                        geometry.At<float>(
                            0,
                            0,
                            y,
                            x);

                    float right =
                        geometry.At<float>(
                            0,
                            1,
                            y,
                            x);

                    float bottom =
                        geometry.At<float>(
                            0,
                            2,
                            y,
                            x);

                    float left =
                        geometry.At<float>(
                            0,
                            3,
                            y,
                            x);

                    float widthValue =
                        right + left;

                    float heightValue =
                        top + bottom;

                    float cos =
                        (float)Math.Cos(
                            angle);

                    float sin =
                        (float)Math.Sin(
                            angle);

                    float endX =
                        offsetX +
                        cos * right +
                        sin * bottom;

                    float endY =
                        offsetY -
                        sin * right +
                        cos * bottom;

                    float centerX =
                        endX -
                        widthValue / 2.0f;

                    float centerY =
                        endY -
                        heightValue / 2.0f;

                    var center =
                        new Point2f(
                            centerX,
                            centerY);

                    var size =
                        new Size2f(
                            widthValue,
                            heightValue);

                    boxes.Add(
                        new RotatedRect(
                            center,
                            size,
                            -angle *
                            180.0f /
                            (float)Math.PI));

                    confidences.Add(
                        score);
                }
            }
        }

        public Bitmap CaptureWindow(
            IntPtr hWnd)
        {
            ThrowIfDisposed();

            if (hWnd == IntPtr.Zero)
                return null;

            RECT rect;

            if (!GetWindowRect(
                    hWnd,
                    out rect))
            {
                return null;
            }

            int width =
                rect.Right -
                rect.Left;

            int height =
                rect.Bottom -
                rect.Top;

            if (width <= 0 ||
                height <= 0)
            {
                return null;
            }

            var bitmap =
                new Bitmap(
                    width,
                    height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                using (Graphics graphics =
                       Graphics.FromImage(bitmap))
                {
                    IntPtr hdc =
                        IntPtr.Zero;

                    try
                    {
                        hdc =
                            graphics.GetHdc();

                        bool success =
                            PrintWindow(
                                hWnd,
                                hdc,
                                2);

                        if (!success)
                        {
                            success =
                                PrintWindow(
                                    hWnd,
                                    hdc,
                                    0);
                        }

                        if (!success)
                        {
                            bitmap.Dispose();
                            return null;
                        }
                    }
                    finally
                    {
                        if (hdc != IntPtr.Zero)
                        {
                            graphics.ReleaseHdc(
                                hdc);
                        }
                    }
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                bitmap.Dispose();

                _logger.LogError(
                    "Pencere görüntüsü alınamadı.",
                    ex);

                return null;
            }
        }

        public Bitmap CropImage(
            Bitmap image,
            System.Drawing.Rectangle region)
        {
            ThrowIfDisposed();

            if (image == null)
                return null;

            System.Drawing.Rectangle bounds =
                new System.Drawing.Rectangle(
                    0,
                    0,
                    image.Width,
                    image.Height);

            System.Drawing.Rectangle safeRegion =
                System.Drawing.Rectangle.Intersect(
                    bounds,
                    region);

            if (safeRegion.Width <= 0 ||
                safeRegion.Height <= 0)
            {
                return null;
            }

            try
            {
                return image.Clone(
                    safeRegion,
                    image.PixelFormat);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Görüntü kırpılamadı: {ex.Message}");

                return null;
            }
        }

        private Bitmap PreprocessImageForOcr(
            Bitmap originalImage,
            bool invertColors = false)
        {
            if (originalImage == null)
                return null;

            Bitmap current =
                new Bitmap(originalImage);

            try
            {
                if (invertColors)
                {
                    Bitmap inverted =
                        InvertBitmap(
                            current);

                    current.Dispose();

                    current =
                        inverted;
                }

                if (_appSettings.EnableSuperResolution &&
                    ShouldApplySuperResolution(current))
                {
                    Bitmap upscaled =
                        ApplySuperResolution(
                            current);

                    if (upscaled != null)
                    {
                        current.Dispose();

                        current =
                            upscaled;
                    }
                }

                if (_appSettings.EnableSkewCorrection)
                {
                    float angle =
                        DetectSkewAngle(
                            current);

                    if (Math.Abs(angle) >
                        _appSettings.SkewCorrectionThreshold)
                    {
                        Bitmap corrected =
                            CorrectSkew(
                                current,
                                angle);

                        if (corrected != null)
                        {
                            current.Dispose();

                            current =
                                corrected;
                        }
                    }
                }

                return current;
            }
            catch (Exception ex)
            {
                if (current != null)
                {
                    current.Dispose();
                }

                _logger.LogError(
                    "Görüntü ön işleme sırasında hata oluştu.",
                    ex);

                return new Bitmap(
                    originalImage);
            }
        }

        private static Bitmap InvertBitmap(
            Bitmap image)
        {
            using (Mat source =
                   BitmapConverter.ToMat(image))
            using (Mat inverted =
                   new Mat())
            {
                Cv2.BitwiseNot(
                    source,
                    inverted);

                return BitmapConverter.ToBitmap(
                    inverted);
            }
        }

        private bool ShouldApplySuperResolution(
            Bitmap image)
        {
            if (image == null)
                return false;

            return
                image.Width <
                _appSettings.MinImageSizeForSuperResolution ||
                image.Height <
                _appSettings.MinImageSizeForSuperResolution;
        }

        private Bitmap ApplySuperResolution(
            Bitmap image)
        {
            if (image == null)
                return null;

            double scale =
                _appSettings.SuperResolutionScale;

            if (scale <= 1.0)
                scale = 2.0;

            if (scale > 4.0)
                scale = 4.0;

            using (Mat src =
                   BitmapConverter.ToMat(image))
            using (Mat upscaled =
                   new Mat())
            using (Mat denoised =
                   new Mat())
            {
                Cv2.Resize(
                    src,
                    upscaled,
                    new OpenCvSharp.Size(0, 0),
                    scale,
                    scale,
                    InterpolationFlags.Cubic);

                Cv2.BilateralFilter(
                    upscaled,
                    denoised,
                    7,
                    50,
                    50);

                return BitmapConverter.ToBitmap(
                    denoised);
            }
        }

        private float DetectSkewAngle(
            Bitmap image)
        {
            if (image == null)
                return 0;

            try
            {
                using (Mat src =
                       BitmapConverter.ToMat(image))
                using (Mat gray =
                       ConvertToGray(src))
                using (Mat binary =
                       new Mat())
                using (Mat kernel =
                       Cv2.GetStructuringElement(
                           MorphShapes.Rect,
                           new OpenCvSharp.Size(5, 3)))
                {
                    Cv2.Threshold(
                        gray,
                        binary,
                        0,
                        255,
                        ThresholdTypes.Binary |
                        ThresholdTypes.Otsu);

                    Cv2.MorphologyEx(
                        binary,
                        binary,
                        MorphTypes.Close,
                        kernel);

                    OpenCvSharp.Point[][] contours;
                    HierarchyIndex[] hierarchy;

                    Cv2.FindContours(
                        binary,
                        out contours,
                        out hierarchy,
                        RetrievalModes.External,
                        ContourApproximationModes.ApproxSimple);

                    if (contours == null ||
                        contours.Length == 0)
                    {
                        return 0;
                    }

                    OpenCvSharp.Point[] largest =
                        contours
                            .OrderByDescending(
                                contour =>
                                    Cv2.ContourArea(contour))
                            .First();

                    RotatedRect rect =
                        Cv2.MinAreaRect(
                            largest);

                    float angle =
                        rect.Angle;

                    if (angle < -45)
                        angle += 90;

                    if (angle > 45)
                        angle -= 90;

                    return angle;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Eğiklik açısı algılanamadı: {ex.Message}");

                return 0;
            }
        }

        private Bitmap CorrectSkew(
            Bitmap image,
            float angle)
        {
            if (image == null)
                return null;

            if (Math.Abs(angle) <
                0.1f)
            {
                return new Bitmap(
                    image);
            }

            using (Mat src =
                   BitmapConverter.ToMat(image))
            using (Mat rotationMatrix =
                   Cv2.GetRotationMatrix2D(
                       new Point2f(
                           src.Width / 2.0f,
                           src.Height / 2.0f),
                       -angle,
                       1.0))
            using (Mat rotated =
                   new Mat())
            {
                Point2f center =
                    new Point2f(
                        src.Width / 2.0f,
                        src.Height / 2.0f);

                double cos =
                    Math.Abs(
                        rotationMatrix.At<double>(
                            0,
                            0));

                double sin =
                    Math.Abs(
                        rotationMatrix.At<double>(
                            0,
                            1));

                int newWidth =
                    (int)(
                        src.Height * sin +
                        src.Width * cos);

                int newHeight =
                    (int)(
                        src.Height * cos +
                        src.Width * sin);

                rotationMatrix.Set(
                    0,
                    2,
                    rotationMatrix.At<double>(
                        0,
                        2) +
                    newWidth / 2.0 -
                    center.X);

                rotationMatrix.Set(
                    1,
                    2,
                    rotationMatrix.At<double>(
                        1,
                        2) +
                    newHeight / 2.0 -
                    center.Y);

                Cv2.WarpAffine(
                    src,
                    rotated,
                    rotationMatrix,
                    new OpenCvSharp.Size(
                        newWidth,
                        newHeight),
                    InterpolationFlags.Cubic,
                    BorderTypes.Constant,
                    Scalar.White);

                return BitmapConverter.ToBitmap(
                    rotated);
            }
        }

        public Bitmap IsolateTextByColor(
            Bitmap sourceImage)
        {
            ThrowIfDisposed();

            if (sourceImage == null)
                return null;

            try
            {
                if (_appSettings.EnableAutoColorDetection)
                {
                    return AutoDetectAndIsolateTextByColor(
                        sourceImage);
                }

                return ManualColorIsolation(
                    sourceImage);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Renk filtresi uygulanamadı.",
                    ex);

                return new Bitmap(
                    sourceImage);
            }
        }

        private Bitmap AutoDetectAndIsolateTextByColor(
            Bitmap sourceImage)
        {
            using (Mat src =
                   BitmapConverter.ToMat(sourceImage))
            using (Mat bgr =
                   EnsureBgr(src))
            using (Mat hsv =
                   new Mat())
            {
                Cv2.CvtColor(
                    bgr,
                    hsv,
                    ColorConversionCodes.BGR2HSV);

                Scalar[] colors =
                    DetectTextColors(
                        hsv);

                if (colors == null ||
                    colors.Length == 0)
                {
                    return new Bitmap(
                        sourceImage);
                }

                Mat combinedMask =
                    new Mat();

                try
                {
                    bool initialized =
                        false;

                    for (int i = 0;
                         i < colors.Length;
                         i++)
                    {
                        Scalar color =
                            colors[i];

                        Scalar lower =
                            new Scalar(
                                Math.Max(
                                    0,
                                    color.Val0 - 35),
                                Math.Max(
                                    0,
                                    color.Val1 - 90),
                                Math.Max(
                                    0,
                                    color.Val2 - 90));

                        Scalar upper =
                            new Scalar(
                                Math.Min(
                                    180,
                                    color.Val0 + 35),
                                Math.Min(
                                    255,
                                    color.Val1 + 90),
                                Math.Min(
                                    255,
                                    color.Val2 + 90));

                        using (Mat mask =
                               new Mat())
                        {
                            Cv2.InRange(
                                hsv,
                                lower,
                                upper,
                                mask);

                            if (!initialized)
                            {
                                mask.CopyTo(
                                    combinedMask);

                                initialized =
                                    true;
                            }
                            else
                            {
                                Cv2.BitwiseOr(
                                    combinedMask,
                                    mask,
                                    combinedMask);
                            }
                        }
                    }

                    if (!initialized ||
                        combinedMask.Empty())
                    {
                        return new Bitmap(
                            sourceImage);
                    }

                    using (Mat inverted =
                           new Mat())
                    using (Mat result =
                           new Mat())
                    {
                        Cv2.BitwiseNot(
                            combinedMask,
                            inverted);

                        Cv2.CvtColor(
                            inverted,
                            result,
                            ColorConversionCodes.GRAY2BGR);

                        return BitmapConverter.ToBitmap(
                            result);
                    }
                }
                finally
                {
                    combinedMask.Dispose();
                }
            }
        }

        public Scalar[] DetectTextColors(
            Mat hsvImage)
        {
            if (hsvImage == null ||
                hsvImage.Empty())
            {
                return new Scalar[0];
            }

            try
            {
                Scalar[] dominantColors =
                    FindDominantColors(
                        hsvImage);

                if (dominantColors == null ||
                    dominantColors.Length == 0)
                {
                    return GetDefaultTextColors();
                }

                var result =
                    new List<Scalar>();

                for (int i = 0;
                     i < dominantColors.Length;
                     i++)
                {
                    if (IsLikelyTextColor(
                        dominantColors[i]))
                    {
                        result.Add(
                            dominantColors[i]);
                    }
                }

                if (result.Count == 0)
                {
                    return GetDefaultTextColors();
                }

                return result.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Metin rengi algılanamadı: {ex.Message}");

                return GetDefaultTextColors();
            }
        }

        private static bool IsLikelyTextColor(
            Scalar color)
        {
            if (color.Val2 >= 150)
                return true;

            if (color.Val2 >= 95 &&
                color.Val1 >= 45)
            {
                return true;
            }

            if (color.Val1 <= 35 &&
                color.Val2 >= 110)
            {
                return true;
            }

            return false;
        }

        private static Scalar[] GetDefaultTextColors()
        {
            return new Scalar[]
            {
                new Scalar(
                    0,
                    0,
                    220),

                new Scalar(
                    30,
                    220,
                    240)
            };
        }

        private Scalar[] FindDominantColors(
            Mat hsvImage,
            int k = 3)
        {
            if (hsvImage == null ||
                hsvImage.Empty())
            {
                return null;
            }

            if (k < 1)
                k = 1;

            if (k > 5)
                k = 5;

            Mat reshaped =
                null;

            try
            {
                reshaped =
                    hsvImage.Reshape(
                        1,
                        hsvImage.Rows *
                        hsvImage.Cols);

                using (Mat floatData =
                       new Mat())
                using (Mat labels =
                       new Mat())
                using (Mat centers =
                       new Mat())
                {
                    reshaped.ConvertTo(
                        floatData,
                        MatType.CV_32F);

                    var criteria =
                        new TermCriteria(
                            CriteriaTypes.Eps |
                            CriteriaTypes.MaxIter,
                            50,
                            0.5);

                    Cv2.Kmeans(
                        floatData,
                        k,
                        labels,
                        criteria,
                        2,
                        KMeansFlags.PpCenters,
                        centers);

                    int count =
                        Math.Min(
                            k,
                            centers.Rows);

                    var colors =
                        new Scalar[count];

                    for (int i = 0;
                         i < count;
                         i++)
                    {
                        colors[i] =
                            new Scalar(
                                centers.At<float>(
                                    i,
                                    0),
                                centers.At<float>(
                                    i,
                                    1),
                                centers.At<float>(
                                    i,
                                    2));
                    }

                    return colors;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Dominant renkler bulunamadı: {ex.Message}");

                return null;
            }
            finally
            {
                if (reshaped != null)
                {
                    reshaped.Dispose();
                }
            }
        }

        private Bitmap ManualColorIsolation(
            Bitmap sourceImage)
        {
            using (Mat src =
                   BitmapConverter.ToMat(sourceImage))
            using (Mat bgr =
                   EnsureBgr(src))
            using (Mat hsv =
                   new Mat())
            using (Mat mask =
                   new Mat())
            using (Mat inverted =
                   new Mat())
            using (Mat result =
                   new Mat())
            {
                Cv2.CvtColor(
                    bgr,
                    hsv,
                    ColorConversionCodes.BGR2HSV);

                Scalar lower =
                    new Scalar(
                        Clamp(
                            _appSettings.HueMin,
                            0,
                            180),
                        Clamp(
                            _appSettings.SaturationMin,
                            0,
                            255),
                        Clamp(
                            _appSettings.ValueMin,
                            0,
                            255));

                Scalar upper =
                    new Scalar(
                        Clamp(
                            _appSettings.HueMax,
                            0,
                            180),
                        Clamp(
                            _appSettings.SaturationMax,
                            0,
                            255),
                        Clamp(
                            _appSettings.ValueMax,
                            0,
                            255));

                Cv2.InRange(
                    hsv,
                    lower,
                    upper,
                    mask);

                Cv2.BitwiseNot(
                    mask,
                    inverted);

                Cv2.CvtColor(
                    inverted,
                    result,
                    ColorConversionCodes.GRAY2BGR);

                return BitmapConverter.ToBitmap(
                    result);
            }
        }

        private Mat CreateContrastMask(
            Mat src)
        {
            if (src == null ||
                src.Empty())
            {
                return new Mat();
            }

            using (Mat gray =
                   ConvertToGray(src))
            using (Mat kernel =
                   Cv2.GetStructuringElement(
                       MorphShapes.Rect,
                       new OpenCvSharp.Size(2, 2)))
            {
                var binary =
                    new Mat();

                Cv2.AdaptiveThreshold(
                    gray,
                    binary,
                    255,
                    AdaptiveThresholdTypes.GaussianC,
                    ThresholdTypes.Binary,
                    11,
                    2);

                Cv2.MorphologyEx(
                    binary,
                    binary,
                    MorphTypes.Open,
                    kernel);

                return binary;
            }
        }

        private Mat CreateEdgeMask(
            Mat src)
        {
            if (src == null ||
                src.Empty())
            {
                return new Mat();
            }

            using (Mat gray =
                   ConvertToGray(src))
            using (Mat blurred =
                   new Mat())
            using (Mat kernel =
                   Cv2.GetStructuringElement(
                       MorphShapes.Rect,
                       new OpenCvSharp.Size(2, 2)))
            {
                Cv2.GaussianBlur(
                    gray,
                    blurred,
                    new OpenCvSharp.Size(3, 3),
                    0);

                var edges =
                    new Mat();

                Cv2.Canny(
                    blurred,
                    edges,
                    50,
                    150);

                Cv2.Dilate(
                    edges,
                    edges,
                    kernel);

                return edges;
            }
        }

        Mat IOcrService.CreateEdgeMask(
            Mat imageMat)
        {
            return CreateEdgeMask(
                imageMat);
        }

        Mat IOcrService.CreateContrastMask(
            Mat imageMat)
        {
            return CreateContrastMask(
                imageMat);
        }

        private static Mat ConvertToGray(
            Mat source)
        {
            var gray =
                new Mat();

            if (source == null ||
                source.Empty())
            {
                return gray;
            }

            int channels =
                source.Channels();

            if (channels == 1)
            {
                source.CopyTo(
                    gray);

                return gray;
            }

            if (channels == 4)
            {
                Cv2.CvtColor(
                    source,
                    gray,
                    ColorConversionCodes.BGRA2GRAY);

                return gray;
            }

            Cv2.CvtColor(
                source,
                gray,
                ColorConversionCodes.BGR2GRAY);

            return gray;
        }

        private static Mat EnsureBgr(
            Mat source)
        {
            var bgr =
                new Mat();

            if (source == null ||
                source.Empty())
            {
                return bgr;
            }

            int channels =
                source.Channels();

            if (channels == 3)
            {
                source.CopyTo(
                    bgr);

                return bgr;
            }

            if (channels == 4)
            {
                Cv2.CvtColor(
                    source,
                    bgr,
                    ColorConversionCodes.BGRA2BGR);

                return bgr;
            }

            Cv2.CvtColor(
                source,
                bgr,
                ColorConversionCodes.GRAY2BGR);

            return bgr;
        }

        private static void NormalizePolarity(
            Mat image)
        {
            if (image == null ||
                image.Empty())
            {
                return;
            }

            Scalar mean =
                Cv2.Mean(
                    image);

            if (mean.Val0 < 127)
            {
                Cv2.BitwiseNot(
                    image,
                    image);
            }
        }

        private static List<System.Drawing.Rectangle> FullImageRegion(
            Bitmap image)
        {
            if (image == null)
            {
                return new List<System.Drawing.Rectangle>();
            }

            return new List<System.Drawing.Rectangle>
            {
                new System.Drawing.Rectangle(
                    0,
                    0,
                    image.Width,
                    image.Height)
            };
        }

        private static bool IsValidTextRegion(
            System.Drawing.Rectangle region)
        {
            return
                region.Width >= 12 &&
                region.Height >= 6;
        }

        private static System.Drawing.Rectangle ExpandAndClampRegion(
            System.Drawing.Rectangle region,
            int imageWidth,
            int imageHeight,
            int padding)
        {
            int left =
                Math.Max(
                    0,
                    region.Left - padding);

            int top =
                Math.Max(
                    0,
                    region.Top - padding);

            int right =
                Math.Min(
                    imageWidth,
                    region.Right + padding);

            int bottom =
                Math.Min(
                    imageHeight,
                    region.Bottom + padding);

            int width =
                Math.Max(
                    0,
                    right - left);

            int height =
                Math.Max(
                    0,
                    bottom - top);

            return new System.Drawing.Rectangle(
                left,
                top,
                width,
                height);
        }

        private static List<System.Drawing.Rectangle> RemoveDuplicateRegions(
            IEnumerable<System.Drawing.Rectangle> regions)
        {
            var result =
                new List<System.Drawing.Rectangle>();

            if (regions == null)
                return result;

            List<System.Drawing.Rectangle> ordered =
                regions
                    .OrderByDescending(
                        region =>
                            (long)region.Width *
                            region.Height)
                    .ToList();

            foreach (System.Drawing.Rectangle region in ordered)
            {
                bool duplicate =
                    false;

                for (int i = 0;
                     i < result.Count;
                     i++)
                {
                    if (IntersectionRatio(
                            region,
                            result[i]) >
                        0.80)
                    {
                        duplicate =
                            true;

                        break;
                    }
                }

                if (!duplicate)
                {
                    result.Add(
                        region);
                }
            }

            return result;
        }

        private static List<System.Drawing.Rectangle> OrderAndMergeRegions(
            IEnumerable<System.Drawing.Rectangle> regions,
            int imageWidth,
            int imageHeight)
        {
            List<System.Drawing.Rectangle> cleaned =
                RemoveDuplicateRegions(
                    regions);

            List<System.Drawing.Rectangle> ordered =
                cleaned
                    .Where(
                        region =>
                            IsValidTextRegion(region))
                    .OrderBy(
                        region =>
                            region.Top)
                    .ThenBy(
                        region =>
                            region.Left)
                    .ToList();

            if (ordered.Count <= 1)
                return ordered;

            var result =
                new List<System.Drawing.Rectangle>();

            foreach (System.Drawing.Rectangle current in ordered)
            {
                if (result.Count == 0)
                {
                    result.Add(
                        current);

                    continue;
                }

                int previousIndex =
                    result.Count - 1;

                System.Drawing.Rectangle previous =
                    result[previousIndex];

                int verticalDistance =
                    Math.Abs(
                        current.Top -
                        previous.Top);

                int allowedVerticalDistance =
                    Math.Max(
                        current.Height,
                        previous.Height) /
                    2;

                int horizontalGap =
                    current.Left -
                    previous.Right;

                if (verticalDistance <=
                        allowedVerticalDistance &&
                    horizontalGap >= -10 &&
                    horizontalGap <= 40)
                {
                    result[previousIndex] =
                        System.Drawing.Rectangle.Union(
                            previous,
                            current);
                }
                else
                {
                    result.Add(
                        current);
                }
            }

            for (int i = 0;
                 i < result.Count;
                 i++)
            {
                result[i] =
                    ExpandAndClampRegion(
                        result[i],
                        imageWidth,
                        imageHeight,
                        2);
            }

            return result;
        }

        private static double IntersectionRatio(
            System.Drawing.Rectangle a,
            System.Drawing.Rectangle b)
        {
            System.Drawing.Rectangle intersection =
                System.Drawing.Rectangle.Intersect(
                    a,
                    b);

            if (intersection.Width <= 0 ||
                intersection.Height <= 0)
            {
                return 0;
            }

            double intersectionArea =
                (double)intersection.Width *
                intersection.Height;

            double areaA =
                (double)a.Width *
                a.Height;

            double areaB =
                (double)b.Width *
                b.Height;

            double smallerArea =
                Math.Min(
                    areaA,
                    areaB);

            if (smallerArea <= 0)
                return 0;

            return
                intersectionArea /
                smallerArea;
        }

        private static double Clamp(
            double value,
            double minimum,
            double maximum)
        {
            if (value < minimum)
                return minimum;

            if (value > maximum)
                return maximum;

            return value;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(
                    ref _disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(OcrService));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) != 0)
            {
                return;
            }

            try
            {
                if (_eastNet != null)
                {
                    _eastNet.Dispose();
                }
            }
            catch
            {
            }

            foreach (IOcrEngine engine in
                     _ocrEngines.Values)
            {
                IDisposable disposable =
                    engine as IDisposable;

                if (disposable == null)
                    continue;

                try
                {
                    disposable.Dispose();
                }
                catch
                {
                }
            }

            _ocrEngines.Clear();

            try
            {
                _eastLock.Dispose();
            }
            catch
            {
            }
        }
    }
}