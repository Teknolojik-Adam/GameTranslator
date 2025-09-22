using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public class VideoCaptureService : IVideoCaptureService, IDisposable
    {
        private readonly ILogger _logger;
        private VideoCapture _videoCapture;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _captureTask;
        private int _frameNumber = 0;
        private bool _disposed = false;

        public event EventHandler<FrameCapturedEventArgs> FrameCaptured;
        public event EventHandler<VideoErrorEventArgs> VideoError;

        public bool IsCapturing { get; private set; }
        public int FrameRate { get; set; } = 30;
        public int VideoWidth { get; set; } = 640;
        public int VideoHeight { get; set; } = 480;

        public VideoCaptureService(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<bool> StartCaptureAsync(int deviceIndex = 0)
        {
            try
            {
                if (IsCapturing)
                {
                    _logger.LogWarning("Video yakalama zaten çalýþýyor");
                    return true;
                }

                _videoCapture = new VideoCapture(deviceIndex);

                if (!_videoCapture.IsOpened())
                {
                    _logger.LogError($"Video yakalama cihazý {deviceIndex} açýlamadý");
                    OnVideoError($"Video yakalama cihazý {deviceIndex} açýlamadý");
                    return false;
                }

                // Video yakalama ayarlarýný yapýlandýr
                _videoCapture.Set(VideoCaptureProperties.FrameWidth, VideoWidth);
                _videoCapture.Set(VideoCaptureProperties.FrameHeight, VideoHeight);
                _videoCapture.Set(VideoCaptureProperties.Fps, FrameRate);

                // Ayarlarý doðrula
                var actualWidth = (int)_videoCapture.Get(VideoCaptureProperties.FrameWidth);
                var actualHeight = (int)_videoCapture.Get(VideoCaptureProperties.FrameHeight);
                var actualFps = _videoCapture.Get(VideoCaptureProperties.Fps);

                _logger.LogInformation($"Video yakalama baþlatýldý - Cihaz: {deviceIndex}, Çözünürlük: {actualWidth}x{actualHeight}, FPS: {actualFps}");

                IsCapturing = true;
                _cancellationTokenSource = new CancellationTokenSource();
                _captureTask = CaptureFramesAsync(_cancellationTokenSource.Token);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("Video yakalama baþlatýlamadý", ex);
                OnVideoError("Video yakalama baþlatýlamadý", ex);
                return false;
            }
        }

        public async Task StopCaptureAsync()
        {
            try
            {
                if (!IsCapturing)
                {
                    return;
                }

                _logger.LogInformation("Video yakalama durduruluyor...");
                IsCapturing = false;

                _cancellationTokenSource?.Cancel();

                if (_captureTask != null)
                {
                    await _captureTask;
                }

                _videoCapture?.Release();
                _videoCapture?.Dispose();
                _videoCapture = null;

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                _logger.LogInformation("Video yakalama durduruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError("Video yakalama durdurulurken hata oluþtu", ex);
                OnVideoError("Video yakalama durdurulurken hata oluþtu", ex);
            }
        }

        public async Task<Bitmap> CaptureFrameAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (_videoCapture == null || !_videoCapture.IsOpened())
                    {
                        return null;
                    }

                    using (var frame = new Mat())
                    {
                        if (_videoCapture.Read(frame) && !frame.Empty())
                        {
                            return OpenCvSharp.Extensions.BitmapConverter.ToBitmap(frame);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("Tek bir kare yakalanýrken hata oluþtu", ex);
                }

                return null;
            });
        }

        public string[] GetAvailableDevices()
        {
            var devices = new List<string>();

            try
            {
                // 0-9 arasý cihazlarý test et
                for (int i = 0; i < 10; i++)
                {
                    using (var testCapture = new VideoCapture(i))
                    {
                        if (testCapture.IsOpened())
                        {
                            devices.Add($"Kamera {i}");
                            testCapture.Release();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Mevcut video cihazlarý algýlanýrken hata oluþtu", ex);
            }

            return devices.ToArray();
        }

        private async Task CaptureFramesAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && IsCapturing)
                {
                    if (_videoCapture == null || !_videoCapture.IsOpened())
                    {
                        break;
                    }

                    using (var frame = new Mat())
                    {
                        if (_videoCapture.Read(frame) && !frame.Empty())
                        {
                            var bitmap = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(frame);
                            var timestamp = DateTime.Now;
                            var frameNum = Interlocked.Increment(ref _frameNumber);

                            OnFrameCaptured(new FrameCapturedEventArgs(bitmap, timestamp, frameNum));
                        }
                    }

                    // Kare hýzýný kontrol et
                    var delay = 1000 / FrameRate;
                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Yakalama durdurulurken beklenen bir durum
            }
            catch (Exception ex)
            {
                _logger.LogError("Video yakalama döngüsünde hata oluþtu", ex);
                OnVideoError("Video yakalama döngüsünde hata oluþtu", ex);
            }
        }

        protected virtual void OnFrameCaptured(FrameCapturedEventArgs e)
        {
            FrameCaptured?.Invoke(this, e);
        }

        protected virtual void OnVideoError(VideoErrorEventArgs e)
        {
            VideoError?.Invoke(this, e);
        }

        protected virtual void OnVideoError(string errorMessage, Exception exception = null)
        {
            OnVideoError(new VideoErrorEventArgs(errorMessage, exception));
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    StopCaptureAsync().Wait(5000); // 5 saniyeye kadar bekle
                }
                _disposed = true;
            }
        }

        ~VideoCaptureService()
        {
            Dispose(false);
        }
    }
}