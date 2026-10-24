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

        Task<bool> StartCaptureAsync(
            int deviceIndex = 0);

        Task StopCaptureAsync();

        Task<Bitmap> CaptureFrameAsync();

        string[] GetAvailableDevices();
    }

    public sealed class FrameCapturedEventArgs : EventArgs
    {
        public Bitmap Frame { get; private set; }

        public DateTime Timestamp { get; private set; }

        public int FrameNumber { get; private set; }

        public FrameCapturedEventArgs(
            Bitmap frame,
            DateTime timestamp,
            int frameNumber)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(
                    nameof(frame));
            }

            Frame =
                frame;

            Timestamp =
                timestamp;

            FrameNumber =
                frameNumber < 0
                    ? 0
                    : frameNumber;
        }

        public Bitmap CloneFrame()
        {
            if (Frame == null)
                return null;

            return new Bitmap(
                Frame);
        }
    }

    public sealed class VideoErrorEventArgs : EventArgs
    {
        public string ErrorMessage { get; private set; }

        public Exception Exception { get; private set; }

        public VideoErrorEventArgs(
            string errorMessage,
            Exception exception = null)
        {
            ErrorMessage =
                !string.IsNullOrWhiteSpace(errorMessage)
                    ? errorMessage.Trim()
                    : exception != null
                        ? exception.Message
                        : "Bilinmeyen video yakalama hatası.";

            Exception =
                exception;
        }

        public override string ToString()
        {
            if (Exception == null)
                return ErrorMessage;

            return
                ErrorMessage +
                " - " +
                Exception.Message;
        }
    }
}