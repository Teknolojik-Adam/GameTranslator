using System;
using System.Drawing;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public interface IVideoCaptureService
    {
        event EventHandler<FrameCapturedEventArgs> FrameCaptured;
        event EventHandler<VideoErrorEventArgs> VideoError;
        
        bool IsCapturing { get; }
        int FrameRate { get; set; }
        int VideoWidth { get; set; }
        int VideoHeight { get; set; }
        
        Task<bool> StartCaptureAsync(int deviceIndex = 0);
        Task StopCaptureAsync();
        Task<Bitmap> CaptureFrameAsync();
        string[] GetAvailableDevices();
    }

    public class FrameCapturedEventArgs : EventArgs
    {
        public Bitmap Frame { get; }
        public DateTime Timestamp { get; }
        public int FrameNumber { get; }

        public FrameCapturedEventArgs(Bitmap frame, DateTime timestamp, int frameNumber)
        {
            Frame = frame;
            Timestamp = timestamp;
            FrameNumber = frameNumber;
        }
    }

    public class VideoErrorEventArgs : EventArgs
    {
        public string ErrorMessage { get; }
        public Exception Exception { get; }

        public VideoErrorEventArgs(string errorMessage, Exception exception = null)
        {
            ErrorMessage = errorMessage;
            Exception = exception;
        }
    }
}

