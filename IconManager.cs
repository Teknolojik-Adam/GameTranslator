using System;
using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace GameTranslatorUltimate
{
    public class IconManager
    {
        private readonly ILogger _logger;

        public IconManager(ILogger logger = null)
        {
            _logger = logger ?? new ConsoleLogger();
        }

        // Process iÃ§in ikon almak iÃ§in
        public BitmapImage ProcessIconuAl(Process process)
        {
            try
            {
                if (process == null || process.HasExited)
                {
                    _logger.LogWarning("Process null veya kapanmÄ±ÅŸ.");
                    return null;
                }

                string filePath = process.MainModule?.FileName;
                return LogoHelper.GetProcessIcon(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError("Process ikonu alÄ±namadÄ±.", ex);
                return null;
            }
        }

        // Dosya yolundan bÃ¼yÃ¼k ikon al
        public BitmapImage BuyukIconAl(string dosyaYolu)
        {
            return LogoHelper.GetIconFromFilePath(dosyaYolu, largeIcon: true);
        }

        // Dosya yolundan kÃ¼Ã§Ã¼k ikon al
        public BitmapImage KucukIconAl(string dosyaYolu)
        {
            return LogoHelper.GetIconFromFilePath(dosyaYolu, largeIcon: false);
        }

        // Resource'dan ikon al
        public BitmapImage ResourceIconAl(string resourceAdi)
        {
            return LogoHelper.GetIconFromResource(resourceAdi);
        }

        // Ã–nbellek durumunu kontrol et
        public void OnbellekDurumuGoster()
        {
            int iconSayisi = LogoHelper.CachedIconCount;
            _logger.LogInformation($"Ã–nbellekte {iconSayisi} adet ikon var.");
        }

        // Ã–nbelleÄŸi temizle
        public void OnbellegiTemizle()
        {
            LogoHelper.ClearIconCache();
            _logger.LogInformation("Ikon Ã¶nbelleÄŸi temizlendi.");
        }

        // Belirli bir dosyanÄ±n Ã¶nbelleÄŸini sil
        public void DosyaOnbelleginiSil(string dosyaYolu)
        {
            LogoHelper.InvalidateIconCache(dosyaYolu);
            _logger.LogInformation($"Dosya Ã¶nbellekten silindi: {dosyaYolu}");
        }

        // Process listesi iÃ§in ikonlarÄ± yÃ¼kle
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

        // Uygulamadan Ã§Ä±karken Ã¶nbelleÄŸi temizle
        public void Dispose()
        {
            OnbellekDurumuGoster();
            OnbellegiTemizle();
        }
    }
}


