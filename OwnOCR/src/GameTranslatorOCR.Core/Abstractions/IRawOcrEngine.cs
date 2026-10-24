using GameTranslatorOCR.Core.Models;

namespace GameTranslatorOCR.Core.Abstractions;

/// <summary>
/// Ekran yakalama uygulamalarının PNG/JPEG kodlama maliyetine katlanmadan
/// doğrudan BGRA32 piksel arabelleğini OCR motoruna vermesini sağlar.
/// Piksel düzeni: B,G,R,A (4 bayt), stride satır başına bayt sayısıdır.
/// </summary>
public interface IRawOcrEngine : IOcrEngine
{
    Task<OcrResult> RecognizeBgraAsync(
        ReadOnlyMemory<byte> pixels,
        int width, int height, int stride,
        OcrOptions options,
        CancellationToken cancellationToken = default);
}
