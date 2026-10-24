namespace GameTranslatorOCR.Core.Models;

public sealed record OcrResult(
    IReadOnlyList<OcrLine> Lines,
    string Language,
    TimeSpan Elapsed);
