using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace P5S_ceviri
{
    public class ProcessInfo : IDisposable
    {
        private readonly Process _process;
        private readonly ILogger _logger;
        private BitmapImage _iconImage;
        private bool _disposed = false;

        public Process Process => _process;
        public string ProcessName => _process?.ProcessName ?? "Unknown";

        public BitmapImage IconImage
        {
            get
            {
                if (_disposed)
                {
                    _logger?.LogWarning("ProcessInfo dispose edilmiş durumda. IconImage döndürülemiyor.");
                    return new BitmapImage();
                }

                if (_iconImage == null)
                {
                    _iconImage = CreateIconImage();
                }
                return _iconImage;
            }
        }

        public ProcessInfo(Process process, ILogger logger = null)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
            _logger = logger;
        }

        private BitmapImage CreateIconImage()
        {
            BitmapImage bitmapImage = null;
            try
            {
                if (_process.MainModule?.FileName != null)
                {
                    using (Icon ico = Icon.ExtractAssociatedIcon(_process.MainModule.FileName))
                    {
                        if (ico != null)
                        {
                            using (var memoryStream = new MemoryStream())
                            {
                                ico.ToBitmap().Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
                                memoryStream.Position = 0;

                                bitmapImage = new BitmapImage();
                                bitmapImage.BeginInit();
                                bitmapImage.StreamSource = memoryStream;
                                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                                bitmapImage.EndInit();
                                bitmapImage.Freeze();

                                _logger?.LogInformation($"Process ikonu oluşturuldu: {_process.MainModule.FileName}");
                            }
                        }
                        else
                        {
                            _logger?.LogWarning($"Icon oluşturulamadı, varsayılan icon döndürüldü: {_process.MainModule.FileName}");
                            bitmapImage = GetDefaultIcon();
                        }
                    }
                }
                else
                {
                    _logger?.LogWarning($"MainModule bulunamadı, varsayılan icon döndürüldü: {_process.ProcessName}");
                    bitmapImage = GetDefaultIcon();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Icon çıkarma hatası: {_process.MainModule?.FileName}", ex);
                bitmapImage = GetDefaultIcon();
            }

            return bitmapImage ?? new BitmapImage();
        }

        private BitmapImage GetDefaultIcon()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("P5S_ceviri.Resources.default_icon.png"))
                {
                    if (stream != null)
                    {
                        var bitmapImage = new BitmapImage();
                        bitmapImage.BeginInit();
                        bitmapImage.StreamSource = stream;
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.EndInit();
                        bitmapImage.Freeze();
                        _logger?.LogInformation("Varsayılan icon döndürüldü.");
                        return bitmapImage;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Varsayılan icon çıkarma hatası", ex);
            }
            
            return new BitmapImage();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _iconImage = null;
                    _logger?.LogInformation($"ProcessInfo temizlendi: {_process.ProcessName}");
                }
                _disposed = true;
            }
        }

        ~ProcessInfo()
        {
            Dispose(false);
        }

        public override string ToString()
        {
            return $"{ProcessName} (PID: {_process.Id})";
        }
    }
}