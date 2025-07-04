using System;
using System.Drawing;

namespace P5S_ceviri
{
    public interface IOcrService
    {
        Bitmap CaptureWindow(IntPtr hWnd);
        string GetTextFromImage(Bitmap image, string language = "eng", bool invertColors = false);
    }
}