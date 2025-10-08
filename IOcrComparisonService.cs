using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public interface IOcrComparisonService
    {
        event EventHandler<OcrComparisonCompletedEventArgs> ComparisonCompleted;
        
        Task<OcrComparisonResult> CompareEnginesAsync(Bitmap image, string language);
        Task<OcrComparisonResult> CompareEnginesWithRegionsAsync(Bitmap image, string language);
        OcrEngineType GetBestEngine(OcrComparisonResult result);
        Task<OcrComparisonReport> GenerateComparisonReportAsync(List<OcrComparisonResult> results);
    }

    public class OcrComparisonResult
    {
        public DateTime Timestamp { get; set; }
        public Bitmap SourceImage { get; set; }
        public string Language { get; set; }
        public Dictionary<OcrEngineType, OcrEngineResult> EngineResults { get; set; }
        public OcrEngineType BestEngine { get; set; }
        public double BestConfidence { get; set; }
        public TimeSpan TotalProcessingTime { get; set; }

        public OcrComparisonResult()
        {
            EngineResults = new Dictionary<OcrEngineType, OcrEngineResult>();
        }
    }

    public class OcrEngineResult
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
            Metadata = new Dictionary<string, object>();
        }
    }

    public class OcrComparisonReport
    {
        public DateTime GeneratedAt { get; set; }
        public int TotalComparisons { get; set; }
        public Dictionary<OcrEngineType, EngineAccuracyStats> EngineStats { get; set; }
        public OcrEngineType OverallBestEngine { get; set; }
        public double AverageProcessingTime { get; set; }
        public Dictionary<string, object> Recommendations { get; set; }

        public OcrComparisonReport()
        {
            EngineStats = new Dictionary<OcrEngineType, EngineAccuracyStats>();
            Recommendations = new Dictionary<string, object>();
        }
    }

    public class EngineAccuracyStats
    {
        public OcrEngineType EngineType { get; set; }
        public int TotalTests { get; set; }
        public int SuccessfulTests { get; set; }
        public double SuccessRate { get; set; }
        public double AverageConfidence { get; set; }
        public double AverageProcessingTime { get; set; }
        public double BestConfidence { get; set; }
        public double WorstConfidence { get; set; } = 1.0;
        public int Wins { get; set; } // Number of times this engine was best
        public double WinRate { get; set; }
    }

    public class OcrComparisonCompletedEventArgs : EventArgs
    {
        public OcrComparisonResult Result { get; }

        public OcrComparisonCompletedEventArgs(OcrComparisonResult result)
        {
            Result = result;
        }
    }
}
