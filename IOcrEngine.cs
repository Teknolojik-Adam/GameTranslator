using System.Drawing;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public interface IOcrEngine
    {
        OcrEngineType EngineType { get; }
        Task<string> RecognizeTextAsync(Bitmap image, string language);
    }
}