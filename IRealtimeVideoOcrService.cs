using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public interface IRealtimeVideoOcrService
    {
        event EventHandler<VideoOcrResultEventArgs> OcrResultReady;
        event EventHandler<VideoOcrErrorEventArgs> OcrError;
        event EventHandler<OcrComparisonCompletedEventArgs> ComparisonCompleted;
        
        bool IsRunning { get; }
        int FrameRate { get; set; }
        bool EnableComparison { get; set; }
        bool EnableAccuracyScoring { get; set; }
        
        Task<bool> StartAsync(int deviceIndex = 0);
        Task StopAsync();
        Task<VideoOcrResult> ProcessFrameAsync(Bitmap frame);
        Task<OcrAccuracyReport> GetAccuracyReportAsync();
        void SetGroundTruth(string groundTruth);
    }

    public class VideoOcrResult
    {
        public DateTime Timestamp { get; set; }
        public int FrameNumber { get; set; }
        public Bitmap SourceFrame { get; set; }
        public string RecognizedText { get; set; }
        public OcrEngineType UsedEngine { get; set; }
        public double Confidence { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public OcrComparisonResult ComparisonResult { get; set; }
        public OcrAccuracyScore AccuracyScore { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public VideoOcrResult()
        {
            Metadata = new Dictionary<string, object>();
        }
    }

    public class VideoOcrResultEventArgs : EventArgs
    {
        public VideoOcrResult Result { get; }

        public VideoOcrResultEventArgs(VideoOcrResult result)
        {
            Result = result;
        }
    }

    public class VideoOcrErrorEventArgs : EventArgs
    {
        public string ErrorMessage { get; }
        public Exception Exception { get; }
        public int FrameNumber { get; }

        public VideoOcrErrorEventArgs(string errorMessage, Exception exception = null, int frameNumber = -1)
        {
            ErrorMessage = errorMessage;
            Exception = exception;
            FrameNumber = frameNumber;
        }
    }
}

