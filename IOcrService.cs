using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using Tesseract;

namespace P5S_ceviri
{
    public interface IOcrService
    {
        Task<string> RecognizeTextInRegionsAsync(Bitmap image, string language, PageSegMode psm = PageSegMode.Auto);

        Task<string> GetTextAdaptiveAsync(Bitmap image, string language, PageSegMode psm = PageSegMode.Auto);
        Task<string> GetTextFromImage(Bitmap image, string language, bool invertColors = false);

        Bitmap CaptureWindow(IntPtr hWnd);
        Bitmap CropImage(Bitmap image, Rectangle region);
        List<Rectangle> FindTextRegions(Bitmap sourceImage);
        Bitmap IsolateTextByColor(Bitmap sourceImage);
        Mat CreateEdgeMask(Mat imageMat);
        Mat CreateContrastMask(Mat imageMat);
        Scalar[] DetectTextColors(Mat hsvImage);
    }
}