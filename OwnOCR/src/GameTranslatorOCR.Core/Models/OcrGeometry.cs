namespace GameTranslatorOCR.Core.Models;

/// <summary>Original ekran piksel koordinatları, sol üst başlangıç.</summary>
public readonly record struct OcrPoint(float X, float Y);

/// <summary>Köşeler sol üst, sağ üst, sağ alt, sol alt sırasındadır.</summary>
public readonly record struct OcrQuad(OcrPoint TopLeft, OcrPoint TopRight,
    OcrPoint BottomRight, OcrPoint BottomLeft)
{
    public float TopWidth => MathF.Sqrt(MathF.Pow(TopRight.X - TopLeft.X, 2) +
        MathF.Pow(TopRight.Y - TopLeft.Y, 2));
    public float LeftHeight => MathF.Sqrt(MathF.Pow(BottomLeft.X - TopLeft.X, 2) +
        MathF.Pow(BottomLeft.Y - TopLeft.Y, 2));
    public float SlopeDegrees => MathF.Atan2(TopRight.Y - TopLeft.Y,
        TopRight.X - TopLeft.X) * 180f / MathF.PI;
}

/// <summary>CTC timestep'lerinden yaklaşık konumu çıkarılan kelime.</summary>
public sealed record OcrWord(string Text, OcrBox Bounds, float Confidence);
