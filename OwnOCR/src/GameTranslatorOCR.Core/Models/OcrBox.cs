namespace GameTranslatorOCR.Core.Models;

/// <summary>Görüntü piksel koordinatlarında eksene paralel metin kutusu.</summary>
public readonly record struct OcrBox(int X, int Y, int Width, int Height)
{
    public bool IsValid => X >= 0 && Y >= 0 && Width > 0 && Height > 0;

    public double IntersectionOverUnion(OcrBox other)
    {
        if (!IsValid || !other.IsValid) return 0;
        long left = Math.Max((long)X, other.X);
        long top = Math.Max((long)Y, other.Y);
        long right = Math.Min((long)X + Width, (long)other.X + other.Width);
        long bottom = Math.Min((long)Y + Height, (long)other.Y + other.Height);
        long intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        long union = (long)Width * Height + (long)other.Width * other.Height - intersection;
        return union > 0 ? (double)intersection / union : 0;
    }
}
