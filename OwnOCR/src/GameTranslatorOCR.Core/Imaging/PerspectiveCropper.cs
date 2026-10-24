using GameTranslatorOCR.Core.Models;
using SkiaSharp;

namespace GameTranslatorOCR.Core.Imaging;

/// <summary>
/// Bilgisayarlı görüş kütüphanelerinden bağımsız dört noktalı homografi crop.
/// Destination pikselinden kaynak koordinata çözüm, bilinear renk örneklemesi.
/// OCR'da yalnız eğimi güvenilir kutularda kullanılır.
/// </summary>
public static class PerspectiveCropper
{
    public static SKBitmap Rectify(SKBitmap source, OcrQuad quad)
    {
        ArgumentNullException.ThrowIfNull(source);
        int w = Math.Clamp((int)Math.Round(
            Math.Max(quad.TopWidth, Distance(quad.BottomLeft, quad.BottomRight))), 1, 2048);
        int h = Math.Clamp((int)Math.Round(
            Math.Max(quad.LeftHeight, Distance(quad.TopRight, quad.BottomRight))), 1, 512);
        if (w < 3 || h < 3)
            throw new ArgumentException("Geçersiz dörtgen boyutu.", nameof(quad));
        var output = new SKBitmap(new SKImageInfo(w, h,
            SKColorType.Rgba8888, SKAlphaType.Premul));

        // Unit square (0,0),(1,0),(1,1),(0,1) -> dörtgen.
        var tl = quad.TopLeft; var tr = quad.TopRight;
        var br = quad.BottomRight; var bl = quad.BottomLeft;
        double dx1 = tr.X - br.X, dx2 = bl.X - br.X;
        double dy1 = tr.Y - br.Y, dy2 = bl.Y - br.Y;
        double dx3 = tl.X - tr.X + br.X - bl.X;
        double dy3 = tl.Y - tr.Y + br.Y - bl.Y;
        double g = 0, hPerspective = 0;
        double determinant = dx1 * dy2 - dx2 * dy1;
        if (Math.Abs(dx3) > 1e-6 || Math.Abs(dy3) > 1e-6)
        {
            if (Math.Abs(determinant) < 1e-6)
            {
                output.Dispose();
                throw new ArgumentException("Dejenere dörtgen.", nameof(quad));
            }
            g = (dx3 * dy2 - dx2 * dy3) / determinant;
            hPerspective = (dx1 * dy3 - dx3 * dy1) / determinant;
        }
        double a = tr.X - tl.X + g * tr.X;
        double b = bl.X - tl.X + hPerspective * bl.X;
        double c = tl.X;
        double d = tr.Y - tl.Y + g * tr.Y;
        double e = bl.Y - tl.Y + hPerspective * bl.Y;
        double f = tl.Y;

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double u = (x + .5) / w;
            double v = (y + .5) / h;
            double den = 1 + g * u + hPerspective * v;
            if (Math.Abs(den) < 1e-8) continue;
            double px = (a * u + b * v + c) / den;
            double py = (d * u + e * v + f) / den;
            output.SetPixel(x, y, Sample(source, px, py));
        }
        return output;
    }

    private static float Distance(OcrPoint a, OcrPoint b) =>
        MathF.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));

    private static SKColor Sample(SKBitmap image, double x, double y)
    {
        if (x < 0 || y < 0 || x > image.Width-1 || y > image.Height-1)
            return SKColors.White;
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, image.Width - 1);
        int y1 = Math.Min(y0 + 1, image.Height - 1);
        double tx = x - x0, ty = y - y0;
        var aa = image.GetPixel(x0, y0);
        var bb = image.GetPixel(x1, y0);
        var cc = image.GetPixel(x0, y1);
        var dd = image.GetPixel(x1, y1);
        byte Blend(byte a,byte b,byte c,byte d) =>
            (byte)Math.Clamp((int)Math.Round(
                (a*(1-tx)+b*tx)*(1-ty)+(c*(1-tx)+d*tx)*ty),0,255);
        return new SKColor(Blend(aa.Red,bb.Red,cc.Red,dd.Red),
            Blend(aa.Green,bb.Green,cc.Green,dd.Green),
            Blend(aa.Blue,bb.Blue,cc.Blue,dd.Blue),255);
    }
}
