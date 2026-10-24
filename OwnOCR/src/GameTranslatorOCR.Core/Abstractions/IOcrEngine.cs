using GameTranslatorOCR.Core.Models;

namespace GameTranslatorOCR.Core.Abstractions;

/// <summary>PNG/JPEG gibi kodlanmış görüntü baytlarını OCR sonucuna dönüştürür.</summary>
public interface IOcrEngine : IDisposable
{
    Task<OcrResult> RecognizeAsync(
        ReadOnlyMemory<byte> encodedImage,
        OcrOptions options,
        CancellationToken cancellationToken = default);
}
