using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace P5S_ceviri
{
  
    public class WindowsOcrEngine : IOcrEngine
    {
        private readonly ILogger _logger;
        private OcrEngine _ocrEngine;

       
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
                }
                else
                {
                    _logger.LogWarning("Windows OCR motoru kullanıcının dil ayarlarıyla başlatılamadı. İngilizce'ye (en-US) fallback yapılıyor.");
                    var lang = new Language("en-US");
                    if (OcrEngine.IsLanguageSupported(lang))
                    {
                        _ocrEngine = OcrEngine.TryCreateFromLanguage(lang);
                        _currentLanguage = lang;
                    }
                }

                if (_ocrEngine != null)
                {
                    _logger.LogInformation($"Windows OCR motoru şu dil için başlatıldı: {_ocrEngine.RecognizerLanguage.DisplayName}");
                }
                else
                {
                    _logger.LogError("Windows OCR motoru başlatılamadı. Lütfen Windows'ta desteklenen bir dil paketinin yüklü olduğundan emin olun.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Windows OCR motoru başlatılırken kritik bir hata oluştu.", ex);
                _ocrEngine = null;
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
                    _ocrEngine = newOcrEngine;
                    _currentLanguage = lang;
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
            // Not: Windows OCR PageSegMode parametresini kullanmaz (Tesseract'a özgü)
            if (_ocrEngine == null)
            {
                _logger.LogWarning("Windows OCR motoru kullanılamıyor, tanıma işlemi atlandı.");
                return string.Empty;
            }
            if (image == null) return string.Empty;

            try
            {

                if (_currentLanguage == null || language != _currentLanguage.LanguageTag)
                {
                    bool languageLoaded = await LoadLanguageAsync(language);
                    if (!languageLoaded)
                    {
                        _logger.LogWarning($"İstenen dil '{language}' yüklenemediği için varsayılan dil '{_currentLanguage?.DisplayName}' ile devam ediliyor.");
                    }
                }

                using (SoftwareBitmap softwareBitmap = await CreateSoftwareBitmapFromBitmap(image))
                {
                    if (softwareBitmap == null)
                    {
                        _logger.LogWarning("Kaynak resimden SoftwareBitmap oluşturulamadı.");
                        return string.Empty;
                    }

                    OcrResult ocrResult = await _ocrEngine.RecognizeAsync(softwareBitmap);
                    return ocrResult.Text?.Trim().Replace("\n", " ").Replace("  ", " ") ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Windows OCR ile metin tanıma sırasında bir hata oluştu.", ex);
                return string.Empty;
            }
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
                    softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                }
                return softwareBitmap;
            }
        }
    }
}