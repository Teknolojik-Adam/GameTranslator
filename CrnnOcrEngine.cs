using OpenCvSharp;
using OpenCvSharp.Dnn;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public class CrnnOcrEngine : IOcrEngine, IDisposable
    {
        private readonly ILogger _logger;
        private readonly Net _net;
        private readonly string _alphabet;
        private bool _disposed = false;
        private const int InputHeight = 32;

        public OcrEngineType EngineType => OcrEngineType.CRNN;

        public CrnnOcrEngine(ILogger logger, string modelPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException("CRNN model file not found", modelPath);
            }

            try
            {
                _net = CvDnn.ReadNet(modelPath);
                _alphabet = OcrModelUtils.DefaultAlphabet;
                _logger.LogInformation($"CRNN OCR Engine initialized with model: {modelPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load CRNN model: {modelPath}", ex);
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
                        // Convert to grayscale
                        Mat gray = new Mat();
                        if (src.Channels() == 3)
                        {
                            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
                        }
                        else
                        {
                            src.CopyTo(gray);
                        }

                        // Resize to fixed height (32) while maintaining aspect ratio
                        double scale = (double)InputHeight / gray.Height;
                        int newWidth = (int)(gray.Width * scale);

                        // CRNN usually expects width to be at least some value, e.g. 100
                        // but dynamic width is often supported by the graph

                        Mat resized = new Mat();
                        Cv2.Resize(gray, resized, new OpenCvSharp.Size(newWidth, InputHeight));

                        // Create blob
                        // Scale factor 1/127.5 and mean 127.5 transforms [0,255] to [-1,1]
                        // verify if your specific model needs [0,1] or [-1,1]
                        // Standard CRNN often uses [-1, 1] normalization.
                        using (Mat blob = CvDnn.BlobFromImage(resized, 1.0/127.5, new OpenCvSharp.Size(newWidth, InputHeight), new Scalar(127.5), true, false))
                        {
                            _net.SetInput(blob);

                            // Forward pass
                            using (Mat prob = _net.Forward())
                            {
                                // prob dims: [batch_size, seq_len, num_classes] (depends on framework)
                                // or [seq_len, batch_size, num_classes]

                                // Parse output
                                // Reshape to 2D: [seq_len, num_classes]
                                // OpenCV DNN usually outputs 2D blobs for this if batch=1
                                int seqLen = prob.Size(0);
                                int numClasses = prob.Size(1);

                                // Sometimes dims are flipped. Let's assume standard output.
                                // If using a frozen .pb from TensorFlow, output is often [1, timesteps, num_classes]
                                // OpenCV flattens.

                                // We need to find the max class for each timestep
                                var classIndices = new List<int>();

                                // Handle different dimensions.
                                // If dims=3: [1, seq_len, num_classes]
                                if (prob.Dims == 3)
                                {
                                    seqLen = prob.Size(1);
                                    numClasses = prob.Size(2);

                                    for (int t = 0; t < seqLen; t++)
                                    {
                                        float maxVal = -float.MaxValue;
                                        int maxIdx = -1;

                                        for (int c = 0; c < numClasses; c++)
                                        {
                                            // Accessing 3D array: [0, t, c]
                                            float val = prob.At<float>(0, t, c);
                                            if (val > maxVal)
                                            {
                                                maxVal = val;
                                                maxIdx = c;
                                            }
                                        }
                                        classIndices.Add(maxIdx);
                                    }
                                }
                                // If dims=2: [seq_len, num_classes]
                                else
                                {
                                    // Depending on model export, it could be [seq_len, num_classes] or [num_classes, seq_len]
                                    // Usually seq_len is larger than num_classes for wide images, but not always.
                                    // Assuming [seq_len, num_classes]
                                    seqLen = prob.Rows;
                                    numClasses = prob.Cols;

                                    for (int i = 0; i < seqLen; i++)
                                    {
                                        // Get row i
                                        using (Mat row = prob.Row(i))
                                        {
                                            Cv2.MinMaxLoc(row, out _, out _, out _, out OpenCvSharp.Point maxLoc);
                                            classIndices.Add(maxLoc.X); // Col index is the class
                                        }
                                    }
                                }

                                return OcrModelUtils.DecodeCTC(classIndices, _alphabet);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("CRNN recognition failed", ex);
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
