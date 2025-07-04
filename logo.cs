using System;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;

namespace P5S_ceviri
{
    public static class LogoHelper
    {
        public static BitmapImage GetProcessIcon(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return new BitmapImage();

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
                            return bitmapImage;
                        }
                    }
                }
            }
            catch
            {
                // Hata durumunda boþ bir BitmapImage döndür
            }
            return new BitmapImage();
        }
    }
}
