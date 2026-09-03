using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public interface IOcrComparisonService
    {
        event EventHandler<OcrComparisonCompletedEventArgs> ComparisonCompleted;

        Task<OcrComparisonResult> CompareEnginesAsync(
            Bitmap image,
            string language);

        Task<OcrComparisonResult> CompareEnginesWithRegionsAsync(
            Bitmap image,
            string language);

        OcrEngineType GetBestEngine(
            OcrComparisonResult result);

        Task<OcrComparisonReport> GenerateComparisonReportAsync(
            List<OcrComparisonResult> results);
    }

    public sealed class OcrComparisonResult : IDisposable
    {
        private Bitmap _sourceImage;
        private bool _disposed;

        public DateTime Timestamp { get; set; }

        public Bitmap SourceImage
        {
            get
            {
                if (_disposed)
                    return null;

                return _sourceImage;
            }

            set
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(
                        nameof(OcrComparisonResult));
                }

                if (ReferenceEquals(
                    _sourceImage,
                    value))
                {
                    return;
                }

                if (_sourceImage != null)
                {
                    _sourceImage.Dispose();
                }

                _sourceImage =
                    value;
            }
        }

        public string Language { get; set; }

        public Dictionary<OcrEngineType, OcrEngineResult> EngineResults { get; set; }

        public OcrEngineType BestEngine { get; set; }

        public double BestConfidence { get; set; }

        public TimeSpan TotalProcessingTime { get; set; }

        public OcrComparisonResult()
        {
            Timestamp =
                DateTime.Now;

            Language =
                string.Empty;

            EngineResults =
                new Dictionary<OcrEngineType, OcrEngineResult>();
        }

        public Bitmap CloneSourceImage()
        {
            if (_disposed ||
                _sourceImage == null)
            {
                return null;
            }

            return new Bitmap(
                _sourceImage);
        }

        public OcrComparisonResult Clone(
            bool includeSourceImage = true)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(OcrComparisonResult));
            }

            var clone =
                new OcrComparisonResult
                {
                    Timestamp =
                        Timestamp,

                    Language =
                        Language ?? string.Empty,

                    BestEngine =
                        BestEngine,

                    BestConfidence =
                        BestConfidence,

                    TotalProcessingTime =
                        TotalProcessingTime
                };

            if (includeSourceImage &&
                _sourceImage != null)
            {
                clone.SourceImage =
                    new Bitmap(
                        _sourceImage);
            }

            if (EngineResults != null)
            {
                foreach (KeyValuePair<OcrEngineType, OcrEngineResult> pair
                         in EngineResults)
                {
                    clone.EngineResults[pair.Key] =
                        pair.Value != null
                            ? pair.Value.Clone()
                            : null;
                }
            }

            return clone;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed =
                true;

            if (_sourceImage != null)
            {
                _sourceImage.Dispose();
                _sourceImage = null;
            }

            if (EngineResults != null)
            {
                EngineResults.Clear();
            }
        }
    }

    public sealed class OcrEngineResult
    {
        public OcrEngineType EngineType { get; set; }

        public string RecognizedText { get; set; }

        public double Confidence { get; set; }

        public TimeSpan ProcessingTime { get; set; }

        public string ErrorMessage { get; set; }

        public bool IsSuccessful { get; set; }

        public Dictionary<string, object> Metadata { get; set; }

        public OcrEngineResult()
        {
            RecognizedText =
                string.Empty;

            ErrorMessage =
                string.Empty;

            Metadata =
                new Dictionary<string, object>();
        }

        public OcrEngineResult Clone()
        {
            var result =
                new OcrEngineResult
                {
                    EngineType =
                        EngineType,

                    RecognizedText =
                        RecognizedText ?? string.Empty,

                    Confidence =
                        Confidence,

                    ProcessingTime =
                        ProcessingTime,

                    ErrorMessage =
                        ErrorMessage ?? string.Empty,

                    IsSuccessful =
                        IsSuccessful
                };

            if (Metadata != null)
            {
                foreach (KeyValuePair<string, object> pair
                         in Metadata)
                {
                    result.Metadata[pair.Key] =
                        pair.Value;
                }
            }

            return result;
        }
    }

    public sealed class OcrComparisonReport
    {
        public DateTime GeneratedAt { get; set; }

        public int TotalComparisons { get; set; }

        public Dictionary<OcrEngineType, EngineAccuracyStats> EngineStats { get; set; }

        public OcrEngineType OverallBestEngine { get; set; }

        public double AverageProcessingTime { get; set; }

        public Dictionary<string, object> Recommendations { get; set; }

        public OcrComparisonReport()
        {
            GeneratedAt =
                DateTime.Now;

            EngineStats =
                new Dictionary<OcrEngineType, EngineAccuracyStats>();

            Recommendations =
                new Dictionary<string, object>();
        }
    }

    public sealed class EngineAccuracyStats
    {
        public OcrEngineType EngineType { get; set; }

        public int TotalTests { get; set; }

        public int SuccessfulTests { get; set; }

        public double SuccessRate { get; set; }

        public double AverageConfidence { get; set; }

        public double AverageProcessingTime { get; set; }

        public double BestConfidence { get; set; }

        public double WorstConfidence { get; set; }

        public int Wins { get; set; }

        public double WinRate { get; set; }

        public EngineAccuracyStats()
        {
            WorstConfidence =
                1.0;
        }
    }

    public sealed class OcrComparisonCompletedEventArgs : EventArgs
    {
        public OcrComparisonResult Result { get; private set; }

        public OcrComparisonCompletedEventArgs(
            OcrComparisonResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(
                    nameof(result));
            }

            Result =
                result;
        }
    }
}