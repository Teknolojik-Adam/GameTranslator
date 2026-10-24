namespace GameTranslatorOCR.Core.Models;

public enum OcrQualityProfile { Speed, Balanced, Quality }

public sealed record OcrOptions(
    string Language = "latin",
    OcrQualityProfile Quality = OcrQualityProfile.Balanced,
    bool PreferGpu = false,
    int RecognitionPaddingPixels = 6,
    bool EnableArtifactFilter = false,
    bool EnablePerspectiveCorrection = false,
    bool AdaptiveDetectionUpscale = false,
    bool AdaptiveRecognitionPadding = false,
    int RecognitionBatchSize = 1,
    bool ReturnWordBoxes = false,
    int DetectionMaxSideOverride = 0,
    bool RestrictTurkishEnglishCharset = false);
