using GameTranslatorOCR.Core.Models;

namespace GameTranslatorOCR.Core.Abstractions;

/// <summary>Görüntüdeki olası metin bölgelerinin piksel kutularını bulur.</summary>
public interface ITextDetector
{
    Task<IReadOnlyList<OcrBox>> DetectAsync(
        ReadOnlyMemory<byte> encodedImage,
        CancellationToken cancellationToken = default);
}
