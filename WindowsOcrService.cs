using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GameTranslatorUltimate
{
    public class WindowsOcrService : IOcrService
    {
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<string, OcrEngine> _ocrEngines =
            new ConcurrentDictionary<string, OcrEngine>(
                StringComparer.OrdinalIgnoreCase);

        private readonly OcrEngine _fallbackOcrEngine;

        public WindowsOcrService(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            try
            {
                _fallbackOcrEngine = OcrEngine.TryCreateFromUserProfileLanguages();

                if (_fallbackOcrEngine != null)
                {
                    _logger.LogInformation(
                        $"Windows OCR varsayılan motoru başlatıldı: " +
                        $"{_fallbackOcrEngine.RecognizerLanguage.DisplayName}");
                }
                else
                {
                    _logger.LogWarning(
                        "Windows OCR kullanıcı profilindeki dillerle başlatılamadı. " +
                        "İngilizce fallback deneniyor.");

                    _fallbackOcrEngine = CreateEngine("en-US");
                }

                if (_fallbackOcrEngine == null)
                {
                    _logger.LogError(
                        "Windows OCR motoru hiçbir desteklenen dille başlatılamadı.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Windows OCR altyapısı başlatılırken hata oluştu.",
                    ex);

                _fallbackOcrEngine = null;
            }
        }

        public async Task<string> RecognizeTextInRegionsAsync(
            Bitmap image,
            string language = "eng")
        {
            if (image == null)
                return string.Empty;

            OcrEngine engine = GetOcrEngine(language);

            if (engine == null)
            {
                _logger.LogError(
                    $"OCR motoru oluşturulamadı. Dil: {language}");

                return string.Empty;
            }

            try
            {
                using (Bitmap processedImage = PreprocessImage(image))
                {
                    if (processedImage == null)
                        return string.Empty;

                    List<Rectangle> regions = FindTextRegions(processedImage);

                    if (regions == null || regions.Count == 0)
                    {
                        _logger.LogInformation(
                            "Metin bölgesi bulunamadı. Tüm görüntü OCR işlemine gönderiliyor.");

                        return await RecognizeBitmapAsync(
                            processedImage,
                            engine);
                    }

                    regions = NormalizeAndSortRegions(
                        regions,
                        processedImage.Size);

                    if (regions.Count == 0)
                    {
                        return await RecognizeBitmapAsync(
                            processedImage,
                            engine);
                    }

                    var recognizedParts = new List<string>();

                    foreach (Rectangle region in regions)
                    {
                        using (Bitmap croppedImage =
                               CropImage(processedImage, region))
                        {
                            if (croppedImage == null)
                                continue;

                            string text = await RecognizeBitmapAsync(
                                croppedImage,
                                engine);

                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                recognizedParts.Add(text.Trim());
                            }
                        }
                    }

                    if (recognizedParts.Count == 0)
                    {
                        // Bölgesel OCR başarısızsa son fallback:
                        // görüntünün tamamını OCR'a gönder.
                        return await RecognizeBitmapAsync(
                            processedImage,
                            engine);
                    }

                    return string.Join(
                        Environment.NewLine,
                        recognizedParts);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Windows OCR tanıma sırasında hata oluştu.",
                    ex);

                return string.Empty;
            }
        }

        public Task<string> GetTextAdaptiveAsync(
            Bitmap image,
            string language)
        {
            return RecognizeTextInRegionsAsync(image, language);
        }

        public Task<string> GetTextFromImage(
            Bitmap image,
            string language = "eng",
            bool invertColors = false)
        {
            if (image == null)
                return Task.FromResult(string.Empty);

            if (!invertColors)
            {
                return RecognizeTextInRegionsAsync(
                    image,
                    language);
            }

            Bitmap inverted = null;

            try
            {
                inverted = InvertBitmap(image);

                return RecognizeAndDisposeAsync(
                    inverted,
                    language);
            }
            catch
            {
                inverted?.Dispose();
                throw;
            }
        }

        private async Task<string> RecognizeAndDisposeAsync(
            Bitmap bitmap,
            string language)
        {
            using (bitmap)
            {
                return await RecognizeTextInRegionsAsync(
                    bitmap,
                    language);
            }
        }

        private OcrEngine GetOcrEngine(string language)
        {
            string languageTag =
                NormalizeLanguageTag(language);

            if (_ocrEngines.TryGetValue(
                languageTag,
                out OcrEngine cachedEngine))
            {
                return cachedEngine;
            }

            OcrEngine engine = CreateEngine(languageTag);

            if (engine != null)
            {
                _ocrEngines.TryAdd(
                    languageTag,
                    engine);

                return engine;
            }

            _logger.LogWarning(
                $"Windows OCR '{languageTag}' dilini kullanamadı. " +
                "Varsayılan OCR motoruna dönülüyor.");

            return _fallbackOcrEngine;
        }

        private OcrEngine CreateEngine(string languageTag)
        {
            try
            {
                var language = new Language(languageTag);

                if (!OcrEngine.IsLanguageSupported(language))
                {
                    _logger.LogWarning(
                        $"Windows OCR dili desteklenmiyor veya dil paketi kurulu değil: " +
                        $"{languageTag}");

                    return null;
                }

                OcrEngine engine =
                    OcrEngine.TryCreateFromLanguage(language);

                if (engine != null)
                {
                    _logger.LogInformation(
                        $"Windows OCR motoru oluşturuldu: " +
                        $"{engine.RecognizerLanguage.DisplayName} ({languageTag})");
                }

                return engine;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"OCR motoru oluşturulamadı ({languageTag}): {ex.Message}");

                return null;
            }
        }

        private string NormalizeLanguageTag(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return "en-US";

            string value =
                language.Trim().ToLowerInvariant();

            switch (value)
            {
                case "eng":
                case "en":
                case "en-us":
                case "english":
                    return "en-US";

                case "tur":
                case "tr":
                case "tr-tr":
                case "turkish":
                    return "tr-TR";

                case "deu":
                case "ger":
                case "de":
                case "de-de":
                case "german":
                    return "de-DE";

                case "fra":
                case "fre":
                case "fr":
                case "fr-fr":
                case "french":
                    return "fr-FR";

                case "spa":
                case "es":
                case "es-es":
                case "spanish":
                    return "es-ES";

                case "ita":
                case "it":
                case "it-it":
                    return "it-IT";

                case "por":
                case "pt":
                case "pt-br":
                    return "pt-BR";

                case "jpn":
                case "ja":
                case "ja-jp":
                    return "ja-JP";

                case "kor":
                case "ko":
                case "ko-kr":
                    return "ko-KR";

                case "zho":
                case "chi":
                case "zh":
                case "zh-cn":
                    return "zh-CN";

                default:
                    // BCP-47 formatı verilmiş olabilir.
                    return language;
            }
        }

        private async Task<string> RecognizeBitmapAsync(
            Bitmap bitmap,
            OcrEngine engine)
        {
            if (bitmap == null || engine == null)
                return string.Empty;

            try
            {
                using (SoftwareBitmap softwareBitmap =
                       await CreateSoftwareBitmapFromBitmap(bitmap))
                {
                    if (softwareBitmap == null)
                        return string.Empty;

                    OcrResult result =
                        await engine.RecognizeAsync(softwareBitmap);

                    return result?.Text?.Trim() ??
                           string.Empty;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Bitmap OCR işlemi başarısız: {ex.Message}");

                return string.Empty;
            }
        }

        public List<Rectangle> FindTextRegions(
            Bitmap sourceImage)
        {
            if (sourceImage == null)
                return new List<Rectangle>();

            try
            {
                using (Mat source =
                       BitmapConverter.ToMat(sourceImage))
                using (Mat gray = new Mat())
                using (Mat gradient = new Mat())
                using (Mat binary = new Mat())
                {
                    ConvertToGray(source, gray);

                    using (Mat gradientKernel =
                           Cv2.GetStructuringElement(
                               MorphShapes.Rect,
                               new OpenCvSharp.Size(3, 3)))
                    {
                        Cv2.MorphologyEx(
                            gray,
                            gradient,
                            MorphTypes.Gradient,
                            gradientKernel);
                    }

                    Cv2.Threshold(
                        gradient,
                        binary,
                        0,
                        255,
                        ThresholdTypes.Binary |
                        ThresholdTypes.Otsu);

                    // Harfleri kelime/satır bloklarına birleştir.
                    using (Mat closeKernel =
                           Cv2.GetStructuringElement(
                               MorphShapes.Rect,
                               new OpenCvSharp.Size(15, 3)))
                    {
                        Cv2.MorphologyEx(
                            binary,
                            binary,
                            MorphTypes.Close,
                            closeKernel);
                    }

                    Cv2.FindContours(
                        binary,
                        out OpenCvSharp.Point[][] contours,
                        out _,
                        RetrievalModes.External,
                        ContourApproximationModes.ApproxSimple);

                    var regions =
                        new List<Rectangle>();

                    double imageArea =
                        sourceImage.Width *
                        (double)sourceImage.Height;

                    foreach (OpenCvSharp.Point[] contour in contours)
                    {
                        OpenCvSharp.Rect rect =
                            Cv2.BoundingRect(contour);

                        if (rect.Width < 15 ||
                            rect.Height < 6)
                        {
                            continue;
                        }

                        double area =
                            rect.Width * (double)rect.Height;

                        if (area < imageArea * 0.00005)
                            continue;

                        if (area > imageArea * 0.95)
                            continue;

                        // Eski:
                        // rect.Width < sourceImage.Width / 2
                        //
                        // Uzun oyun altyazılarını kaybettirebiliyordu.
                        if (rect.Width >
                            sourceImage.Width * 0.98)
                        {
                            continue;
                        }

                        Rectangle region =
                            ExpandRectangle(
                                new Rectangle(
                                    rect.X,
                                    rect.Y,
                                    rect.Width,
                                    rect.Height),
                                sourceImage.Size,
                                4);

                        regions.Add(region);
                    }

                    return NormalizeAndSortRegions(
                        regions,
                        sourceImage.Size);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"FindTextRegions başarısız: {ex.Message}");

                return new List<Rectangle>
                {
                    new Rectangle(
                        0,
                        0,
                        sourceImage.Width,
                        sourceImage.Height)
                };
            }
        }

        private List<Rectangle> NormalizeAndSortRegions(
            IEnumerable<Rectangle> regions,
            System.Drawing.Size imageSize)
        {
            var validRegions =
                regions
                    .Select(r =>
                        ClampRectangle(r, imageSize))
                    .Where(r =>
                        r.Width > 0 &&
                        r.Height > 0)
                    .OrderBy(r => r.Top)
                    .ThenBy(r => r.Left)
                    .ToList();

            var result =
                new List<Rectangle>();

            foreach (Rectangle candidate in validRegions)
            {
                bool duplicate = result.Any(existing =>
                    IntersectionRatio(
                        existing,
                        candidate) > 0.80);

                if (!duplicate)
                {
                    result.Add(candidate);
                }
            }

            return result;
        }

        private double IntersectionRatio(
            Rectangle a,
            Rectangle b)
        {
            Rectangle intersection =
                Rectangle.Intersect(a, b);

            if (intersection.Width <= 0 ||
                intersection.Height <= 0)
            {
                return 0;
            }

            double intersectionArea =
                intersection.Width *
                (double)intersection.Height;

            double smallerArea =
                Math.Min(
                    a.Width * (double)a.Height,
                    b.Width * (double)b.Height);

            if (smallerArea <= 0)
                return 0;

            return intersectionArea /
                   smallerArea;
        }

        private Rectangle ExpandRectangle(
            Rectangle rectangle,
            System.Drawing.Size imageSize,
            int padding)
        {
            Rectangle expanded =
                new Rectangle(
                    rectangle.X - padding,
                    rectangle.Y - padding,
                    rectangle.Width + padding * 2,
                    rectangle.Height + padding * 2);

            return ClampRectangle(
                expanded,
                imageSize);
        }

        private Rectangle ClampRectangle(
            Rectangle rectangle,
            System.Drawing.Size imageSize)
        {
            int left =
                Math.Max(0, rectangle.Left);

            int top =
                Math.Max(0, rectangle.Top);

            int right =
                Math.Min(
                    imageSize.Width,
                    rectangle.Right);

            int bottom =
                Math.Min(
                    imageSize.Height,
                    rectangle.Bottom);

            if (right <= left ||
                bottom <= top)
            {
                return Rectangle.Empty;
            }

            return Rectangle.FromLTRB(
                left,
                top,
                right,
                bottom);
        }

        public Bitmap IsolateTextByColor(
            Bitmap sourceImage)
        {
            if (sourceImage == null)
                return null;

            Bitmap result =
                new Bitmap(sourceImage);

            try
            {
                using (Mat source =
                       BitmapConverter.ToMat(result))
                using (Mat gray = new Mat())
                using (Mat mask = new Mat())
                {
                    ConvertToGray(source, gray);

                    Cv2.AdaptiveThreshold(
                        gray,
                        mask,
                        255,
                        AdaptiveThresholdTypes.GaussianC,
                        ThresholdTypes.Binary,
                        31,
                        9);

                    return BitmapConverter.ToBitmap(mask);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Metin renk izolasyonu başarısız: {ex.Message}");

                return result;
            }
            finally
            {
                result.Dispose();
            }
        }

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(
            IntPtr hWnd,
            IntPtr hdcBlt,
            uint nFlags);

        [DllImport("user32.dll")]
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

        public Bitmap CaptureWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
                return null;

            if (!GetWindowRect(
                hWnd,
                out RECT rect))
            {
                _logger.LogWarning(
                    "GetWindowRect başarısız.");

                return null;
            }

            int width =
                rect.Right - rect.Left;

            int height =
                rect.Bottom - rect.Top;

            if (width <= 0 ||
                height <= 0)
            {
                return null;
            }

            var bitmap =
                new Bitmap(
                    width,
                    height,
                    PixelFormat.Format32bppArgb);

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
                            _logger.LogWarning(
                                "PrintWindow görüntü yakalayamadı.");

                            bitmap.Dispose();
                            return null;
                        }
                    }
                    finally
                    {
                        if (hdc != IntPtr.Zero)
                        {
                            graphics.ReleaseHdc(hdc);
                        }
                    }
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                bitmap.Dispose();

                _logger.LogError(
                    "Pencere görüntüsü yakalanırken hata oluştu.",
                    ex);

                return null;
            }
        }

        public Bitmap CropImage(
            Bitmap image,
            Rectangle region)
        {
            if (image == null)
                return null;

            Rectangle safeRegion =
                ClampRectangle(
                    region,
                    image.Size);

            if (safeRegion ==
                Rectangle.Empty)
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

        private async Task<SoftwareBitmap>
            CreateSoftwareBitmapFromBitmap(
                Bitmap bitmap)
        {
            if (bitmap == null)
                return null;

            using (var stream =
                   new InMemoryRandomAccessStream())
            {
                using (Stream netStream =
                       stream.AsStreamForWrite())
                {
                    bitmap.Save(
                        netStream,
                        ImageFormat.Bmp);

                    await netStream.FlushAsync();
                }

                stream.Seek(0);

                BitmapDecoder decoder =
                    await BitmapDecoder.CreateAsync(
                        stream);

                SoftwareBitmap softwareBitmap =
                    await decoder.GetSoftwareBitmapAsync();

                if (softwareBitmap.BitmapPixelFormat !=
                    BitmapPixelFormat.Bgra8 ||
                    softwareBitmap.BitmapAlphaMode ==
                    BitmapAlphaMode.Straight)
                {
                    SoftwareBitmap converted =
                        SoftwareBitmap.Convert(
                            softwareBitmap,
                            BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Premultiplied);

                    softwareBitmap.Dispose();
                    softwareBitmap = converted;
                }

                return softwareBitmap;
            }
        }

        public Bitmap PreprocessImage(
            Bitmap image)
        {
            if (image == null)
                return null;

            Bitmap processedBitmap =
                new Bitmap(image);

            OptimizeImageForOcr(
                processedBitmap);

            return processedBitmap;
        }

        private void OptimizeImageForOcr(
            Bitmap bitmap)
        {
            try
            {
                using (Mat source =
                       BitmapConverter.ToMat(bitmap))
                using (Mat gray = new Mat())
                {
                    ConvertToGray(
                        source,
                        gray);

                    Cv2.EqualizeHist(
                        gray,
                        gray);

                    Cv2.GaussianBlur(
                        gray,
                        gray,
                        new OpenCvSharp.Size(3, 3),
                        0);

                    Cv2.Threshold(
                        gray,
                        gray,
                        0,
                        255,
                        ThresholdTypes.Binary |
                        ThresholdTypes.Otsu);

                    using (Bitmap converted =
                           BitmapConverter.ToBitmap(gray))
                    using (Graphics graphics =
                           Graphics.FromImage(bitmap))
                    {
                        graphics.DrawImageUnscaled(
                            converted,
                            0,
                            0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"OCR görüntü optimizasyonu başarısız: {ex.Message}");
            }
        }

        private void ConvertToGray(
            Mat source,
            Mat gray)
        {
            if (source == null ||
                source.Empty())
            {
                return;
            }

            if (source.Channels() == 1)
            {
                source.CopyTo(gray);
                return;
            }

            if (source.Channels() == 4)
            {
                Cv2.CvtColor(
                    source,
                    gray,
                    ColorConversionCodes.BGRA2GRAY);

                return;
            }

            Cv2.CvtColor(
                source,
                gray,
                ColorConversionCodes.BGR2GRAY);
        }

        private Bitmap InvertBitmap(
            Bitmap source)
        {
            using (Mat mat =
                   BitmapConverter.ToMat(source))
            using (Mat inverted =
                   new Mat())
            {
                Cv2.BitwiseNot(
                    mat,
                    inverted);

                return BitmapConverter.ToBitmap(
                    inverted);
            }
        }

        public Mat CreateContrastMask(
            Mat sourceImage)
        {
            if (sourceImage == null ||
                sourceImage.Empty())
            {
                return new Mat();
            }

            using (Mat gray =
                   new Mat())
            {
                ConvertToGray(
                    sourceImage,
                    gray);

                Mat mask =
                    new Mat();

                Cv2.AdaptiveThreshold(
                    gray,
                    mask,
                    255,
                    AdaptiveThresholdTypes.GaussianC,
                    ThresholdTypes.BinaryInv,
                    11,
                    2);

                return mask;
            }
        }

        public Mat CreateEdgeMask(
            Mat sourceImage)
        {
            if (sourceImage == null ||
                sourceImage.Empty())
            {
                return new Mat();
            }

            using (Mat gray =
                   new Mat())
            {
                ConvertToGray(
                    sourceImage,
                    gray);

                Cv2.GaussianBlur(
                    gray,
                    gray,
                    new OpenCvSharp.Size(3, 3),
                    0);

                Mat edges =
                    new Mat();

                Cv2.Canny(
                    gray,
                    edges,
                    100,
                    200);

                return edges;
            }
        }

        public Scalar[] DetectTextColors(
            Mat sourceImage)
        {
            if (sourceImage == null ||
                sourceImage.Empty())
            {
                return new Scalar[0];
            }

            try
            {
                using (Mat hsv =
                       new Mat())
                {
                    Cv2.CvtColor(
                        sourceImage,
                        hsv,
                        sourceImage.Channels() == 4
                            ? ColorConversionCodes.BGRA2BGR
                            : ColorConversionCodes.BGR2HSV);

                    // BGRA ise önce BGR'ye çevirmek gerekir.
                    if (sourceImage.Channels() == 4)
                    {
                        using (Mat bgr = new Mat())
                        {
                            Cv2.CvtColor(
                                sourceImage,
                                bgr,
                                ColorConversionCodes.BGRA2BGR);

                            Cv2.CvtColor(
                                bgr,
                                hsv,
                                ColorConversionCodes.BGR2HSV);
                        }
                    }

                    using (Mat hist =
                           new Mat())
                    {
                        int[] channels = { 0 };
                        int[] histSize = { 180 };

                        Rangef[] ranges =
                        {
                            new Rangef(0, 180)
                        };

                        Cv2.CalcHist(
                            new[] { hsv },
                            channels,
                            null,
                            hist,
                            1,
                            histSize,
                            ranges);

                        var dominantColors =
                            new Scalar[3];

                        for (int i = 0;
                             i < dominantColors.Length;
                             i++)
                        {
                            Cv2.MinMaxLoc(
                                hist,
                                out _,
                                out _,
                                out _,
                                out OpenCvSharp.Point maxLoc);

                            int hue =
                                maxLoc.Y;

                            dominantColors[i] =
                                new Scalar(
                                    hue,
                                    255,
                                    255);

                            if (hue >= 0 &&
                                hue < hist.Rows)
                            {
                                hist.Set<float>(
                                    hue,
                                    0,
                                    0f);
                            }
                        }

                        return dominantColors;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Metin rengi analizi başarısız: {ex.Message}");

                return new Scalar[0];
            }
        }
    }
}