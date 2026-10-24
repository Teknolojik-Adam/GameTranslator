using GameTranslatorOCR.Core.Models;

namespace GameTranslatorOCR.Core.Abstractions;

/// <summary>Önceden kırpılmış tek satır görüntüsünü metne dönüştürür.</summary>
public interface ITextRecognizer
{
    Task<RecognitionResult> RecognizeAsync(
        ReadOnlyMemory<byte> encodedLineImage,
        string language,
        CancellationToken cancellationToken = default);
}
