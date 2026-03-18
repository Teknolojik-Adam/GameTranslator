using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public interface IOcrAccuracyService
    {
        Task<OcrAccuracyScore> CalculateAccuracyAsync(string recognizedText, string groundTruth);
        Task<OcrAccuracyScore> CalculateAccuracyWithImageAsync(Bitmap image, string recognizedText, string groundTruth);
        Task<OcrAccuracyReport> GenerateDetailedReportAsync(List<OcrTestResult> testResults);
        Task<OcrAccuracyScore> CalculateConfidenceScoreAsync(string recognizedText, Bitmap sourceImage);
    }

    public class OcrAccuracyScore
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
            ErrorDetails = new List<string>();
            DetailedMetrics = new Dictionary<string, double>();
        }
    }

    public class OcrTestResult
    {
        public string TestId { get; set; }
        public Bitmap SourceImage { get; set; }
        public string GroundTruth { get; set; }
        public string RecognizedText { get; set; }
        public OcrEngineType EngineType { get; set; }
        public DateTime TestTime { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public OcrAccuracyScore AccuracyScore { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public OcrTestResult()
        {
            Metadata = new Dictionary<string, object>();
        }
    }

    public class OcrAccuracyReport
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
            EngineSummaries = new Dictionary<OcrEngineType, EngineAccuracySummary>();
            Trends = new List<AccuracyTrend>();
            Recommendations = new Dictionary<string, object>();
        }
    }

    public class EngineAccuracySummary
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
            CommonErrors = new List<string>();
        }
    }

    public class AccuracyTrend
    {
        public DateTime Date { get; set; }
        public double Accuracy { get; set; }
        public OcrEngineType EngineType { get; set; }
        public string TestCategory { get; set; }
    }
}

