using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Tesseract;

namespace P5S_ceviri
{
    public class OcrService : IOcrService
    {
        #region Win32 Imports for Window Capture

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        #endregion

        public Bitmap CaptureWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return null;
            try
            {
                GetWindowRect(hWnd, out RECT rect);
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                if (width <= 0 || height <= 0) return null;

                var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var gfx = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = gfx.GetHdc();
                    try
                    {
                        PrintWindow(hWnd, hdc, 2);
                    }
                    finally
                    {
                        gfx.ReleaseHdc(hdc);
                    }
                }
                return bmp;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Pencere yakalama hatası: {ex.Message}");
                return null;
            }
        }

        public string GetTextFromImage(Bitmap image, string language = "eng", bool invertColors = false)
        {
            if (image == null) return string.Empty;

            try
            {
                if (invertColors)
                {
                    InvertBitmapColors(image);
                }

                using (var preprocessedImage = PreprocessImageForOcr(image))
                {
                    using (var engine = new TesseractEngine(@"./tessdata", language, EngineMode.LstmOnly))
                    {
                        engine.SetVariable("user_defined_dpi", "300");
                        using (var page = engine.Process(preprocessedImage))
                        {
                            var text = page.GetText();
                            return text?.Trim().Replace("\n", " ").Replace("  ", " ") ?? string.Empty;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return $"OCR Hatası: {ex.Message}";
            }
        }

        private void InvertBitmapColors(Bitmap bmp)
        {
            var bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, bmp.PixelFormat);
            int bytesPerPixel = Bitmap.GetPixelFormatSize(bmp.PixelFormat) / 8;
            int byteCount = bmpData.Stride * bmp.Height;
            byte[] pixels = new byte[byteCount];
            Marshal.Copy(bmpData.Scan0, pixels, 0, byteCount);

            for (int i = 0; i < byteCount; i += bytesPerPixel)
            {
                pixels[i] = (byte)(255 - pixels[i]);         // Blue
                pixels[i + 1] = (byte)(255 - pixels[i + 1]); // Green
                pixels[i + 2] = (byte)(255 - pixels[i + 2]); // Red
            }

            Marshal.Copy(pixels, 0, bmpData.Scan0, byteCount);
            bmp.UnlockBits(bmpData);
        }

        private Bitmap PreprocessImageForOcr(Bitmap image)
        {
            // 1. Görüntüyü yeniden boyutlandır
            image = ResizeImage(image);

            // 2. Görüntüyü gri tonlamaya çevir
            using (var grayscale = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(grayscale))
                {
                    var colorMatrix = new ColorMatrix(new float[][] {
                        new float[] {.3f, .3f, .3f, 0, 0},
                        new float[] {.59f, .59f, .59f, 0, 0},
                        new float[] {.11f, .11f, .11f, 0, 0},
                        new float[] {0, 0, 0, 1, 0},
                        new float[] {0, 0, 0, 0, 1}
                    });
                    using (var attributes = new ImageAttributes())
                    {
                        attributes.SetColorMatrix(colorMatrix);
                        graphics.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height),
                            0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
                    }
                }

                // 3. Sabit bir threshold değeri kullanarak görüntüyü siyah-beyaz hale getir
                int threshold = 180; // eşik değeri
                return BinarizeImage(grayscale, threshold);
            }
        }

        private Bitmap ResizeImage(Bitmap image)
        {
            // Görüntüyü yarı boyutuna düşürmek için 
            return new Bitmap(image, new Size(image.Width / 2, image.Height / 2));
        }
        public Bitmap CropImage(Bitmap image, Rectangle region)
        {
            // Belirtilen bölgeyi kırp ve yeni bir Bitmap döndürüyor
            return image.Clone(region, image.PixelFormat);
        }
        private Bitmap BinarizeImage(Bitmap grayscaleImage, int threshold)
        {
            Bitmap binarized = null;
            BitmapData grayscaleData = null;
            BitmapData binarizedData = null;

            try
            {
                grayscaleData = grayscaleImage.LockBits(new Rectangle(0, 0, grayscaleImage.Width, grayscaleImage.Height), ImageLockMode.ReadOnly, grayscaleImage.PixelFormat);
                int grayStride = grayscaleData.Stride;
                int grayByteCount = Math.Abs(grayStride) * grayscaleImage.Height;
                byte[] grayValues = new byte[grayByteCount];
                Marshal.Copy(grayscaleData.Scan0, grayValues, 0, grayByteCount);
                grayscaleImage.UnlockBits(grayscaleData);
                grayscaleData = null;

                binarized = new Bitmap(grayscaleImage.Width, grayscaleImage.Height, PixelFormat.Format1bppIndexed);
                binarizedData = binarized.LockBits(new Rectangle(0, 0, binarized.Width, binarized.Height), ImageLockMode.WriteOnly, binarized.PixelFormat);
                int binStride = binarizedData.Stride;
                int binByteCount = Math.Abs(binStride) * binarized.Height;
                byte[] binValues = new byte[binByteCount];

                for (int y = 0; y < grayscaleImage.Height; y++)
                {
                    for (int x = 0; x < grayscaleImage.Width; x++)
                    {
                        byte grayValue = grayValues[y * grayStride + x * 4];
                        if (grayValue >= threshold)
                        {
                            int index = y * binStride + (x / 8);
                            binValues[index] |= (byte)(128 >> (x % 8));
                        }
                    }
                }

                Marshal.Copy(binValues, 0, binarizedData.Scan0, binByteCount);
            }
            finally
            {
                if (grayscaleData != null) grayscaleImage.UnlockBits(grayscaleData);
                if (binarizedData != null) binarized?.UnlockBits(binarizedData);
            }
            return binarized;
        }

    }
}