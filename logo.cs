using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace GameTranslatorUltimate
{
    public static class LogoHelper
    {
        private static readonly Dictionary<string, BitmapImage> _iconCache =
            new Dictionary<string, BitmapImage>(
                StringComparer.OrdinalIgnoreCase);

        private static readonly object _lockObject =
            new object();

        private static readonly ILogger _logger =
            new ConsoleLogger();

        private const string DefaultIconResource =
            "GameTranslatorUltimate.Resources.default_icon.png";

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

        public static BitmapImage GetProcessIcon(
            string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                _logger.LogWarning(
                    "Dosya yolu boş veya geçersiz.");

                return GetDefaultIcon();
            }

            string normalizedPath =
                NormalizePath(filePath);

            string cacheKey =
                "process|" +
                normalizedPath;

            BitmapImage cachedIcon;

            lock (_lockObject)
            {
                if (_iconCache.TryGetValue(
                    cacheKey,
                    out cachedIcon))
                {
                    return cachedIcon;
                }
            }

            BitmapImage iconImage =
                ExtractAssociatedIcon(
                    normalizedPath);

            if (iconImage == null)
            {
                return GetDefaultIcon();
            }

            lock (_lockObject)
            {
                _iconCache[cacheKey] =
                    iconImage;
            }

            return iconImage;
        }

        public static BitmapImage GetIconFromFilePath(
            string filePath,
            bool largeIcon = true)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                _logger.LogWarning(
                    "Dosya yolu boş veya geçersiz.");

                return GetDefaultIcon();
            }

            string normalizedPath =
                NormalizePath(filePath);

            if (!File.Exists(normalizedPath))
            {
                _logger.LogWarning(
                    $"Dosya bulunamadı: {normalizedPath}");

                return GetDefaultIcon();
            }

            string cacheKey =
                "shell|" +
                normalizedPath +
                "|" +
                (largeIcon ? "large" : "small");

            BitmapImage cachedIcon;

            lock (_lockObject)
            {
                if (_iconCache.TryGetValue(
                    cacheKey,
                    out cachedIcon))
                {
                    return cachedIcon;
                }
            }

            BitmapImage iconImage =
                ExtractShellIcon(
                    normalizedPath,
                    largeIcon);

            if (iconImage == null)
            {
                return GetProcessIcon(
                    normalizedPath);
            }

            lock (_lockObject)
            {
                _iconCache[cacheKey] =
                    iconImage;
            }

            return iconImage;
        }

        public static BitmapImage GetIconFromResource(
            string resourceName)
        {
            if (string.IsNullOrWhiteSpace(
                resourceName))
            {
                _logger.LogWarning(
                    "Kaynak adı boş veya geçersiz.");

                return GetDefaultIcon();
            }

            string cacheKey =
                "resource|" +
                resourceName;

            BitmapImage cachedIcon;

            lock (_lockObject)
            {
                if (_iconCache.TryGetValue(
                    cacheKey,
                    out cachedIcon))
                {
                    return cachedIcon;
                }
            }

            BitmapImage image =
                CreateBitmapImageFromResource(
                    resourceName);

            if (image == null)
            {
                return GetDefaultIcon();
            }

            lock (_lockObject)
            {
                _iconCache[cacheKey] =
                    image;
            }

            return image;
        }

        public static void ClearIconCache()
        {
            lock (_lockObject)
            {
                int count =
                    _iconCache.Count;

                _iconCache.Clear();

                _logger.LogInformation(
                    $"Icon önbelleği temizlendi ({count} adet).");
            }
        }

        public static void InvalidateIconCache(
            string filePath)
        {
            if (string.IsNullOrWhiteSpace(
                filePath))
            {
                return;
            }

            string normalizedPath =
                NormalizePath(filePath);

            lock (_lockObject)
            {
                var keysToRemove =
                    new List<string>();

                foreach (string key in
                         _iconCache.Keys)
                {
                    if (key.IndexOf(
                            normalizedPath,
                            StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        keysToRemove.Add(
                            key);
                    }
                }

                for (int i = 0;
                     i < keysToRemove.Count;
                     i++)
                {
                    _iconCache.Remove(
                        keysToRemove[i]);
                }

                if (keysToRemove.Count > 0)
                {
                    _logger.LogInformation(
                        $"Icon önbellekten silindi: {normalizedPath} ({keysToRemove.Count} kayıt)");
                }
            }
        }

        private static BitmapImage ExtractAssociatedIcon(
            string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                using (Icon icon =
                       Icon.ExtractAssociatedIcon(
                           filePath))
                {
                    if (icon == null)
                        return null;

                    return CreateBitmapImageFromIcon(
                        icon);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    $"Icon çıkarma hatası: {filePath}",
                    ex);

                return null;
            }
        }

        private static BitmapImage ExtractShellIcon(
            string filePath,
            bool largeIcon)
        {
            NativeMethods.SHFILEINFO shellInfo =
                new NativeMethods.SHFILEINFO();

            IntPtr result =
                IntPtr.Zero;

            try
            {
                uint flags =
                    NativeMethods.SHGFI_ICON |
                    (largeIcon
                        ? NativeMethods.SHGFI_LARGEICON
                        : NativeMethods.SHGFI_SMALLICON);

                result =
                    NativeMethods.SHGetFileInfo(
                        filePath,
                        0,
                        out shellInfo,
                        (uint)Marshal.SizeOf(
                            typeof(NativeMethods.SHFILEINFO)),
                        flags);

                if (result == IntPtr.Zero ||
                    shellInfo.hIcon == IntPtr.Zero)
                {
                    return null;
                }

                using (Icon icon =
                       (Icon)Icon.FromHandle(
                           shellInfo.hIcon)
                           .Clone())
                {
                    return CreateBitmapImageFromIcon(
                        icon);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    $"Shell icon çıkarma hatası: {filePath}",
                    ex);

                return null;
            }
            finally
            {
                if (shellInfo.hIcon != IntPtr.Zero)
                {
                    NativeMethods.DestroyIcon(
                        shellInfo.hIcon);

                    shellInfo.hIcon =
                        IntPtr.Zero;
                }
            }
        }

        private static BitmapImage CreateBitmapImageFromIcon(
            Icon icon)
        {
            if (icon == null)
                return null;

            try
            {
                using (Bitmap bitmap =
                       icon.ToBitmap())
                using (var memoryStream =
                       new MemoryStream())
                {
                    bitmap.Save(
                        memoryStream,
                        System.Drawing.Imaging.ImageFormat.Png);

                    memoryStream.Position =
                        0;

                    var bitmapImage =
                        new BitmapImage();

                    bitmapImage.BeginInit();

                    bitmapImage.CacheOption =
                        BitmapCacheOption.OnLoad;

                    bitmapImage.StreamSource =
                        memoryStream;

                    bitmapImage.EndInit();

                    bitmapImage.Freeze();

                    return bitmapImage;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Icon BitmapImage formatına dönüştürülemedi.",
                    ex);

                return null;
            }
        }

        private static BitmapImage GetDefaultIcon()
        {
            const string cacheKey =
                "resource|default";

            BitmapImage cached;

            lock (_lockObject)
            {
                if (_iconCache.TryGetValue(
                    cacheKey,
                    out cached))
                {
                    return cached;
                }
            }

            BitmapImage image =
                CreateBitmapImageFromResource(
                    DefaultIconResource);

            if (image == null)
            {
                image =
                    CreateEmptyBitmapImage();
            }

            lock (_lockObject)
            {
                _iconCache[cacheKey] =
                    image;
            }

            return image;
        }

        private static BitmapImage CreateBitmapImageFromResource(
            string resourceName)
        {
            try
            {
                var assembly =
                    typeof(LogoHelper).Assembly;

                using (Stream stream =
                       assembly.GetManifestResourceStream(
                           resourceName))
                {
                    if (stream == null)
                    {
                        _logger.LogWarning(
                            $"Icon kaynağı bulunamadı: {resourceName}");

                        return null;
                    }

                    var bitmapImage =
                        new BitmapImage();

                    bitmapImage.BeginInit();

                    bitmapImage.CacheOption =
                        BitmapCacheOption.OnLoad;

                    bitmapImage.StreamSource =
                        stream;

                    bitmapImage.EndInit();

                    bitmapImage.Freeze();

                    return bitmapImage;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    $"Kaynaktan icon çıkarma hatası: {resourceName}",
                    ex);

                return null;
            }
        }

        private static BitmapImage CreateEmptyBitmapImage()
        {
            var image =
                new BitmapImage();

            if (image.CanFreeze)
            {
                image.Freeze();
            }

            return image;
        }

        private static string NormalizePath(
            string filePath)
        {
            if (string.IsNullOrWhiteSpace(
                filePath))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(
                    filePath.Trim());
            }
            catch
            {
                return filePath.Trim();
            }
        }

        private static class NativeMethods
        {
            [DllImport(
                "shell32.dll",
                CharSet = CharSet.Unicode,
                SetLastError = true)]
            public static extern IntPtr SHGetFileInfo(
                string pszPath,
                uint dwFileAttributes,
                out SHFILEINFO psfi,
                uint cbFileInfo,
                uint uFlags);

            [DllImport(
                "user32.dll",
                SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DestroyIcon(
                IntPtr hIcon);

            [StructLayout(
                LayoutKind.Sequential,
                CharSet = CharSet.Unicode)]
            public struct SHFILEINFO
            {
                public IntPtr hIcon;
                public int iIcon;
                public uint dwAttributes;

                [MarshalAs(
                    UnmanagedType.ByValTStr,
                    SizeConst = 260)]
                public string szDisplayName;

                [MarshalAs(
                    UnmanagedType.ByValTStr,
                    SizeConst = 80)]
                public string szTypeName;
            }

            public const uint SHGFI_ICON =
                0x00000100;

            public const uint SHGFI_LARGEICON =
                0x00000000;

            public const uint SHGFI_SMALLICON =
                0x00000001;
        }
    }
}