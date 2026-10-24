using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GameTranslatorUltimate
{
  
    public class WindowsOcrEngine : IOcrEngine
    {
        private readonly ILogger _logger;
        private OcrEngine _ocrEngine;
        private readonly object _engineLock = new object();

        private Language _currentLanguage;

        public OcrEngineType EngineType => OcrEngineType.WindowsOcr;

        public WindowsOcrEngine(ILogger logger)
        {
            _logger = logger;
            InitializeOcrEngine();
        }

        private void InitializeOcrEngine()
        {
            try
            {
                // Varsayılan olarak kullanıcının sistem dilleriyle başlat
                _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_ocrEngine != null)
                {
                    _currentLanguage = _ocrEngine.RecognizerLanguage;
                    _logger.LogInformation($"Windows OCR motoru şu dil için başlatıldı: {_ocrEngine.RecognizerLanguage.DisplayName}");
                    return;
                }
                
                _logger.LogWarning("Windows OCR motoru kullanıcının dil ayarlarıyla başlatılamadı. İngilizce'ye (en-US) fallback yapılıyor.");
                var lang = new Language("en-US");
                if (OcrEngine.IsLanguageSupported(lang))
                {
                    _ocrEngine = OcrEngine.TryCreateFromLanguage(lang);
                    _currentLanguage = lang;
                    _logger.LogInformation($"Windows OCR motoru şu dil için başlatıldı: {_ocrEngine.RecognizerLanguage.DisplayName}");
                    return;
                }

                _logger.LogError("Windows OCR motoru başlatılamadı. Lütfen Windows'ta desteklenen bir dil paketinin yüklü olduğundan emin olun.");
            }
            catch (Exception ex)
            {
                _logger.LogError("Windows OCR motoru başlatılırken kritik bir hata oluştu.", ex);
                _ocrEngine = null;
            }
        }
        
        // Tesseract dil kodlarını (eng, tur) Windows dil kodlarına (en-US, tr-TR) çevirir
        private string MapToWindowsLanguageCode(string inputLanguage)
        {
            if (string.IsNullOrWhiteSpace(inputLanguage)) return "en-US";
            switch (inputLanguage.ToLowerInvariant())
            {
                case "eng": return "en-US";
                case "tur": return "tr-TR";
                case "jpn": return "ja-JP";
                case "ger": case "deu": return "de-DE";
                case "fra": return "fr-FR";
                case "rus": return "ru-RU";
                case "spa": return "es-ES";
                case "ita": return "it-IT";
                case "chi_sim": return "zh-Hans";
                case "chi_tra": return "zh-Hant";
                case "kor": return "ko-KR";
                default: return inputLanguage; // Belki zaten Windows tag formunda verilmiştir
            }
        }


        public async Task<bool> LoadLanguageAsync(string languageCode)
        {

            try
            {
                var lang = new Language(languageCode);
                if (!OcrEngine.IsLanguageSupported(lang))
                {
                    _logger.LogWarning($"Desteklenmeyen dil: {languageCode}");
                    return false;
                }

                var newOcrEngine = OcrEngine.TryCreateFromLanguage(lang);
                if (newOcrEngine != null)
                {
                    lock (_engineLock)
                    {
                        _ocrEngine = newOcrEngine;
                        _currentLanguage = lang;
                    }
                    _logger.LogInformation($"OCR motoru yeni dile ayarlandı: {_ocrEngine.RecognizerLanguage.DisplayName}");
                    return true;
                }

                _logger.LogWarning($"Dil paketi yüklenemedi: {languageCode}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Dil paketi yüklenirken hata oluştu: {languageCode}", ex);
                return false;
            }
        }

        public async Task<string> RecognizeTextAsync(Bitmap image, string language, Tesseract.PageSegMode psm = Tesseract.PageSegMode.Auto)
        {
            OcrEngine engineCopy;
            lock (_engineLock) { engineCopy = _ocrEngine; }
            if (engineCopy == null)
            {
                _logger.LogWarning("Windows OCR motoru kullanılamıyor, tanıma işlemi atlandı.");
                return string.Empty;
            }
            if (image == null) return string.Empty;

            try
            {

                string windowsLanguageCode = MapToWindowsLanguageCode(language);

                Language curLang;
                lock (_engineLock) { curLang = _currentLanguage; }
                if (curLang == null || windowsLanguageCode != curLang.LanguageTag)
                {
                    bool languageLoaded = await LoadLanguageAsync(windowsLanguageCode);
                    if (!languageLoaded)
                    {
                        _logger.LogWarning($"İstenen dil '{windowsLanguageCode}' yüklenemediği için varsayılan dil '{curLang?.DisplayName}' ile devam ediliyor.");
                    }
                    lock (_engineLock) { engineCopy = _ocrEngine; }
                }

                using (SoftwareBitmap softwareBitmap = await CreateSoftwareBitmapFromBitmap(image))
                {
                    if (softwareBitmap == null)
                    {
                        _logger.LogWarning("Kaynak resimden SoftwareBitmap oluşturulamadı.");
                        return string.Empty;
                    }

                    OcrResult ocrResult = await engineCopy.RecognizeAsync(softwareBitmap);
                    string rawText = ocrResult.Text?.Trim() ?? string.Empty;
                    
                    // Hafif temizlik (sadece boşluk/satır sonu normalizasyonu)
                    rawText = CleanOcrText(rawText);
                    
                    return rawText;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Windows OCR ile metin tanıma sırasında bir hata oluştu.", ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// OCR sonuçlarını hafifçe temizler (sadece boşluklar ve satır sonları)
        /// NOT: Eski agresif temizlik kaldırıldı - noktalama, sayılar korunuyor
        /// </summary>
        private string CleanOcrText(string ocrText)
        {
            if (string.IsNullOrWhiteSpace(ocrText)) return string.Empty;
            
            // Sadece boşluk ve satır sonu normalizasyonu
            ocrText = ocrText.Trim();
            ocrText = Regex.Replace(ocrText, @"\r\n|\r|\n", " "); // Satır sonlarını boşluğa çevir
            ocrText = Regex.Replace(ocrText, @"\s{2,}", " ");     // Çoklu boşlukları tek boşluğa indir
            
            return ocrText;
        }

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
                    softwareBitmap.Dispose(); // Eski formatlı bitmap'i bellekten sil
                    softwareBitmap = convertedBitmap;
                }
                return softwareBitmap;
            }
        }
    }
}
