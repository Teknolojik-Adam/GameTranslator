using System.Drawing;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public class WindowsOcrEngine : IOcrEngine
    {
        private readonly ILogger _logger;

        public OcrEngineType EngineType => OcrEngineType.WindowsOcr;

        public WindowsOcrEngine(ILogger logger)
        {
            _logger = logger;
            _logger.LogWarning("WindowsOcrEngine tam olarak uygulanmamıştır ve sonuç üretmez.");
        }

        public Task<string> RecognizeTextAsync(Bitmap image, string language)
        {

            return Task.FromResult(string.Empty);
        }
    }
}