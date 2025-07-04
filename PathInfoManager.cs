using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;

namespace P5S_ceviri
{
    public class ProcessInfo
    {
        public Process Process { get; }
        public string ProcessName => Process.ProcessName;
        private BitmapImage _iconImage;

        public BitmapImage IconImage
        {
            get
            {
                if (_iconImage == null)
                {
                    _iconImage = CreateIconImage();
                }
                return _iconImage;
            }
        }

        public ProcessInfo(Process process)
        {
            Process = process ?? throw new ArgumentNullException(nameof(process));
        }

        private BitmapImage CreateIconImage()
        {
            BitmapImage bitmapImage = null;
            try
            {
                if (Process.MainModule?.FileName != null)
                {
                    using (Icon ico = Icon.ExtractAssociatedIcon(Process.MainModule.FileName))
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
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
               
            }
            return bitmapImage ?? new BitmapImage();
        }
    }
}