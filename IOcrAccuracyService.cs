using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public interface IOcrAccuracyService
    {
        Task<OcrAccuracyScore> CalculateAccuracyAsync(
            string recognizedText,
            string groundTruth);

        Task<OcrAccuracyScore> CalculateAccuracyWithImageAsync(
            Bitmap image,
            string recognizedText,
            string groundTruth);

        Task<OcrAccuracyReport> GenerateDetailedReportAsync(
            List<OcrTestResult> testResults);

        Task<OcrAccuracyScore> CalculateConfidenceScoreAsync(
            string recognizedText,
            Bitmap sourceImage);
    }

    public sealed class OcrAccuracyScore
    {
        public double OverallScore { get; set; }

        public double CharacterAccuracy { get; set; }

        public double WordAccuracy { get; set; }

        public double LineAccuracy { get; set; }

        public double ConfidenceScore { get; set; }

        public int CharacterErrors { get; set; }

        public int WordErrors { get; set; }

        public int LineErrors { get; set; }

        public int TotalCharacters { get; set; }

        public int TotalWords { get; set; }

        public int TotalLines { get; set; }

        public List<string> ErrorDetails { get; set; }

        public Dictionary<string, double> DetailedMetrics { get; set; }

        public OcrAccuracyScore()
        {
            ErrorDetails =
                new List<string>();

            DetailedMetrics =
                new Dictionary<string, double>();
        }

        public OcrAccuracyScore Clone()
        {
            var clone =
                new OcrAccuracyScore
                {
                    OverallScore =
                        OverallScore,

                    CharacterAccuracy =
                        CharacterAccuracy,

                    WordAccuracy =
                        WordAccuracy,

                    LineAccuracy =
                        LineAccuracy,

                    ConfidenceScore =
                        ConfidenceScore,

                    CharacterErrors =
                        CharacterErrors,

                    WordErrors =
                        WordErrors,

                    LineErrors =
                        LineErrors,

                    TotalCharacters =
                        TotalCharacters,

                    TotalWords =
                        TotalWords,

                    TotalLines =
                        TotalLines
                };

            if (ErrorDetails != null)
            {
                clone.ErrorDetails.AddRange(
                    ErrorDetails);
            }

            if (DetailedMetrics != null)
            {
                foreach (KeyValuePair<string, double> pair
                         in DetailedMetrics)
                {
                    clone.DetailedMetrics[pair.Key] =
                        pair.Value;
                }
            }

            return clone;
        }
    }

    public sealed class OcrTestResult : IDisposable
    {
        private Bitmap _sourceImage;
        private bool _disposed;

        public string TestId { get; set; }

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
                        nameof(OcrTestResult));
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

        public string GroundTruth { get; set; }

        public string RecognizedText { get; set; }

        public OcrEngineType EngineType { get; set; }

        public DateTime TestTime { get; set; }

        public TimeSpan ProcessingTime { get; set; }

        public OcrAccuracyScore AccuracyScore { get; set; }

        public Dictionary<string, object> Metadata { get; set; }

        public OcrTestResult()
        {
            TestId =
                string.Empty;

            GroundTruth =
                string.Empty;

            RecognizedText =
                string.Empty;

            TestTime =
                DateTime.Now;

            Metadata =
                new Dictionary<string, object>();
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

        public OcrTestResult Clone(
            bool includeSourceImage = true)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(OcrTestResult));
            }

            var clone =
                new OcrTestResult
                {
                    TestId =
                        TestId ?? string.Empty,

                    GroundTruth =
                        GroundTruth ?? string.Empty,

                    RecognizedText =
                        RecognizedText ?? string.Empty,

                    EngineType =
                        EngineType,

                    TestTime =
                        TestTime,

                    ProcessingTime =
                        ProcessingTime,

                    AccuracyScore =
                        AccuracyScore != null
                            ? AccuracyScore.Clone()
                            : null
                };

            if (includeSourceImage &&
                _sourceImage != null)
            {
                clone.SourceImage =
                    new Bitmap(
                        _sourceImage);
            }

            if (Metadata != null)
            {
                foreach (KeyValuePair<string, object> pair
                         in Metadata)
                {
                    clone.Metadata[pair.Key] =
                        pair.Value;
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

            if (Metadata != null)
            {
                Metadata.Clear();
            }
        }
    }

    public sealed class OcrAccuracyReport
    {
        public DateTime GeneratedAt { get; set; }

        public int TotalTests { get; set; }

        public Dictionary<OcrEngineType, EngineAccuracySummary> EngineSummaries { get; set; }

        public double OverallAccuracy { get; set; }

        public OcrEngineType BestPerformingEngine { get; set; }

        public List<AccuracyTrend> Trends { get; set; }

        public Dictionary<string, object> Recommendations { get; set; }

        public OcrAccuracyReport()
        {
            GeneratedAt =
                DateTime.Now;

            EngineSummaries =
                new Dictionary<OcrEngineType, EngineAccuracySummary>();

            Trends =
                new List<AccuracyTrend>();

            Recommendations =
                new Dictionary<string, object>();
        }
    }

    public sealed class EngineAccuracySummary
    {
        public OcrEngineType EngineType { get; set; }

        public int TestCount { get; set; }

        public double AverageAccuracy { get; set; }

        public double BestAccuracy { get; set; }

        public double WorstAccuracy { get; set; }

        public double AverageProcessingTime { get; set; }

        public double CharacterAccuracy { get; set; }

        public double WordAccuracy { get; set; }

        public double LineAccuracy { get; set; }

        public List<string> CommonErrors { get; set; }

        public EngineAccuracySummary()
        {
            CommonErrors =
                new List<string>();
        }
    }

    public sealed class AccuracyTrend
    {
        public DateTime Date { get; set; }

        public double Accuracy { get; set; }

        public OcrEngineType EngineType { get; set; }

        public string TestCategory { get; set; }

        public AccuracyTrend()
        {
            TestCategory =
                string.Empty;
        }
    }
}