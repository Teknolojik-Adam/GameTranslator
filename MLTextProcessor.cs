using OpenCvSharp;
using OpenCvSharp.Dnn;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace GameTranslatorUltimate
{
    public sealed class MLTextProcessor : IDisposable
    {
        private readonly ILogger _logger;
        private readonly AppSettings _appSettings;

        private readonly Dictionary<DnnModelType, Net> _dnnModels =
            new Dictionary<DnnModelType, Net>();

        private readonly Dictionary<string, int> _wordFrequency =
            new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<string>> _contextPatterns =
            new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);

        private readonly List<string> _recentTexts =
            new List<string>();

        private readonly object _historyLock =
            new object();

        private readonly object _modelLock =
            new object();

        private int _disposed;

        public MLTextProcessor(
            ILogger logger,
            AppSettings appSettings)
        {
            _logger =
                logger ?? throw new ArgumentNullException(nameof(logger));

            _appSettings =
                appSettings ?? throw new ArgumentNullException(nameof(appSettings));

            InitializeDnnModels();
            LoadGameTerminology();
        }

        private void InitializeDnnModels()
        {
            string baseDirectory =
                AppDomain.CurrentDomain.BaseDirectory;

            TryLoadModel(
                DnnModelType.EAST,
                Path.Combine(
                    baseDirectory,
                    "frozen_east_text_detection.pb"));

            string customPath =
                _appSettings.CustomDnnModelPath;

            if (!string.IsNullOrWhiteSpace(customPath))
            {
                if (!Path.IsPathRooted(customPath))
                {
                    customPath =
                        Path.Combine(
                            baseDirectory,
                            customPath);
                }

                TryLoadModel(
                    DnnModelType.Custom,
                    customPath);
            }
        }

        private void TryLoadModel(
            DnnModelType modelType,
            string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !File.Exists(path))
            {
                return;
            }

            try
            {
                Net model =
                    CvDnn.ReadNet(path);

                if (model == null ||
                    model.Empty())
                {
                    model?.Dispose();
                    return;
                }

                _dnnModels[modelType] =
                    model;

                _logger.LogInformation(
                    $"{modelType} DNN modeli yüklendi: {path}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"{modelType} DNN modeli yüklenemedi: {ex.Message}");
            }
        }

        public MLTextResult ProcessTextWithML(
            string rawText,
            Mat image = null,
            string context = "")
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(rawText))
            {
                return CreateResult(
                    rawText,
                    rawText,
                    0);
            }

            if (!_appSettings.EnableMachineLearning)
            {
                return CreateResult(
                    rawText,
                    rawText,
                    1.0);
            }

            var result =
                CreateResult(
                    rawText,
                    rawText,
                    0.80);

            try
            {
                string processed =
                    rawText;

                if (_appSettings.EnableTextCorrection)
                {
                    string corrected =
                        OcrTextCorrector.CorrectText(
                            processed,
                            GetCurrentOcrLanguage(),
                            true,
                            _logger);

                    if (!string.Equals(
                            corrected,
                            processed,
                            StringComparison.Ordinal))
                    {
                        processed =
                            corrected;

                        result.Improvements.Add(
                            "OCR metin düzeltmesi uygulandı.");

                        result.Confidence +=
                            0.05;
                    }
                }

                if (_appSettings.EnableContextAnalysis &&
                    !string.IsNullOrWhiteSpace(context))
                {
                    string contextImproved =
                        AnalyzeContext(
                            processed,
                            context);

                    if (!string.Equals(
                            contextImproved,
                            processed,
                            StringComparison.Ordinal))
                    {
                        processed =
                            contextImproved;

                        result.Improvements.Add(
                            "Bağlam tabanlı düzeltme uygulandı.");

                        result.Confidence +=
                            0.05;
                    }
                }

                if (image != null &&
                    _dnnModels.ContainsKey(
                        _appSettings.SelectedDnnModel))
                {
                    bool validated =
                        ValidateWithDnn(
                            image,
                            _appSettings.SelectedDnnModel);

                    if (validated)
                    {
                        result.Improvements.Add(
                            $"{_appSettings.SelectedDnnModel} doğrulaması başarılı.");

                        result.Confidence +=
                            0.05;
                    }
                }

                processed =
                    NormalizeWhitespace(
                        processed);

                result.ProcessedText =
                    processed;

                result.Confidence =
                    Clamp01(
                        result.Confidence);

                LearnFromHistory(
                    processed);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "ML metin işleme sırasında hata oluştu.",
                    ex);

                return new MLTextResult
                {
                    OriginalText =
                        rawText,

                    ProcessedText =
                        rawText,

                    Confidence =
                        0.50,

                    Improvements =
                        new List<string>
                        {
                            "ML işleme sırasında hata oluştu."
                        }
                };
            }
        }

        private string AnalyzeContext(
            string text,
            string context)
        {
            if (string.IsNullOrWhiteSpace(text) ||
                string.IsNullOrWhiteSpace(context))
            {
                return text;
            }

            string[] contextWords =
                SplitWords(context);

            if (contextWords.Length == 0)
                return text;

            string[] words =
                text.Split(
                    new[] { ' ' },
                    StringSplitOptions.None);

            for (int i = 0;
                 i < words.Length;
                 i++)
            {
                string original =
                    words[i];

                string clean =
                    CleanWord(original);

                if (clean.Length < 4)
                    continue;

                string bestMatch =
                    FindBestContextMatch(
                        clean,
                        contextWords);

                if (string.IsNullOrWhiteSpace(bestMatch))
                    continue;

                double similarity =
                    CalculateSimilarity(
                        clean,
                        CleanWord(bestMatch));

                if (similarity < 0.88)
                    continue;

                words[i] =
                    ReplaceWordPreservingPunctuation(
                        original,
                        bestMatch);
            }

            return string.Join(
                " ",
                words);
        }

        private string FindBestContextMatch(
            string word,
            string[] contextWords)
        {
            if (string.IsNullOrWhiteSpace(word) ||
                contextWords == null ||
                contextWords.Length == 0)
            {
                return null;
            }

            string exact =
                contextWords.FirstOrDefault(
                    contextWord =>
                        string.Equals(
                            CleanWord(contextWord),
                            word,
                            StringComparison.OrdinalIgnoreCase));

            if (exact != null)
                return null;

            string bestMatch =
                null;

            double bestScore =
                0;

            foreach (string contextWord in contextWords)
            {
                string cleanContext =
                    CleanWord(contextWord);

                if (cleanContext.Length < 4)
                    continue;

                if (Math.Abs(
                        cleanContext.Length -
                        word.Length) > 2)
                {
                    continue;
                }

                double score =
                    CalculateSimilarity(
                        word,
                        cleanContext);

                if (score >
                    bestScore)
                {
                    bestScore =
                        score;

                    bestMatch =
                        contextWord;
                }
            }

            return bestScore >= 0.88
                ? bestMatch
                : null;
        }

        private bool ValidateWithDnn(
            Mat image,
            DnnModelType modelType)
        {
            if (image == null ||
                image.Empty())
            {
                return false;
            }

            Net model;

            if (!_dnnModels.TryGetValue(
                    modelType,
                    out model))
            {
                return false;
            }

            if (modelType !=
                DnnModelType.EAST)
            {
                return false;
            }

            try
            {
                lock (_modelLock)
                {
                    List<Rectangle> regions =
                        DetectTextRegionsWithEast(
                            image,
                            model);

                    return regions.Count > 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"DNN doğrulaması başarısız: {ex.Message}");

                return false;
            }
        }

        private List<Rectangle> DetectTextRegionsWithEast(
            Mat source,
            Net model)
        {
            var regions =
                new List<Rectangle>();

            if (source == null ||
                source.Empty() ||
                model == null)
            {
                return regions;
            }

            int newWidth =
                (source.Width / 32) * 32;

            int newHeight =
                (source.Height / 32) * 32;

            if (newWidth < 32 ||
                newHeight < 32)
            {
                return regions;
            }

            double ratioWidth =
                (double)source.Width /
                newWidth;

            double ratioHeight =
                (double)source.Height /
                newHeight;

            using (Mat blob =
                   CvDnn.BlobFromImage(
                       source,
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
                    model.SetInput(blob);

                    string[] outputNames =
                    {
                        "feature_fusion/Conv_7/Sigmoid",
                        "feature_fusion/concat_3"
                    };

                    model.Forward(
                        output,
                        outputNames);

                    List<RotatedRect> boxes;
                    List<float> confidences;

                    DecodeEastOutput(
                        output[0],
                        output[1],
                        0.5f,
                        out boxes,
                        out confidences);

                    if (boxes.Count == 0)
                        return regions;

                    int[] indices;

                    CvDnn.NMSBoxes(
                        boxes,
                        confidences,
                        0.5f,
                        0.4f,
                        out indices);

                    foreach (int index in indices)
                    {
                        if (index < 0 ||
                            index >= boxes.Count)
                        {
                            continue;
                        }

                        Point2f[] points =
                            boxes[index].Points();

                        for (int i = 0;
                             i < points.Length;
                             i++)
                        {
                            points[i].X *=
                                (float)ratioWidth;

                            points[i].Y *=
                                (float)ratioHeight;
                        }

                        OpenCvSharp.Rect cvRect =
                            Cv2.BoundingRect(
                                points);

                        Rectangle rect =
                            ClampRectangle(
                                new Rectangle(
                                    cvRect.X,
                                    cvRect.Y,
                                    cvRect.Width,
                                    cvRect.Height),
                                source.Width,
                                source.Height);

                        if (rect.Width >= 8 &&
                            rect.Height >= 5)
                        {
                            regions.Add(
                                rect);
                        }
                    }
                }
                finally
                {
                    for (int i = 0;
                         i < output.Length;
                         i++)
                    {
                        output[i]?.Dispose();
                    }
                }
            }

            return regions;
        }

        private static void DecodeEastOutput(
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

                    float boxWidth =
                        right +
                        left;

                    float boxHeight =
                        top +
                        bottom;

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
                        boxWidth / 2.0f;

                    float centerY =
                        endY -
                        boxHeight / 2.0f;

                    boxes.Add(
                        new RotatedRect(
                            new Point2f(
                                centerX,
                                centerY),
                            new Size2f(
                                boxWidth,
                                boxHeight),
                            -angle *
                            180.0f /
                            (float)Math.PI));

                    confidences.Add(
                        score);
                }
            }
        }

        private void LearnFromHistory(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            lock (_historyLock)
            {
                _recentTexts.Add(
                    text);

                while (_recentTexts.Count >
                       100)
                {
                    _recentTexts.RemoveAt(
                        0);
                }

                string[] words =
                    SplitWords(
                        text);

                foreach (string word in words)
                {
                    string clean =
                        CleanWord(word);

                    if (clean.Length < 2)
                        continue;

                    int current;

                    if (_wordFrequency.TryGetValue(
                            clean,
                            out current))
                    {
                        if (current <
                            int.MaxValue)
                        {
                            _wordFrequency[clean] =
                                current + 1;
                        }
                    }
                    else
                    {
                        _wordFrequency[clean] =
                            1;
                    }
                }
            }
        }

        private void LoadGameTerminology()
        {
            _contextPatterns["combat"] =
                new List<string>
                {
                    "attack",
                    "defend",
                    "damage",
                    "health",
                    "mana",
                    "spell",
                    "skill"
                };

            _contextPatterns["inventory"] =
                new List<string>
                {
                    "item",
                    "weapon",
                    "armor",
                    "potion",
                    "equipment",
                    "bag",
                    "inventory"
                };

            _contextPatterns["quest"] =
                new List<string>
                {
                    "quest",
                    "mission",
                    "objective",
                    "goal",
                    "reward",
                    "complete",
                    "finish"
                };

            _contextPatterns["character"] =
                new List<string>
                {
                    "level",
                    "experience",
                    "stats",
                    "attributes",
                    "class",
                    "race",
                    "character"
                };
        }

        private double CalculateSimilarity(
            string first,
            string second)
        {
            if (string.IsNullOrWhiteSpace(first) ||
                string.IsNullOrWhiteSpace(second))
            {
                return 0;
            }

            first =
                first.ToLowerInvariant();

            second =
                second.ToLowerInvariant();

            int maxLength =
                Math.Max(
                    first.Length,
                    second.Length);

            if (maxLength == 0)
                return 1;

            int distance =
                LevenshteinDistance(
                    first,
                    second);

            return Clamp01(
                1.0 -
                (double)distance /
                maxLength);
        }

        private static int LevenshteinDistance(
            string source,
            string target)
        {
            source =
                source ?? string.Empty;

            target =
                target ?? string.Empty;

            if (source.Length == 0)
                return target.Length;

            if (target.Length == 0)
                return source.Length;

            if (source.Length >
                target.Length)
            {
                string temp =
                    source;

                source =
                    target;

                target =
                    temp;
            }

            int[] previous =
                new int[source.Length + 1];

            int[] current =
                new int[source.Length + 1];

            for (int i = 0;
                 i <= source.Length;
                 i++)
            {
                previous[i] =
                    i;
            }

            for (int j = 1;
                 j <= target.Length;
                 j++)
            {
                current[0] =
                    j;

                for (int i = 1;
                     i <= source.Length;
                     i++)
                {
                    int cost =
                        source[i - 1] ==
                        target[j - 1]
                            ? 0
                            : 1;

                    current[i] =
                        Math.Min(
                            Math.Min(
                                current[i - 1] + 1,
                                previous[i] + 1),
                            previous[i - 1] +
                            cost);
                }

                int[] temp =
                    previous;

                previous =
                    current;

                current =
                    temp;
            }

            return previous[
                source.Length];
        }

        public MLStatistics GetStatistics()
        {
            ThrowIfDisposed();

            lock (_historyLock)
            {
                return new MLStatistics
                {
                    TotalTextsProcessed =
                        _recentTexts.Count,

                    UniqueWordsLearned =
                        _wordFrequency.Count,

                    DnnModelsLoaded =
                        _dnnModels.Count,

                    AverageConfidence =
                        _recentTexts.Count > 0
                            ? 0.85
                            : 0
                };
            }
        }

        public void ClearHistory()
        {
            ThrowIfDisposed();

            lock (_historyLock)
            {
                _recentTexts.Clear();
                _wordFrequency.Clear();
            }

            _logger.LogInformation(
                "ML geçmişi temizlendi.");
        }

        private string GetCurrentOcrLanguage()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(
                    _appSettings.OcrLanguage))
                {
                    return _appSettings.OcrLanguage;
                }
            }
            catch
            {
            }

            return null;
        }

        private static string[] SplitWords(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new string[0];

            return text.Split(
                new[]
                {
                    ' ',
                    '\t',
                    '\r',
                    '\n'
                },
                StringSplitOptions.RemoveEmptyEntries);
        }

        private static string CleanWord(
            string word)
        {
            if (string.IsNullOrWhiteSpace(word))
                return string.Empty;

            return Regex.Replace(
                    word,
                    @"[^\p{L}\p{N}'’-]",
                    string.Empty)
                .ToLowerInvariant();
        }

        private static string ReplaceWordPreservingPunctuation(
            string original,
            string replacement)
        {
            if (string.IsNullOrWhiteSpace(original) ||
                string.IsNullOrWhiteSpace(replacement))
            {
                return original;
            }

            Match match =
                Regex.Match(
                    original,
                    @"[\p{L}\p{N}'’-]+");

            if (!match.Success)
                return original;

            string value =
                PreserveCase(
                    match.Value,
                    replacement);

            return original.Substring(
                       0,
                       match.Index) +
                   value +
                   original.Substring(
                       match.Index +
                       match.Length);
        }

        private static string PreserveCase(
            string original,
            string replacement)
        {
            if (string.IsNullOrEmpty(original) ||
                string.IsNullOrEmpty(replacement))
            {
                return replacement;
            }

            if (original.All(
                char.IsUpper))
            {
                return replacement
                    .ToUpperInvariant();
            }

            if (char.IsUpper(
                original[0]))
            {
                if (replacement.Length == 1)
                {
                    return replacement
                        .ToUpperInvariant();
                }

                return
                    char.ToUpperInvariant(
                        replacement[0]) +
                    replacement.Substring(
                        1);
            }

            return replacement;
        }

        private static string NormalizeWhitespace(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string result =
                text.Replace("\r\n", "\n")
                    .Replace('\r', '\n');

            result =
                Regex.Replace(
                    result,
                    @"[ \t]{2,}",
                    " ");

            result =
                Regex.Replace(
                    result,
                    @" *\n *",
                    "\n");

            return result.Trim();
        }

        private static Rectangle ClampRectangle(
            Rectangle rectangle,
            int width,
            int height)
        {
            return Rectangle.Intersect(
                new Rectangle(
                    0,
                    0,
                    width,
                    height),
                rectangle);
        }

        private static double Clamp01(
            double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value))
            {
                return 0;
            }

            if (value < 0)
                return 0;

            if (value > 1)
                return 1;

            return value;
        }

        private static MLTextResult CreateResult(
            string original,
            string processed,
            double confidence)
        {
            return new MLTextResult
            {
                OriginalText =
                    original ?? string.Empty,

                ProcessedText =
                    processed ?? string.Empty,

                Confidence =
                    Clamp01(confidence),

                Improvements =
                    new List<string>(),

                Timestamp =
                    DateTime.Now
            };
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(
                    ref _disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(MLTextProcessor));
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

            lock (_modelLock)
            {
                foreach (Net model in
                         _dnnModels.Values)
                {
                    try
                    {
                        model?.Dispose();
                    }
                    catch
                    {
                    }
                }

                _dnnModels.Clear();
            }

            lock (_historyLock)
            {
                _recentTexts.Clear();
                _wordFrequency.Clear();
                _contextPatterns.Clear();
            }
        }
    }

    public sealed class MLTextResult
    {
        public string OriginalText { get; set; } = string.Empty;
        public string ProcessedText { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public List<string> Improvements { get; set; } = new List<string>();
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public sealed class MLStatistics
    {
        public int TotalTextsProcessed { get; set; }
        public int UniqueWordsLearned { get; set; }
        public int DnnModelsLoaded { get; set; }
        public double AverageConfidence { get; set; }
    }
}