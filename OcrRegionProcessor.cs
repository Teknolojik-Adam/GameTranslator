using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;

namespace P5S_ceviri
{
    public class OcrRegionProcessor
    {
        private readonly OcrService _ocrService; // OCR işlemlerini gerçekleştiren servis
        private readonly ITranslationService _translationService; // Çeviri işlemlerini gerçekleştiren servis

        private List<Rectangle> _ocrRegions; // OCR işlemi yapılacak bölgeler (x, y, genişlik, yükseklik)
        private Bitmap _previousImage; // Önceki görüntü (değişiklikleri karşılaştırmak için)

       
        public OcrRegionProcessor(OcrService ocrService, ITranslationService translationService)
        {
            _ocrService = ocrService;
            _translationService = translationService;
            _ocrRegions = new List<Rectangle>(); // Başlangıçta boş bir bölge listesi
        }

      
        public void SetOcrRegions(List<Rectangle> regions)
        {
            _ocrRegions = regions ?? new List<Rectangle>(); // Eğer null gelirse boş bir liste ata
        }

        
        public async Task ProcessRegionsAsync(Bitmap currentImage)
        {
            // Eğer önceki görüntü yoksa, ilk görüntüyü kaydedip çık
            if (_previousImage == null)
            {
                _previousImage = currentImage;
                return;
            }

            // Her bir bölge için değişiklik kontrolü ve OCR işlemi
            foreach (var region in _ocrRegions)
            {
                if (IsRegionChanged(_previousImage, currentImage, region))
                {
                    // Değişen bölge için görüntüyü kırp
                    using (var regionImage = _ocrService.CropImage(currentImage, region))
                    {
                        // OCR işlemi yap ve metni al
                        string recognizedText = await Task.Run(() => _ocrService.GetTextFromImage(regionImage, "eng", true));
                        if (!string.IsNullOrWhiteSpace(recognizedText))
                        {
                            // Metni çevir
                            string translatedText = await _translationService.TranslateAsync(recognizedText, "tr");

                            // Metni UI'ye gönder veya başka bir işlem yap
                            // Örneğin, burada bir event tetikleyebilir veya bir callback çağırabilirsin
                            OnOcrRegionProcessed(region, recognizedText, translatedText);
                        }
                    }
                }
            }

            // Yeni görüntüyü önceki görüntü olarak kaydet
            _previousImage = currentImage;
        }

      
        private bool IsRegionChanged(Bitmap previousImage, Bitmap currentImage, Rectangle region)
        {
            for (int y = region.Top; y < region.Bottom; y++)
            {
                for (int x = region.Left; x < region.Right; x++)
                {
                    // Her iki görüntüden de piksel rengini al
                    Color previousColor = previousImage.GetPixel(x, y);
                    Color currentColor = currentImage.GetPixel(x, y);

                    // Pikseller farklıysa bölge değişmiş demektir
                    if (previousColor != currentColor)
                        return true;
                }
            }

            // Tüm pikseller aynıysa bölge değişmemiş
            return false;
        }

      
        protected virtual void OnOcrRegionProcessed(Rectangle region, string recognizedText, string translatedText)
        {
            Console.WriteLine($"[OCR Bölge] X: {region.X}, Y: {region.Y}, Genişlik: {region.Width}, Yükseklik: {region.Height}");
            Console.WriteLine($"[Tanınan Metin] {recognizedText}");
            Console.WriteLine($"[Çevrilmiş Metin] {translatedText}");
        }
    }
}