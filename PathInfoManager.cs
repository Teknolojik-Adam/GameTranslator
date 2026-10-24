using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace GameTranslatorUltimate
{
    public class ProcessInfo : IDisposable
    {
        private readonly Process _process;
        private readonly ILogger _logger;
        private readonly int _processId;
        private readonly string _processName;

        private BitmapImage _iconImage;
        private string _executablePath;
        private bool _iconLoaded;
        private bool _disposed;

        public Process Process => _process;

        public int ProcessId => _processId;

        public string ProcessName => _processName;

        public string ExecutablePath
        {
            get
            {
                if (_executablePath != null)
                    return _executablePath;

                _executablePath = GetExecutablePath();
                return _executablePath;
            }
        }

        public BitmapImage IconImage
        {
            get
            {
                if (_disposed)
                    return GetDefaultIcon();

                if (!_iconLoaded)
                {
                    _iconImage = CreateIconImage();
                    _iconLoaded = true;
                }

                return _iconImage ?? GetDefaultIcon();
            }
        }

        public ProcessInfo(
            Process process,
            ILogger logger = null)
        {
            _process =
                process ?? throw new ArgumentNullException(nameof(process));

            _logger = logger;

            try
            {
                _processId = process.Id;
            }
            catch
            {
                _processId = -1;
            }

            try
            {
                _processName =
                    process.ProcessName;
            }
            catch
            {
                _processName = "Unknown";
            }
        }

        private BitmapImage CreateIconImage()
        {
            string executablePath =
                ExecutablePath;

            if (string.IsNullOrWhiteSpace(executablePath) ||
                !File.Exists(executablePath))
            {
                return GetDefaultIcon();
            }

            try
            {
                using (Icon icon =
                       Icon.ExtractAssociatedIcon(executablePath))
                {
                    if (icon == null)
                        return GetDefaultIcon();

                    using (Bitmap bitmap =
                           icon.ToBitmap())
                    using (var memoryStream =
                           new MemoryStream())
                    {
                        bitmap.Save(
                            memoryStream,
                            System.Drawing.Imaging.ImageFormat.Png);

                        memoryStream.Position = 0;

                        var image =
                            new BitmapImage();

                        image.BeginInit();

                        image.CacheOption =
                            BitmapCacheOption.OnLoad;

                        image.StreamSource =
                            memoryStream;

                        image.EndInit();
                        image.Freeze();

                        return image;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    $"Process ikonu alınamadı: {_processName}. {ex.Message}");

                return GetDefaultIcon();
            }
        }

        private string GetExecutablePath()
        {
            if (_disposed)
                return null;

            try
            {
                if (_process == null ||
                    _process.HasExited)
                {
                    return null;
                }

                return _process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        private BitmapImage GetDefaultIcon()
        {
            try
            {
                Assembly assembly =
                    Assembly.GetExecutingAssembly();

                using (Stream stream =
                       assembly.GetManifestResourceStream(
                           "GameTranslatorUltimate.Resources.default_icon.png"))
                {
                    if (stream == null)
                        return CreateEmptyImage();

                    var image =
                        new BitmapImage();

                    image.BeginInit();

                    image.CacheOption =
                        BitmapCacheOption.OnLoad;

                    image.StreamSource =
                        stream;

                    image.EndInit();
                    image.Freeze();

                    return image;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    $"Varsayılan process ikonu yüklenemedi: {ex.Message}");

                return CreateEmptyImage();
            }
        }

        private static BitmapImage CreateEmptyImage()
        {
            var image =
                new BitmapImage();

            if (image.CanFreeze)
            {
                image.Freeze();
            }

            return image;
        }

        public override string ToString()
        {
            if (_processId >= 0)
            {
                return
                    $"{_processName} (PID: {_processId})";
            }

            return _processName;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _iconImage = null;
            _executablePath = null;
        }
    }
}