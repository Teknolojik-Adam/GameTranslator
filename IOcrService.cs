using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public interface IOcrService
    {
        Task<string> RecognizeTextInRegionsAsync(Bitmap image, string language = "eng");
        Task<string> GetTextAdaptiveAsync(Bitmap image, string language = "eng");

        Task<string> GetTextFromImage(Bitmap image, string language = "eng", bool invertColors = false);

        Bitmap CaptureWindow(IntPtr hWnd);
        Bitmap CropImage(Bitmap image, Rectangle region);
        List<Rectangle> FindTextRegions(Bitmap sourceImage);
    }
}