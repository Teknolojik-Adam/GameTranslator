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
        private readonly OcrEngine _ocrEngine;

        public OcrEngineType EngineType => OcrEngineType.WindowsOcr;

        public WindowsOcrEngine(ILogger logger)
        {
            _logger = logger;
            try
            {
                // Kullanıcının Windows dil ayarlarına göre OCR motorunu başlatmayı dene
                _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_ocrEngine == null)
                {
                    _logger.LogWarning("Windows OCR motoru kullanıcının dil ayarlarıyla başlatılamadı. İngilizce'ye (en-US) fallback yapılıyor.");
                    var lang = new Language("en-US");
                    if (OcrEngine.IsLanguageSupported(lang))
                    {
                        _ocrEngine = OcrEngine.TryCreateFromLanguage(lang);
                    }
                }

                if (_ocrEngine != null)
                {
                    _logger.LogInformation($"Windows OCR motoru şu dil için başlatıldı: {_ocrEngine.RecognizerLanguage.DisplayName}");
                }
                else
                {
                    // Bu durum genellikle Windows'un N veya KN sürümlerinde medya özellik paketi yüklü olmadığında yaşanır.
                    _logger.LogError("Windows OCR motoru başlatılamadı. Lütfen Windows'ta desteklenen bir dil paketinin yüklü olduğundan emin olun.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Windows OCR motoru başlatılırken kritik bir hata oluştu. Bu, gerekli Windows bileşenlerinin eksik olduğu anlamına gelebilir.", ex);
                _ocrEngine = null;
            }
        }

        public async Task<string> RecognizeTextAsync(Bitmap image, string language)
        {
            // Motor başlatılamadıysa veya geçerli bir resim yoksa, boş string döndür.
            if (_ocrEngine == null)
            {
                _logger.LogWarning("Windows OCR motoru kullanılamıyor, tanıma işlemi atlandı.");
                return string.Empty;
            }
            if (image == null) return string.Empty;

            try
            {
                // Windows OCR API'sinin anlayacağı formata resmi dönüştür.
                using (SoftwareBitmap softwareBitmap = await CreateSoftwareBitmapFromBitmap(image))
                {
                    if (softwareBitmap == null)
                    {
                        _logger.LogWarning("Kaynak resimden SoftwareBitmap oluşturulamadı.");
                        return string.Empty;
                    }

                    // OCR işlemini gerçekleştir.
                    OcrResult ocrResult = await _ocrEngine.RecognizeAsync(softwareBitmap);

                    // Sonucu temizleyip döndür.
                    return ocrResult.Text?.Trim().Replace("\n", " ").Replace("  ", " ") ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Windows OCR ile metin tanıma sırasında bir hata oluştu.", ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// System.Drawing.Bitmap'i Windows OCR API'sinin gerektirdiği SoftwareBitmap formatına dönüştürür.
        /// </summary>
        /// <param name="bitmap">Dönüştürülecek kaynak resim.</param>
        /// <returns>Dönüştürülmüş SoftwareBitmap nesnesi.</returns>
        private async Task<SoftwareBitmap> CreateSoftwareBitmapFromBitmap(Bitmap bitmap)
        {
            if (bitmap == null) return null;

            using (var stream = new InMemoryRandomAccessStream())
            {
                // Bitmap'i bir bellek akışına BMP formatında kaydet.
                bitmap.Save(stream.AsStreamForWrite(), System.Drawing.Imaging.ImageFormat.Bmp);
                stream.Seek(0);

                // Bellek akışından bir BitmapDecoder oluştur.
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

                // Decoder'dan SoftwareBitmap'i al.
                SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                // OCR motorunun en iyi şekilde çalışması için piksel formatını ve alfa modunu ayarla.
                if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || softwareBitmap.BitmapAlphaMode == BitmapAlphaMode.Straight)
                {
                    softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                }

                return softwareBitmap;
            }
        }
    }
}
