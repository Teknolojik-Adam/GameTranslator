using OpenCvSharp;
using OpenCvSharp.Dnn;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public class PaddleOcrEngine : IOcrEngine, IDisposable
    {
        private readonly ILogger _logger;
        private readonly Net _net;
        private readonly string _alphabet;
        private bool _disposed = false;
        private const int InputHeight = 32;

        public OcrEngineType EngineType => OcrEngineType.PaddleOCR;

        public PaddleOcrEngine(ILogger logger, string modelPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException("PaddleOCR model file not found", modelPath);
            }

            try
            {
                _net = CvDnn.ReadNet(modelPath);
                // PaddleOCR typically uses a specific dictionary.
                // For now we use the default/generic one or we might need a specific one.
                // Assuming the generic one covers most basic chars.
                _alphabet = OcrModelUtils.DefaultAlphabet;
                _logger.LogInformation($"PaddleOCR Engine initialized with model: {modelPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load PaddleOCR model: {modelPath}", ex);
                throw;
            }
        }

        public Task<string> RecognizeTextAsync(Bitmap image, string language, PageSegMode psm = PageSegMode.Auto)
        {
            return Task.Run(() =>
            {
                if (image == null) return string.Empty;

                try
                {
                    using (Mat src = BitmapConverter.ToMat(image))
                    {
                        // Preprocessing for PaddleOCR
                        // Typically: Resize height to 32, maintain aspect ratio,
                        // Normalize: (pixel / 255.0 - 0.5) / 0.5  => (pixel - 127.5) / 127.5

                        Mat processed = new Mat();
                        if (src.Channels() == 1)
                        {
                            Cv2.CvtColor(src, processed, ColorConversionCodes.GRAY2RGB);
                        }
                        else
                        {
                            Cv2.CvtColor(src, processed, ColorConversionCodes.BGR2RGB);
                        }

                        double scale = (double)InputHeight / processed.Height;
                        int newWidth = (int)(processed.Width * scale);
                        // PaddleOCR rec models often require width to be multiple of 32 or specific size
                        // Or they support dynamic width.
                        // Let's ensure a minimum width.
                        newWidth = Math.Max(newWidth, 32);

                        Cv2.Resize(processed, processed, new OpenCvSharp.Size(newWidth, InputHeight));

                        using (Mat blob = CvDnn.BlobFromImage(processed, 1.0 / 127.5, new OpenCvSharp.Size(newWidth, InputHeight), new Scalar(127.5, 127.5, 127.5), false, false))
                        {
                            _net.SetInput(blob);

                            using (Mat prob = _net.Forward())
                            {
                                // Output shape typically: [1, seq_len, num_classes]
                                // We need to decode it.

                                int batch = prob.Size(0);
                                int seqLen = prob.Size(1);
                                int numClasses = prob.Size(2);

                                var classIndices = new List<int>();

                                for (int t = 0; t < seqLen; t++)
                                {
                                    float maxVal = -float.MaxValue;
                                    int maxIdx = -1;

                                    for (int c = 0; c < numClasses; c++)
                                    {
                                        float val = prob.At<float>(0, t, c);
                                        if (val > maxVal)
                                        {
                                            maxVal = val;
                                            maxIdx = c;
                                        }
                                    }
                                    classIndices.Add(maxIdx);
                                }

                                return OcrModelUtils.DecodeCTC(classIndices, _alphabet);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("PaddleOCR recognition failed", ex);
                    return string.Empty;
                }
            });
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _net?.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}
