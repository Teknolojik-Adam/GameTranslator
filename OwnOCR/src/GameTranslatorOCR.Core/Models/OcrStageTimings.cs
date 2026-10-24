namespace GameTranslatorOCR.Core.Models;

/// <summary>OCR aşama süreleri; milisaniye. Bunlar profil metrikleridir.</summary>
public sealed record OcrStageTimings(
    double DecodeMs,
    double PreprocessMs,
    double DetectionMs,
    double PostprocessMs,
    double CropMs,
    double RecognitionMs,
    double TotalMs);
