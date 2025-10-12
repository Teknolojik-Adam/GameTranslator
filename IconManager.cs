using System;
using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace P5S_ceviri
{
    public class IconManager
    {
        private readonly ILogger _logger;

        public IconManager(ILogger logger = null)
        {
            _logger = logger ?? new ConsoleLogger();
        }

        // Process için ikon almak için
        public BitmapImage ProcessIconuAl(Process process)
        {
            try
            {
                if (process == null || process.HasExited)
                {
                    _logger.LogWarning("Process null veya kapanmış.");
                    return null;
                }

                string filePath = process.MainModule?.FileName;
                return LogoHelper.GetProcessIcon(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError("Process ikonu alınamadı.", ex);
                return null;
            }
        }

        // Dosya yolundan büyük ikon al
        public BitmapImage BuyukIconAl(string dosyaYolu)
        {
            return LogoHelper.GetIconFromFilePath(dosyaYolu, largeIcon: true);
        }

        // Dosya yolundan küçük ikon al
        public BitmapImage KucukIconAl(string dosyaYolu)
        {
            return LogoHelper.GetIconFromFilePath(dosyaYolu, largeIcon: false);
        }

        // Resource'dan ikon al
        public BitmapImage ResourceIconAl(string resourceAdi)
        {
            return LogoHelper.GetIconFromResource(resourceAdi);
        }

        // Önbellek durumunu kontrol et
        public void OnbellekDurumuGoster()
        {
            int iconSayisi = LogoHelper.CachedIconCount;
            _logger.LogInformation($"Önbellekte {iconSayisi} adet ikon var.");
        }

        // Önbelleği temizle
        public void OnbellegiTemizle()
        {
            LogoHelper.ClearIconCache();
            _logger.LogInformation("Ikon önbelleği temizlendi.");
        }

        // Belirli bir dosyanın önbelleğini sil
        public void DosyaOnbelleginiSil(string dosyaYolu)
        {
            LogoHelper.InvalidateIconCache(dosyaYolu);
            _logger.LogInformation($"Dosya önbellekten silindi: {dosyaYolu}");
        }

        // Process listesi için ikonları yükle
        public BitmapImage[] ProcessIconlariYukle(Process[] processler)
        {
            var ikonlar = new BitmapImage[processler.Length];
            
            for (int i = 0; i < processler.Length; i++)
            {
                ikonlar[i] = ProcessIconuAl(processler[i]);
            }

            OnbellekDurumuGoster();
            return ikonlar;
        }

        // Uygulamadan çıkarken önbelleği temizle
        public void Dispose()
        {
            OnbellekDurumuGoster();
            OnbellegiTemizle();
        }
    }
}

