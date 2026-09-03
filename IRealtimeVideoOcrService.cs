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

        Task<bool> StartAsync(
            int deviceIndex = 0);

        Task StopAsync();

        Task<VideoOcrResult> ProcessFrameAsync(
            Bitmap frame);

        Task<OcrAccuracyReport> GetAccuracyReportAsync();

        void SetGroundTruth(
            string groundTruth);
    }

    public sealed class VideoOcrResult : IDisposable
    {
        private Bitmap _sourceFrame;
        private bool _disposed;

        public DateTime Timestamp { get; set; }

        public int FrameNumber { get; set; }

        public Bitmap SourceFrame
        {
            get
            {
                if (_disposed)
                    return null;

                return _sourceFrame;
            }

            set
            {
                if (_disposed)
                    throw new ObjectDisposedException(
                        nameof(VideoOcrResult));

                if (ReferenceEquals(
                    _sourceFrame,
                    value))
                {
                    return;
                }

                if (_sourceFrame != null)
                {
                    _sourceFrame.Dispose();
                }

                _sourceFrame =
                    value;
            }
        }

        public string RecognizedText { get; set; }

        public OcrEngineType UsedEngine { get; set; }

        public double Confidence { get; set; }

        public TimeSpan ProcessingTime { get; set; }

        public OcrComparisonResult ComparisonResult { get; set; }

        public OcrAccuracyScore AccuracyScore { get; set; }

        public Dictionary<string, object> Metadata { get; set; }

        public VideoOcrResult()
        {
            Timestamp =
                DateTime.Now;

            RecognizedText =
                string.Empty;

            Metadata =
                new Dictionary<string, object>();
        }

        public Bitmap CloneSourceFrame()
        {
            if (_disposed ||
                _sourceFrame == null)
            {
                return null;
            }

            return new Bitmap(
                _sourceFrame);
        }

        public VideoOcrResult Clone(
            bool includeFrame = true)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(VideoOcrResult));
            }

            var result =
                new VideoOcrResult
                {
                    Timestamp =
                        Timestamp,

                    FrameNumber =
                        FrameNumber,

                    RecognizedText =
                        RecognizedText ?? string.Empty,

                    UsedEngine =
                        UsedEngine,

                    Confidence =
                        Confidence,

                    ProcessingTime =
                        ProcessingTime,

                    ComparisonResult =
                        ComparisonResult,

                    AccuracyScore =
                        AccuracyScore
                };

            if (includeFrame &&
                _sourceFrame != null)
            {
                result.SourceFrame =
                    new Bitmap(
                        _sourceFrame);
            }

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

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed =
                true;

            if (_sourceFrame != null)
            {
                _sourceFrame.Dispose();
                _sourceFrame = null;
            }

            if (Metadata != null)
            {
                Metadata.Clear();
            }
        }
    }

    public sealed class VideoOcrResultEventArgs : EventArgs
    {
        public VideoOcrResult Result { get; private set; }

        public VideoOcrResultEventArgs(
            VideoOcrResult result)
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

    public sealed class VideoOcrErrorEventArgs : EventArgs
    {
        public string ErrorMessage { get; private set; }

        public Exception Exception { get; private set; }

        public int FrameNumber { get; private set; }

        public VideoOcrErrorEventArgs(
            string errorMessage,
            Exception exception = null,
            int frameNumber = -1)
        {
            ErrorMessage =
                !string.IsNullOrWhiteSpace(errorMessage)
                    ? errorMessage.Trim()
                    : exception != null
                        ? exception.Message
                        : "Bilinmeyen video OCR hatası.";

            Exception =
                exception;

            FrameNumber =
                frameNumber;
        }

        public override string ToString()
        {
            string frameInfo =
                FrameNumber >= 0
                    ? $"Frame {FrameNumber}: "
                    : string.Empty;

            if (Exception == null)
            {
                return
                    frameInfo +
                    ErrorMessage;
            }

            return
                frameInfo +
                ErrorMessage +
                " - " +
                Exception.Message;
        }
    }
}