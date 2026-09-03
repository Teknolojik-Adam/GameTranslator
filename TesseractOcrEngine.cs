using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Tesseract;

namespace GameTranslatorUltimate
{
    public class TesseractOcrEngine : IOcrEngine, IDisposable
    {
        private sealed class EngineHolder : IDisposable
        {
            public TesseractEngine Engine { get; }
            public SemaphoreSlim Gate { get; }

            public EngineHolder(TesseractEngine engine)
            {
                Engine = engine;
                Gate = new SemaphoreSlim(1, 1);
            }

            public void Dispose()
            {
                Engine?.Dispose();
                Gate?.Dispose();
            }
        }

        private sealed class OcrCandidate
        {
            public string Text { get; set; }
            public float Confidence { get; set; }
        }

        private const float EarlyAcceptConfidence = 0.82f;
        private const float MinimumConfidence = 0.35f;

        private readonly ILogger _logger;
        private readonly AppSettings _appSettings;
        private readonly string _tessDataPath;

        private readonly ConcurrentDictionary<string, Lazy<EngineHolder>> _engineCache =
            new ConcurrentDictionary<string, Lazy<EngineHolder>>(
                StringComparer.OrdinalIgnoreCase);

        private bool _disposed;

        public OcrEngineType EngineType => OcrEngineType.Tesseract;

        public TesseractOcrEngine(
            ILogger logger,
            AppSettings appSettings = null)
        {
            _logger =
                logger ?? throw new ArgumentNullException(nameof(logger));

            _appSettings = appSettings;

            _tessDataPath =
                Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "tessdata");

            if (!Directory.Exists(_tessDataPath))
            {
                _logger.LogWarning(
                    $"Tesseract dil klasörü bulunamadı: {_tessDataPath}");
            }
        }

        public async Task<string> RecognizeTextAsync(
            Bitmap image,
            string language,
            PageSegMode psm = PageSegMode.Auto)
        {
            ThrowIfDisposed();

            if (image == null ||
                image.Width <= 0 ||
                image.Height <= 0)
            {
                return string.Empty;
            }

            string resolvedLanguage =
                ResolveLanguage(language);

            bool handwriting =
                _appSettings?.EnableHandwritingMode == true;

            EngineHolder holder =
                GetEngineHolder(
                    resolvedLanguage,
                    handwriting);

            if (holder == null)
                return string.Empty;

            PageSegMode effectivePsm =
                ResolvePageSegMode(
                    image,
                    psm);

            OcrCandidate otsu =
                await TryRecognizeAsync(
                    holder,
                    image,
                    effectivePsm,
                    PreprocessOtsu);

            if (IsEarlyAccept(otsu))
                return otsu.Text;

            OcrCandidate adaptive =
                await TryRecognizeAsync(
                    holder,
                    image,
                    effectivePsm,
                    PreprocessAdaptive);

            OcrCandidate best =
                SelectBest(
                    otsu,
                    adaptive);

            if (best == null ||
                string.IsNullOrWhiteSpace(best.Text))
            {
                return string.Empty;
            }

            if (best.Confidence < MinimumConfidence)
            {
                _logger.LogWarning(
                    $"Tesseract sonucu düşük güven nedeniyle reddedildi. " +
                    $"Dil: {resolvedLanguage}, Güven: %{best.Confidence * 100:F0}");

                return string.Empty;
            }

            return best.Text;
        }

