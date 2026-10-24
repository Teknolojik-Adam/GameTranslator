namespace GameTranslatorOCR.Core.Models;

/// <summary>Tek bir OCR satırı. Id, izleyici tarafından sonradan atanır.</summary>
public sealed record OcrLine(string Text, OcrBox Bounds, float Confidence)
{
    /// <summary>Approximate CTC alignment word boxes; opt-in.</summary>
    public IReadOnlyList<OcrWord> Words { get; init; } = Array.Empty<OcrWord>();
    public OcrQuad? Quadrilateral { get; init; }
}
