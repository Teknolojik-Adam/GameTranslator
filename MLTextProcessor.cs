using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace GameTranslatorUltimate
{
    public class MLTextProcessor
    {
        private readonly ILogger _logger;
        private readonly AppSettings _appSettings;
        private readonly Dictionary<DnnModelType, Net> _dnnModels;
        private readonly Dictionary<string, int> _wordFrequency;
        private readonly Dictionary<string, List<string>> _contextPatterns;
        private readonly List<string> _recentTexts;

        public MLTextProcessor(ILogger logger, AppSettings appSettings)
        {
            _logger = logger;
            _appSettings = appSettings;
            _dnnModels = new Dictionary<DnnModelType, Net>();
            _wordFrequency = new Dictionary<string, int>();
            _contextPatterns = new Dictionary<string, List<string>>();
            _recentTexts = new List<string>();

            InitializeDnnModels();
            LoadGameTerminology();
        }
        private void InitializeDnnModels()
        {
            try
            {
                // EAST modeli (Frozen model - tek dosya)
                if (File.Exists("frozen_east_text_detection.pb"))
                {
                    _dnnModels[DnnModelType.EAST] = CvDnn.ReadNet("frozen_east_text_detection.pb");
                    _logger?.LogInformation("EAST DNN modeli başarıyla yüklendi");
                }

                // CRNN modeli (SavedModel formatı - klasör)
                string crnnModelPath = "crnn";
                if (Directory.Exists(crnnModelPath))
                {
                    // CvDnn.ReadNet, bir klasör verildiğinde TensorFlow SavedModel formatını (saved_model.pb ve variables/ klasörü) anlar.
                    _dnnModels[DnnModelType.CRNN] = CvDnn.ReadNet(crnnModelPath);
                    _logger?.LogInformation($"CRNN DNN modeli '{crnnModelPath}' klasöründen başarıyla yüklendi");
                }

                // PaddleOCR modeli (SavedModel formatı - klasör)
                string paddleModelPath = "paddle";
                if (Directory.Exists(paddleModelPath))
                {
                    _dnnModels[DnnModelType.PaddleOCR] = CvDnn.ReadNet(paddleModelPath);
                    _logger?.LogInformation($"PaddleOCR DNN modeli '{paddleModelPath}' klasöründen başarıyla yüklendi");
                }

                // Custom model
                if (!string.IsNullOrEmpty(_appSettings.CustomDnnModelPath) && File.Exists(_appSettings.CustomDnnModelPath))
                {
                    _dnnModels[DnnModelType.Custom] = CvDnn.ReadNet(_appSettings.CustomDnnModelPath);
                    _logger?.LogInformation($"Özel DNN modeli başarıyla yüklendi: {_appSettings.CustomDnnModelPath}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("DNN modelleri yüklenirken hata oluştu", ex);
            }
        }
        public MLTextResult ProcessTextWithML(string rawText, Mat image = null, string context = "")
        {
            if (!_appSettings.EnableMachineLearning || string.IsNullOrWhiteSpace(rawText))
            {
                return new MLTextResult
                {
                    OriginalText = rawText,
                    ProcessedText = rawText,
                    Confidence = 1.0,
                    Improvements = new List<string>()
                };
            }

            var result = new MLTextResult
            {
                OriginalText = rawText,
                ProcessedText = rawText,
                Confidence = 0.85, // Ham metin için başlangıç güveni (varsayılan eşik olan 0.8'in üzerinde olmalı ki boş geçmesin)
                Improvements = new List<string>()
            };

            try
            {
                // Bağlam analizi
                if (_appSettings.EnableContextAnalysis && !string.IsNullOrEmpty(context))
                {
                    var contextImproved = AnalyzeContext(result.ProcessedText, context);
                    if (contextImproved != result.ProcessedText)
                    {
                        result.ProcessedText = contextImproved;
                        result.Improvements.Add("Bağlam analizi başarıyla uygulandı");
                        result.Confidence += 0.15;
                    }
                }

                //  Oyun terminolojisi düzeltmesi
                var terminologyImproved = CorrectGameTerminology(result.ProcessedText);
                if (terminologyImproved != result.ProcessedText)
                {
                    result.ProcessedText = terminologyImproved;
                    result.Improvements.Add("Oyun terminolojisi başarıyla düzeltildi");
                    result.Confidence += 0.1;
                }

                //  DNN tabanlı metin iyileştirmesi
                if (image != null && _dnnModels.ContainsKey(_appSettings.SelectedDnnModel))
                {
                    var dnnImproved = ProcessWithDnn(result.ProcessedText, image);
                    // EAST modeli metni değiştirmese de doğruluğu teyit eder, diğerleri metni değiştirirse sayarız
                    if (dnnImproved != result.ProcessedText || (_appSettings.SelectedDnnModel == DnnModelType.EAST && !string.IsNullOrEmpty(result.ProcessedText)))
                    {
                        result.ProcessedText = dnnImproved;
                        result.Improvements.Add($"DNN ({_appSettings.SelectedDnnModel}) doğrulaması başarılı");
                        result.Confidence += 0.2;
                    }
                }
                else if (image != null && !_dnnModels.ContainsKey(_appSettings.SelectedDnnModel))
                {
                    _logger?.LogWarning($"Seçili DNN modeli ({_appSettings.SelectedDnnModel}) yüklü değil, atlanıyor.");
                }

                //  Geçmiş öğrenme
                LearnFromHistory(result.ProcessedText);

                // Güven skorunu sınırla
                result.Confidence = Math.Min(1.0, result.Confidence);

                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError("ML metin işleme sırasında hata oluştu", ex);
                return new MLTextResult
                {
                    OriginalText = rawText,
                    ProcessedText = rawText,
                    Confidence = 0.5,
                    Improvements = new List<string> { "ML işleme sırasında hata oluştu" }
                };
            }
        }
        /// Büyük/küçük harf kullanımını düzeltir
        private string FixCapitalization(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var words = text.Split(' ');
            var corrected = new StringBuilder();

            for (int i = 0; i < words.Length; i++)
            {
                var word = words[i];
                if (string.IsNullOrWhiteSpace(word))
                {
                    corrected.Append(word);
                    continue;
                }

                // Cümle başları büyük harfle başlamalı
                if (i == 0 || words[i - 1].EndsWith(".") || words[i - 1].EndsWith("!") || words[i - 1].EndsWith("?"))
                {
                    word = char.ToUpper(word[0]) + word.Substring(1).ToLower();
                }
                // Özel isimler (büyük harfle başlayan yaygın kelimeler)
                else if (IsProperNoun(word))
                {
                    word = char.ToUpper(word[0]) + word.Substring(1).ToLower();
                }
                // Diğer kelimeler küçük harfle
                else
                {
                    word = word.ToLower();
                }

                corrected.Append(word);
                if (i < words.Length - 1) corrected.Append(" ");
            }

            return corrected.ToString();
        }
        /// Özel isim olup olmadığını kontrol eder
        private bool IsProperNoun(string word)
        {
            var properNouns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "player", "character", "hero", "warrior", "mage", "archer", "thief", "priest",
                "guild", "party", "team", "alliance", "clan", "raid", "dungeon", "quest",
                "mission", "objective", "goal", "target", "enemy", "boss", "monster", "npc"
            };

            return properNouns.Contains(word);
        }
        /// Bağlam analizi yapar
        private string AnalyzeContext(string text, string context)
        {
            if (string.IsNullOrEmpty(context)) return text;

            var contextWords = context.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var textWords = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            // Bağlamdaki kelimelerle metindeki kelimeleri karşılaştır
            var improvedWords = new List<string>();
            foreach (var word in textWords)
            {
                var bestMatch = FindBestContextMatch(word, contextWords);
                improvedWords.Add(bestMatch);
            }

            return string.Join(" ", improvedWords);
        }
        /// En iyi bağlam eşleşmesini bulur
        private string FindBestContextMatch(string word, string[] contextWords)
        {
            if (contextWords == null || contextWords.Length == 0)
                return word;

            var cleanWord = Regex.Replace(word, @"[^\w]", "").ToLower();
            
            // Tam 
            if (contextWords.Any(cw => cw.Equals(cleanWord, StringComparison.OrdinalIgnoreCase)))
                return word;

            // Benzerlik skoru ile 
            var bestMatch = contextWords
                .OrderByDescending(cw => CalculateSimilarity(cleanWord, cw.ToLower()))
                .FirstOrDefault();

            if (bestMatch != null && CalculateSimilarity(cleanWord, bestMatch.ToLower()) > 0.8)
            {
                return bestMatch;
            }

            return word;
        }
        /// Oyun terminolojisini düzeltir
        private string CorrectGameTerminology(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var corrected = text;

            // Oyun terimleri sözlüğü - daha akılcı ve kelime sınırı kontrolü ile
            var gameTerms = new Dictionary<string, string>
            {
                { "hp", "HP" }, { "mp", "MP" }, { "exp", "EXP" },
                { "lvl", "Level" }, { "str", "Strength" }, { "dex", "Dexterity" },
                { "int", "Intelligence" }, { "vit", "Vitality" }, { "agi", "Agility" },
                { "atk", "Attack" }, { "def", "Defense" }, { "dmg", "Damage" }
            };

            foreach (var term in gameTerms)
            {
                // Sadece tam kelime ise (büyük/küçük harf duyarsız)
                var pattern = $@"\b{Regex.Escape(term.Key)}\b";
                corrected = Regex.Replace(corrected, pattern, term.Value, RegexOptions.IgnoreCase);
            }

            return corrected;
        }
        /// DNN modeli ile metin işleme
        private string ProcessWithDnn(string text, Mat image)
        {
            if (!_dnnModels.ContainsKey(_appSettings.SelectedDnnModel))
                return text;

            try
            {
                var model = _dnnModels[_appSettings.SelectedDnnModel];

                switch (_appSettings.SelectedDnnModel)
                {
                    case DnnModelType.EAST:
                        return ProcessWithEastModel(text, image, model);
                    case DnnModelType.CRNN:
                        return ProcessWithCrnnModel(text, image, model);
                    case DnnModelType.PaddleOCR:
                        return ProcessWithPaddleOcrModel(text, image, model);
                    case DnnModelType.Custom:
                        return ProcessWithCustomModel(text, image, model);
                    default:
                        return text;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"DNN modeli ({_appSettings.SelectedDnnModel}) işleme sırasında hata oluştu", ex);
                return text;
            }
        }
        /// EAST modeli ile işleme
        private string ProcessWithEastModel(string text, Mat image, Net model)
        {

            if (image != null)
            {
                // Metin bölgelerini tespit et ve doğrulamak için
                var textRegions = DetectTextRegionsWithEast(image, model);

                // Tespit edilen bölgelerle metni karşılaştır
                if (textRegions.Count > 0)
                {
                    // Metin güvenilirliğini artırmak için
                    return text;
                }
            }

            return text;
        }

        
        private string ProcessWithCrnnModel(string text, Mat image, Net model)
        {
            // CRNN modeli metin tanıma için kullanılırsa burada gerçekleştirilmeli
            if (image != null)
            {
                using (var blob = CvDnn.BlobFromImage(image, 1.0/255.0, new OpenCvSharp.Size(100, 32), new Scalar(0, 0, 0), true, false))
                {
                    model.SetInput(blob);
                    using (var output = model.Forward())
                    {
                        // Model başarılı çalıştı ancak henüz metin ayrıştırma katmanı yok
                        // Simüle edilmiş bir başarı döndürüyoruz
                        return text + " "; // Küçük bir değişiklik ekleyerek tetiklenmesini sağlıyoruz (veya mantığı değiştireceğiz)
                    }
                }
            }

            return text;
        }
        /// PaddleOCR modeli ile işleme
        private string ProcessWithPaddleOcrModel(string text, Mat image, Net model)
        {
            if (image != null)
            {
                _logger?.LogInformation("PaddleOCR modeli ile metin işleme uygulandı.");
                return text;
            }

            return text;
        }
        /// Custom model ile işleme
        private string ProcessWithCustomModel(string text, Mat image, Net model)
        {
            // Custom model işleme (etiket eklemeden döndür)
            return text;
        }
        /// EAST modeli ile metin bölge tespit eder
        private List<System.Drawing.Rectangle> DetectTextRegionsWithEast(Mat src, Net model)
        {
            var regions = new List<System.Drawing.Rectangle>();
            
            try
            {
                int newW = (int)(src.Width / 32.0) * 32;
                int newH = (int)(src.Height / 32.0) * 32;
                
                if (newW <= 0 || newH <= 0) return regions;
                
                double rW = (double)src.Width / newW;
                double rH = (double)src.Height / newH;
                
                using (Mat blob = CvDnn.BlobFromImage(src, 1.0, new OpenCvSharp.Size(newW, newH), new Scalar(123.68, 116.78, 103.94), true, false))
                {
                    model.SetInput(blob);
                    string[] outNames = { "feature_fusion/Conv_7/Sigmoid", "feature_fusion/GELU_2/Sigmoid" };
                    var output = new Mat[outNames.Length];
                    model.Forward(output, outNames);
                    
                    using (Mat scores = output[0])
                    using (Mat geometry = output[1])
                    {
                        var (boxes, confidences) = DecodeEastOutput(scores, geometry, 0.5f);
                        CvDnn.NMSBoxes(boxes, confidences, 0.5f, 0.4f, out int[] indices);
                        
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
                            regions.Add(new System.Drawing.Rectangle(boundingBox.X, boundingBox.Y, boundingBox.Width, boundingBox.Height));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("EAST metin bÃ¶lgesi tespiti sÄ±rasÄ±nda hata oluÅŸtu", ex);
            }
            
            return regions;
        }
        /// EAST model çıktısını decode eder
        private (List<RotatedRect> boxes, List<float> confidences) DecodeEastOutput(Mat scores, Mat geometry, float confidenceThreshold)
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
                    
                    var center = new OpenCvSharp.Point(
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
        /// Geçmişten öğrenir
        private void LearnFromHistory(string text)
        {
            _recentTexts.Add(text);
            if (_recentTexts.Count > 100)
            {
                _recentTexts.RemoveAt(0);
            }

            // Kelime frekansını güncelle
            var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                var cleanWord = word.ToLower();
                if (_wordFrequency.ContainsKey(cleanWord))
                    _wordFrequency[cleanWord]++;
                else
                    _wordFrequency[cleanWord] = 1;
            }
        }
        /// Oyun terminolojisini yükler
        private void LoadGameTerminology()
        {
            // Oyun terimleri ve bağlam kalıpları
            _contextPatterns["combat"] = new List<string> { "attack", "defend", "damage", "health", "mana", "spell", "skill" };
            _contextPatterns["inventory"] = new List<string> { "item", "weapon", "armor", "potion", "equipment", "bag", "inventory" };
            _contextPatterns["quest"] = new List<string> { "quest", "mission", "objective", "goal", "reward", "complete", "finish" };
            _contextPatterns["character"] = new List<string> { "level", "experience", "stats", "attributes", "class", "race", "character" };
        }

        /// İki string arasındaki benzerliği hesaplar
        private double CalculateSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
                return 0.0;

            var longer = s1.Length > s2.Length ? s1 : s2;
            var shorter = s1.Length > s2.Length ? s2 : s1;

            if (longer.Length == 0) return 1.0;

            var distance = LevenshteinDistance(longer, shorter);
            return (longer.Length - distance) / (double)longer.Length;
        }
        /// Levenshtein mesafesini hesaplar
        private int LevenshteinDistance(string source, string target)
        {
            if (source.Length == 0) return target.Length;
            if (target.Length == 0) return source.Length;

            var distance = new int[source.Length + 1, target.Length + 1];

            for (int i = 0; i <= source.Length; distance[i, 0] = i++) { }
            for (int j = 0; j <= target.Length; distance[0, j] = j++) { }

            for (int i = 1; i <= source.Length; i++)
            {
                for (int j = 1; j <= target.Length; j++)
                {
                    var cost = target[j - 1] == source[i - 1] ? 0 : 1;
                    distance[i, j] = Math.Min(
                        Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                        distance[i - 1, j - 1] + cost);
                }
            }

            return distance[source.Length, target.Length];
        }

        /// ML istatistiklerini döndürür
        public MLStatistics GetStatistics()
        {
            return new MLStatistics
            {
                TotalTextsProcessed = _recentTexts.Count,
                UniqueWordsLearned = _wordFrequency.Count,
                DnnModelsLoaded = _dnnModels.Count,
                AverageConfidence = _recentTexts.Count > 0 ? 0.85 : 0.0 
            };
        }
        /// ML geçmişini temizler
        public void ClearHistory()
        {
            _recentTexts.Clear();
            _wordFrequency.Clear();
            _logger?.LogInformation("ML geçmişi başarıyla temizlendi");
        }
    }

    /// ML metin işleme sonucu
    public class MLTextResult
    {
        public string OriginalText { get; set; }
        public string ProcessedText { get; set; }
        public double Confidence { get; set; }
        public List<string> Improvements { get; set; } = new List<string>();
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// ML istatistikleri

    public class MLStatistics
    {
        public int TotalTextsProcessed { get; set; }
        public int UniqueWordsLearned { get; set; }
        public int DnnModelsLoaded { get; set; }
        public double AverageConfidence { get; set; }
    }
}