        private async Task<OcrCandidate> TryRecognizeAsync(
            EngineHolder holder,
            Bitmap image,
            PageSegMode psm,
            Func<Bitmap, Pix> preprocess)
        {
            Pix pix = null;

            try
            {
                pix =
                    await Task.Run(
                        () => preprocess(image));

                if (pix == null)
                {
                    return EmptyCandidate();
                }

                await holder.Gate
                    .WaitAsync()
                    .ConfigureAwait(false);

                try
                {
                    return await Task.Run(() =>
                    {
                        using (Page page =
                               holder.Engine.Process(
                                   pix,
                                   psm))
                        {
                            string text =
                                CleanOcrText(
                                    page.GetText());

                            float confidence =
                                page.GetMeanConfidence();

                            return new OcrCandidate
                            {
                                Text = text,
                                Confidence = confidence
                            };
                        }
                    }).ConfigureAwait(false);
                }
                finally
                {
                    holder.Gate.Release();
                }
            }
            catch (DllNotFoundException ex)
            {
                _logger.LogError(
                    "Tesseract native kütüphanesi yüklenemedi.",
                    ex);

                return EmptyCandidate();
            }
            catch (BadImageFormatException ex)
            {
                _logger.LogError(
                    "Tesseract native kütüphanesi mimari uyumsuzluğu nedeniyle yüklenemedi.",
                    ex);

                return EmptyCandidate();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    $"Tesseract OCR işlemi başarısız oldu: {preprocess.Method.Name}",
                    ex);

                return EmptyCandidate();
            }
            finally
            {
                pix?.Dispose();
            }
        }

        private EngineHolder GetEngineHolder(
            string language,
            bool handwriting)
        {
            string key =
                $"{language}|{(handwriting ? "handwriting" : "normal")}";

            try
            {
                Lazy<EngineHolder> lazy =
                    _engineCache.GetOrAdd(
                        key,
                        _ => new Lazy<EngineHolder>(
                            () => CreateEngine(
                                language,
                                handwriting),
                            LazyThreadSafetyMode.ExecutionAndPublication));

                EngineHolder holder =
                    lazy.Value;

                if (holder == null)
                {
                    _engineCache.TryRemove(
                        key,
                        out _);
                }

                return holder;
            }
            catch (Exception ex)
            {
                _engineCache.TryRemove(
                    key,
                    out _);

                _logger.LogError(
                    $"Tesseract motoru oluşturulamadı. Dil: {language}",
                    ex);

                return null;
            }
        }

        private EngineHolder CreateEngine(
            string language,
            bool handwriting)
        {
            if (!Directory.Exists(_tessDataPath))
            {
                _logger.LogError(
                    $"Tesseract klasörü bulunamadı: {_tessDataPath}");

                return null;
            }

            try
            {
                var engine =
                    new TesseractEngine(
                        _tessDataPath,
                        language,
                        EngineMode.Default);

                engine.SetVariable(
                    "user_defined_dpi",
                    "300");

                engine.SetVariable(
                    "preserve_interword_spaces",
                    "1");

                if (handwriting)
                {
                    ConfigureHandwritingMode(
                        engine);
                }

                _logger.LogInformation(
                    $"Tesseract motoru hazırlandı. Dil: {language}");

                return new EngineHolder(
                    engine);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    $"Tesseract motoru başlatılamadı. Dil: {language}",
                    ex);

                return null;
            }
        }

        private string ResolveLanguage(
            string language)
        {
            string normalized =
                NormalizeLanguage(language);

            if (HasLanguageData(normalized))
                return normalized;

            _logger.LogWarning(
                $"Tesseract dil paketi bulunamadı: {normalized}");

            if (HasLanguageData("eng"))
            {
                _logger.LogWarning(
                    "OCR dili İngilizceye fallback yapıldı.");

                return "eng";
            }

            return normalized;
        }

        private bool HasLanguageData(
            string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return false;

            string[] languages =
                language.Split(
                    new[] { '+' },
                    StringSplitOptions.RemoveEmptyEntries);

            if (languages.Length == 0)
                return false;

            return languages.All(item =>
                File.Exists(
                    Path.Combine(
                        _tessDataPath,
                        item + ".traineddata")));
        }

        private string NormalizeLanguage(
            string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return "eng";

            string[] parts =
                language.Split(
                    new[] { '+' },
                    StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length > 1)
            {
                return string.Join(
                    "+",
                    parts.Select(
                        NormalizeSingleLanguage));
            }

            return NormalizeSingleLanguage(
                language);
        }

        private string NormalizeSingleLanguage(
            string language)
        {
            string value =
                language
                    .Trim()
                    .ToLowerInvariant();

            switch (value)
            {
                case "en":
                case "en-us":
                case "en-gb":
                case "english":
                case "eng":
                    return "eng";

                case "tr":
                case "tr-tr":
                case "turkish":
                case "tur":
                    return "tur";

                case "ja":
                case "ja-jp":
                case "japanese":
                case "jpn":
                    return "jpn";

                case "de":
                case "de-de":
                case "german":
                case "ger":
                case "deu":
                    return "deu";

                case "fr":
                case "fr-fr":
                case "french":
                case "fre":
                case "fra":
                    return "fra";

                case "ru":
                case "ru-ru":
                case "russian":
                case "rus":
                    return "rus";

                case "es":
                case "es-es":
                case "spanish":
                case "spa":
                    return "spa";

                case "it":
                case "it-it":
                case "italian":
                case "ita":
                    return "ita";

                case "pt":
                case "pt-br":
                case "pt-pt":
                case "portuguese":
                case "por":
                    return "por";

                case "ko":
                case "ko-kr":
                case "korean":
                case "kor":
                    return "kor";

                case "zh":
                case "zh-cn":
                case "zh-hans":
                case "chi_sim":
                    return "chi_sim";

                case "zh-tw":
                case "zh-hk":
                case "zh-hant":
                case "chi_tra":
                    return "chi_tra";

                default:
                    return language.Trim();
            }
        }

        private PageSegMode ResolvePageSegMode(
            Bitmap image,
            PageSegMode requested)
        {
            if (requested != PageSegMode.Auto)
                return requested;

            if (image.Height <= 120 &&
                image.Width >= image.Height * 2.5)
            {
                return PageSegMode.SingleLine;
            }

            if (image.Height <= 300)
            {
                return PageSegMode.SingleBlock;
            }

            return PageSegMode.Auto;
        }

        private Pix PreprocessOtsu(
            Bitmap image)
        {
            using (Mat source =
                   BitmapConverter.ToMat(image))
            using (Mat gray =
                   ConvertToGray(source))
            using (Mat prepared =
                   ResizeForOcr(gray))
            using (Mat blurred =
                   new Mat())
            using (Mat thresholded =
                   new Mat())
            {
                Cv2.GaussianBlur(
                    prepared,
                    blurred,
                    new OpenCvSharp.Size(3, 3),
                    0);

                Cv2.Threshold(
                    blurred,
                    thresholded,
                    0,
                    255,
                    ThresholdTypes.Binary |
                    ThresholdTypes.Otsu);

                NormalizePolarity(
                    thresholded);

                return ConvertMatToPix(
                    thresholded);
            }
        }

        private Pix PreprocessAdaptive(
            Bitmap image)
        {
            using (Mat source =
                   BitmapConverter.ToMat(image))
            using (Mat gray =
                   ConvertToGray(source))
            using (Mat prepared =
                   ResizeForOcr(gray))
            using (Mat denoised =
                   new Mat())
            using (Mat thresholded =
                   new Mat())
            {
                Cv2.GaussianBlur(
                    prepared,
                    denoised,
                    new OpenCvSharp.Size(3, 3),
                    0);

                Cv2.AdaptiveThreshold(
                    denoised,
                    thresholded,
                    255,
                    AdaptiveThresholdTypes.GaussianC,
                    ThresholdTypes.Binary,
                    31,
                    9);

                NormalizePolarity(
                    thresholded);

                return ConvertMatToPix(
                    thresholded);
            }
        }

        private Mat ConvertToGray(
            Mat source)
        {
            var gray =
                new Mat();

            if (source.Channels() == 1)
            {
                source.CopyTo(
                    gray);

                return gray;
            }

            if (source.Channels() == 4)
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

        private Mat ResizeForOcr(
            Mat gray)
        {
            var result =
                new Mat();

            double scale = 1.0;

            if (gray.Rows < 60)
                scale = 3.0;
            else if (gray.Rows < 120)
                scale = 2.0;
            else if (gray.Rows < 220)
                scale = 1.5;

            if (scale <= 1.0)
            {
                gray.CopyTo(
                    result);

                return result;
            }

            Cv2.Resize(
              gray,
            result,
           new OpenCvSharp.Size(0, 0),
             scale,
         scale,
        InterpolationFlags.Cubic);

            return result;
        }

        private void NormalizePolarity(
            Mat image)
        {
            Scalar mean =
                Cv2.Mean(image);

            if (mean.Val0 < 127)
            {
                Cv2.BitwiseNot(
                    image,
                    image);
            }
        }

        private Pix ConvertMatToPix(
            Mat mat)
        {
            using (Bitmap bitmap =
                   BitmapConverter.ToBitmap(mat))
            {
                return PixConverter.ToPix(
                    bitmap);
            }
        }

        private string CleanOcrText(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string result =
                text.Trim();

            result =
                Regex.Replace(
                    result,
                    @"\r\n|\r|\n",
                    " ");

            result =
                Regex.Replace(
                    result,
                    @"[ \t]{2,}",
                    " ");

            return result.Trim();
        }

        private OcrCandidate SelectBest(
            params OcrCandidate[] candidates)
        {
            return candidates
                .Where(candidate =>
                    candidate != null &&
                    !string.IsNullOrWhiteSpace(candidate.Text))
                .OrderByDescending(
                    candidate => candidate.Confidence)
                .ThenByDescending(
                    candidate => candidate.Text.Length)
                .FirstOrDefault();
        }

        private bool IsEarlyAccept(
            OcrCandidate candidate)
        {
            return candidate != null &&
                   !string.IsNullOrWhiteSpace(candidate.Text) &&
                   candidate.Confidence >= EarlyAcceptConfidence;
        }

        private OcrCandidate EmptyCandidate()
        {
            return new OcrCandidate
            {
                Text = string.Empty,
                Confidence = 0f
            };
        }

        private void ConfigureHandwritingMode(
            TesseractEngine engine)
        {
            TrySetVariable(
                engine,
                "classify_bln_numeric_mode",
                "0");

            TrySetVariable(
                engine,
                "preserve_interword_spaces",
                "1");

            TrySetVariable(
                engine,
                "tessedit_enable_doc_dict",
                "0");
        }

        private void TrySetVariable(
            TesseractEngine engine,
            string name,
            string value)
        {
            try
            {
                engine.SetVariable(
                    name,
                    value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Tesseract ayarı uygulanamadı: {name}={value}, {ex.Message}");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(TesseractOcrEngine));
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            foreach (KeyValuePair<string, Lazy<EngineHolder>> pair
                     in _engineCache)
            {
                try
                {
                    if (pair.Value.IsValueCreated)
                    {
                        pair.Value.Value?.Dispose();
                    }
                }
                catch
                {
                }
            }

            _engineCache.Clear();
        }
    }
}