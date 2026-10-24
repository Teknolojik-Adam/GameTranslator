using GameTranslatorOCR.Core.Models;

namespace GameTranslatorOCR.Core.Recognition;

/// <summary>
/// Çok küçük tek karakterli yapay OCR kutularını elemeye yönelik geri alınabilir
/// filtre. Modelin gerçek □ simgesini okuması da mümkün olduğundan devre dışı
/// bırakılabilir; filtre çok karakterli metinleri asla reddetmez.
/// </summary>
public static class OcrArtifactFilter
{
    public static bool ShouldReject(RecognitionResult result, OcrBox bounds,
        float minConfidence = .45f)
    {
        if (string.IsNullOrWhiteSpace(result.Text)) return true;
        if (!float.IsFinite(result.Confidence) || result.Confidence < minConfidence)
            return true;
        string text = result.Text.Trim();
        if (text == "□" && bounds.Width <= 32 && bounds.Height <= 24)
            return true;
        return false;
    }
}
