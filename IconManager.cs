using System;
using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace GameTranslatorUltimate
{
    public sealed class IconManager : IDisposable
    {
        private readonly ILogger _logger;
        private bool _disposed;

        public IconManager(
            ILogger logger = null)
        {
            _logger =
                logger ?? new ConsoleLogger();
        }

        public BitmapImage ProcessIconuAl(
            Process process)
        {
            if (_disposed)
            {
                _logger.LogWarning(
                    "IconManager dispose edilmiş.");

                return null;
            }

            try
            {
                if (process == null)
                {
                    _logger.LogWarning(
                        "Process null.");

                    return null;
                }

                if (process.HasExited)
                {
                    _logger.LogWarning(
                        "Process kapanmış.");

                    return null;
                }

                string filePath =
                    process.MainModule != null
                        ? process.MainModule.FileName
                        : null;

                if (string.IsNullOrWhiteSpace(filePath))
                {
                    _logger.LogWarning(
                        $"Process dosya yolu alınamadı. PID: {process.Id}");

                    return null;
                }

                return LogoHelper.GetProcessIcon(
                    filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Process ikonu alınamadı.",
                    ex);

                return null;
            }
        }

        public BitmapImage BuyukIconAl(
            string dosyaYolu)
        {
            if (_disposed)
                return null;

            return LogoHelper.GetIconFromFilePath(
                dosyaYolu,
                true);
        }

        public BitmapImage KucukIconAl(
            string dosyaYolu)
        {
            if (_disposed)
                return null;

            return LogoHelper.GetIconFromFilePath(
                dosyaYolu,
                false);
        }

        public BitmapImage ResourceIconAl(
            string resourceAdi)
        {
            if (_disposed)
                return null;

            return LogoHelper.GetIconFromResource(
                resourceAdi);
        }

        public void OnbellekDurumuGoster()
        {
            if (_disposed)
                return;

            int iconSayisi =
                LogoHelper.CachedIconCount;

            _logger.LogInformation(
                $"Önbellekte {iconSayisi} adet ikon var.");
        }

        public void OnbellegiTemizle()
        {
            LogoHelper.ClearIconCache();

            _logger.LogInformation(
                "İkon önbelleği temizlendi.");
        }

        public void DosyaOnbelleginiSil(
            string dosyaYolu)
        {
            if (_disposed)
                return;

            if (string.IsNullOrWhiteSpace(
                dosyaYolu))
            {
                _logger.LogWarning(
                    "Önbellekten silinecek dosya yolu boş.");

                return;
            }

            LogoHelper.InvalidateIconCache(
                dosyaYolu);

            _logger.LogInformation(
                $"Dosya önbellekten silindi: {dosyaYolu}");
        }

        public BitmapImage[] ProcessIconlariYukle(
            Process[] processler)
        {
            if (_disposed)
                return new BitmapImage[0];

            if (processler == null ||
                processler.Length == 0)
            {
                return new BitmapImage[0];
            }

            var ikonlar =
                new BitmapImage[processler.Length];

            for (int i = 0;
                 i < processler.Length;
                 i++)
            {
                ikonlar[i] =
                    ProcessIconuAl(
                        processler[i]);
            }

            OnbellekDurumuGoster();

            return ikonlar;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                OnbellekDurumuGoster();

                LogoHelper.ClearIconCache();

                _logger.LogInformation(
                    "IconManager kapatıldı ve ikon önbelleği temizlendi.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "IconManager kapatılırken hata oluştu.",
                    ex);
            }
            finally
            {
                _disposed =
                    true;
            }
        }
    }
}