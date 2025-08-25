using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Tesseract;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace P5S_ceviri
{
    public class WindowsOcrService : IOcrService
    {
        private readonly OcrEngine _ocrEngine;
        private readonly ILogger _logger;

        public WindowsOcrService(ILogger logger)
        {
            _logger = logger;
            try
            {
                _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_ocrEngine == null)
                {
                    _logger.LogWarning("Windows OCR Engine could not be initialized with user profile languages. Falling back to English.");
                    var lang = new Language("en-US");
                    if (OcrEngine.IsLanguageSupported(lang))
                    {
                        _ocrEngine = OcrEngine.TryCreateFromLanguage(lang);
                    }
                }

                if (_ocrEngine != null)
                {
                    _logger.LogInformation($"Windows OCR Engine initialized for language: {_ocrEngine.RecognizerLanguage.DisplayName}");
                }
                else
                {
                    _logger.LogError("Windows OCR Engine could not be initialized. Please ensure a supported language pack is installed in Windows and set as a display language.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize Windows OCR Engine. This may happen on Windows versions without the necessary components.", ex);
                _ocrEngine = null;
            }
        }

        public async Task<string> RecognizeTextInRegionsAsync(Bitmap image, string language = "eng", PageSegMode psm = PageSegMode.Auto)
        {
            if (_ocrEngine == null || image == null) return string.Empty;

            try
            {
                SoftwareBitmap softwareBitmap = await CreateSoftwareBitmapFromBitmap(image);
                if (softwareBitmap == null)
                {
                    _logger.LogWarning("SoftwareBitmap could not be created from the source image.");
                    return string.Empty;
                }

                OcrResult ocrResult = await _ocrEngine.RecognizeAsync(softwareBitmap);
                return ocrResult.Text?.Trim().Replace("", " ").Replace("", " ") ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError("An error occurred during Windows OCR recognition.", ex);
                return string.Empty;
            }
        }

        public Task<string> GetTextAdaptiveAsync(Bitmap image, string language, PageSegMode psm = PageSegMode.Auto)
        {
            return RecognizeTextInRegionsAsync(image, language, psm);
        }

        public Task<string> GetTextFromImage(Bitmap image, string language = "eng", bool invertColors = false)
        {
            return RecognizeTextInRegionsAsync(image, language);
        }

        public List<Rectangle> FindTextRegions(Bitmap sourceImage)
        {
            if (sourceImage == null) return new List<Rectangle>();
            return new List<Rectangle> { new Rectangle(0, 0, sourceImage.Width, sourceImage.Height) };
        }

        public Bitmap IsolateTextByColor(Bitmap sourceImage)
        {
            return sourceImage;
        }

        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        public Bitmap CaptureWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return null;
            GetWindowRect(hWnd, out RECT rect);
            if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return null;

            var bmp = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top, PixelFormat.Format32bppArgb);
            using (var gfx = Graphics.FromImage(bmp))
            {
                IntPtr hdc = gfx.GetHdc();
                PrintWindow(hWnd, hdc, 2); // PW_RENDERFULLCONTENT
                gfx.ReleaseHdc(hdc);
            }
            return bmp;
        }

        public Bitmap CropImage(Bitmap image, Rectangle region)
        {
            if (image == null) return null;
            return image.Clone(region, image.PixelFormat);
        }

        private async Task<SoftwareBitmap> CreateSoftwareBitmapFromBitmap(Bitmap bitmap)
        {
            if (bitmap == null) return null;

            using (var stream = new InMemoryRandomAccessStream())
            {
                bitmap.Save(stream.AsStreamForWrite(), System.Drawing.Imaging.ImageFormat.Bmp);
                stream.Seek(0);

                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || softwareBitmap.BitmapAlphaMode == BitmapAlphaMode.Straight)
                {
                    softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                }

                return softwareBitmap;
            }
        }
    }
}