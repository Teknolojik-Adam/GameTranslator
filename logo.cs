using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace P5S_ceviri
{
    public static class LogoHelper
    {
        #region Fields
        private static readonly Dictionary<string, BitmapImage> _iconCache = new Dictionary<string, BitmapImage>();
        private static readonly object _lockObject = new object();
        private static readonly ILogger _logger = new ConsoleLogger();
        #endregion

        #region Public Properties
        public static int CachedIconCount
        {
            get
            {
                lock (_lockObject)
                {
                    return _iconCache.Count;
                }
            }
        }
        #endregion

        #region Public Methods - Icon Extraction
        public static BitmapImage GetProcessIcon(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                _logger.LogWarning("Dosya yolu boş veya geçersiz.");
                return GetDefaultIcon();
            }

            lock (_lockObject)
            {
                if (_iconCache.TryGetValue(filePath, out BitmapImage cachedIcon))
                {
                    _logger.LogInformation($"Icon önbellekten alındı: {filePath}");
                    return cachedIcon;
                }

                try
                {
                    using (Icon ico = Icon.ExtractAssociatedIcon(filePath))
                    {
                        if (ico != null)
                        {
                            using (var memoryStream = new MemoryStream())
                            {
                                ico.ToBitmap().Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
                                memoryStream.Position = 0;

                                var bitmapImage = new BitmapImage();
                                bitmapImage.BeginInit();
                                bitmapImage.StreamSource = memoryStream;
                                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                                bitmapImage.EndInit();
                                bitmapImage.Freeze();

                                _iconCache[filePath] = bitmapImage;
                                _logger.LogInformation($"Icon önbelleğe eklendi: {filePath}");
                                return bitmapImage;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Icon çıkarma hatası: {filePath}", ex);
                }

                return GetDefaultIcon();
            }
        }

        public static BitmapImage GetIconFromFilePath(string filePath, bool largeIcon = true)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                _logger.LogWarning("Dosya yolu boş, geçersiz veya dosya bulunamadı.");
                return GetDefaultIcon();
            }

            lock (_lockObject)
            {
                string cacheKey = $"{filePath}_{(largeIcon ? "large" : "small")}";
                
                if (_iconCache.TryGetValue(cacheKey, out BitmapImage cachedIcon))
                {
                    _logger.LogInformation($"Icon önbellekten alındı: {cacheKey}");
                    return cachedIcon;
                }

                try
                {
                    uint flags = NativeMethods.SHGFI_ICON | (largeIcon ? NativeMethods.SHGFI_LARGEICON : NativeMethods.SHGFI_SMALLICON);
                    NativeMethods.SHFILEINFO shinfo = new NativeMethods.SHFILEINFO();
                    IntPtr hImgSmall = NativeMethods.SHGetFileInfo(filePath, 0, out shinfo, (uint)Marshal.SizeOf(shinfo), flags);

                    if (shinfo.hIcon != IntPtr.Zero)
                    {
                        using (Icon icon = Icon.FromHandle(shinfo.hIcon))
                        {
                            using (var memoryStream = new MemoryStream())
                            {
                                icon.ToBitmap().Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
                                memoryStream.Position = 0;

                                var bitmapImage = new BitmapImage();
                                bitmapImage.BeginInit();
                                bitmapImage.StreamSource = memoryStream;
                                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                                bitmapImage.EndInit();
                                bitmapImage.Freeze();

                                _iconCache[cacheKey] = bitmapImage;
                                _logger.LogInformation($"Icon önbelleğe eklendi: {cacheKey}");
                                return bitmapImage;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Icon çıkarma hatası: {filePath}", ex);
                }

                return GetDefaultIcon();
            }
        }

        public static BitmapImage GetIconFromResource(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName))
            {
                _logger.LogWarning("Kaynak adı boş veya geçersiz.");
                return new BitmapImage();
            }

            return CreateBitmapImageFromResource(resourceName);
        }
        #endregion

        #region Public Methods - Cache Management
        public static void ClearIconCache()
        {
            lock (_lockObject)
            {
                int count = _iconCache.Count;
                _iconCache.Clear();
                _logger.LogInformation($"Icon önbelleği temizlendi ({count} adet).");
            }
        }

        public static void InvalidateIconCache(string filePath)
        {
            lock (_lockObject)
            {
                if (_iconCache.ContainsKey(filePath))
                {
                    _iconCache.Remove(filePath);
                    _logger.LogInformation($"Icon önbellekten silindi: {filePath}");
                }
                else
                {
                    _logger.LogWarning($"Önbellekte bulunamadı: {filePath}");
                }
            }
        }
        #endregion

        #region Private Methods
        private static BitmapImage GetDefaultIcon()
        {
            _logger.LogInformation("Varsayılan icon döndürüldü.");
            return CreateBitmapImageFromResource("P5S_ceviri.Resources.default_icon.png");
        }
        //
        private static BitmapImage CreateBitmapImageFromResource(string resourceName)
        {
            try
            {
                var assembly = typeof(LogoHelper).Assembly;
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        var bitmapImage = new BitmapImage();
                        bitmapImage.BeginInit();
                        bitmapImage.StreamSource = stream;
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.EndInit();
                        bitmapImage.Freeze();
                        return bitmapImage;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kaynaktan icon çıkarma hatası: {resourceName}", ex);
            }
            return new BitmapImage();
        }
        #endregion

        #region Win32 Interop
        private static class NativeMethods
        {
            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct SHFILEINFO
            {
                public IntPtr hIcon;
                public int iIcon;
                public uint dwAttributes;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
                public string szDisplayName;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
                public string szTypeName;
            }

            public const uint SHGFI_ICON = 0x000000100;
            public const uint SHGFI_LARGEICON = 0x000000000;
            public const uint SHGFI_SMALLICON = 0x000000001;
        }
        #endregion
    }
}
