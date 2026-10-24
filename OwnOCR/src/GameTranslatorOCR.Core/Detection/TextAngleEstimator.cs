using GameTranslatorOCR.Core.Models;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GameTranslatorOCR.Core.Detection;

/// <summary>
/// DBNet haritasındaki olasılık noktalarına PCA uygulayarak baskın satır eğimini
/// tahmin eder. Bu hesap yalnızca eğik OCR açıkken kullanılır; detector modelini
/// ya da mevcut dikdörtgen çıkarma kurallarını değiştirmez.
/// </summary>
public static class TextAngleEstimator
{
    public static OcrQuad? Estimate(Tensor<float> probability, OcrBox bounds,
        int imageWidth, int imageHeight, float threshold = .35f)
    {
        if (!bounds.IsValid || imageWidth < 1 || imageHeight < 1) return null;
        int mw = probability.Dimensions[3], mh = probability.Dimensions[2];
        int xmin = Math.Clamp((int)((long)bounds.X * mw / imageWidth), 0, mw - 1);
        int xmax = Math.Clamp((int)Math.Ceiling((double)(bounds.X + bounds.Width) * mw / imageWidth), 0, mw - 1);
        int ymin = Math.Clamp((int)((long)bounds.Y * mh / imageHeight), 0, mh - 1);
        int ymax = Math.Clamp((int)Math.Ceiling((double)(bounds.Y + bounds.Height) * mh / imageHeight), 0, mh - 1);
        double count = 0, sx = 0, sy = 0, xx = 0, xy = 0, yy = 0;
        var points = new List<(double x,double y)>();
        for (int y = ymin; y <= ymax; y++)
        for (int x = xmin; x <= xmax; x++)
        {
            if (probability[0,0,y,x] <= threshold) continue;
            double px = (x + .5) * imageWidth / mw;
            double py = (y + .5) * imageHeight / mh;
            points.Add((px, py));
            count++; sx += px; sy += py; xx += px * px; yy += py * py; xy += px * py;
        }
        if (count < 50 || bounds.Width < 2.2 * bounds.Height) return null;
        double mx = sx / count, my = sy / count;
        double cxx = xx / count - mx * mx, cyy = yy / count - my * my;
        double cxy = xy / count - mx * my;
        if (cxx <= 0 || cxx < cyy) return null;
        double angle = .5 * Math.Atan2(2 * cxy, cxx - cyy);
        double degrees = angle * 180 / Math.PI;
        if (Math.Abs(degrees) < 6 || Math.Abs(degrees) > 28) return null;

        double ux = Math.Cos(angle), uy = Math.Sin(angle);
        double vx = -uy, vy = ux;
        double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
        double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
        foreach (var pt in points)
        {
            double dx = pt.x - mx, dy = pt.y - my;
            double u = dx * ux + dy * uy, v = dx * vx + dy * vy;
            minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
        }

        // Örnek satır geometrisi güvenilir değilse mevcut crop'a dön.
        if (maxU - minU < bounds.Height * 1.5 ||
            maxV - minV < 5 || maxV - minV > bounds.Height * 1.5)
            return null;

        const double pad = 3;
        minU -= pad; maxU += pad; minV -= pad; maxV += pad;
        OcrPoint Make(double u,double v) =>
            new((float)(mx + u * ux + v * vx),(float)(my + u * uy + v * vy));
        return new OcrQuad(Make(minU,minV),Make(maxU,minV),
            Make(maxU,maxV),Make(minU,maxV));
    }
}
