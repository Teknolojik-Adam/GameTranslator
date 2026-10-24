using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public class VideoCaptureService : IVideoCaptureService, IDisposable
    {
        private readonly ILogger _logger;

        // Start / Stop işlemlerinin aynı anda yapılmasını engeller.
        private readonly SemaphoreSlim _lifecycleLock =
            new SemaphoreSlim(1, 1);

        // VideoCapture nesnesine aynı anda yalnızca bir erişim.
        private readonly SemaphoreSlim _captureLock =
            new SemaphoreSlim(1, 1);

        // Son frame'e güvenli erişim.
        private readonly object _latestFrameLock =
            new object();

        private VideoCapture _videoCapture;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _captureTask;

        private Bitmap _latestFrame;

        private int _frameNumber;
        private bool _disposed;

        private volatile bool _isCapturing;

        private const int MinFrameRate = 1;
        private const int MaxFrameRate = 120;

        private const int MinVideoWidth = 160;
        private const int MinVideoHeight = 120;

        private const int MaxVideoWidth = 7680;
        private const int MaxVideoHeight = 4320;

        private const int MaxConsecutiveReadFailures = 15;

        public event EventHandler<FrameCapturedEventArgs> FrameCaptured;
        public event EventHandler<VideoErrorEventArgs> VideoError;

        public bool IsCapturing => _isCapturing;

        public int FrameRate { get; set; } = 30;
        public int VideoWidth { get; set; } = 640;
        public int VideoHeight { get; set; } = 480;

        public VideoCaptureService(ILogger logger)
        {
            _logger =
                logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<bool> StartCaptureAsync(int deviceIndex = 0)
        {
            ThrowIfDisposed();

            if (deviceIndex < 0)
            {
                _logger.LogWarning(
                    $"Geçersiz kamera indeksi: {deviceIndex}");

                return false;
            }

            await _lifecycleLock.WaitAsync().ConfigureAwait(false);

            try
            {
                ThrowIfDisposed();

                if (_isCapturing)
                {
                    _logger.LogWarning(
                        "Video yakalama zaten çalışıyor.");

                    return true;
                }

                await CleanupCaptureResourcesAsync()
                    .ConfigureAwait(false);

                int requestedWidth =
                    Clamp(
                        VideoWidth,
                        MinVideoWidth,
                        MaxVideoWidth);

                int requestedHeight =
                    Clamp(
                        VideoHeight,
                        MinVideoHeight,
                        MaxVideoHeight);

                int requestedFrameRate =
                    Clamp(
                        FrameRate,
                        MinFrameRate,
                        MaxFrameRate);

                VideoWidth = requestedWidth;
                VideoHeight = requestedHeight;
                FrameRate = requestedFrameRate;

                VideoCapture capture = null;

                try
                {
                    // Kamera açma işlemi bazı sistemlerde birkaç saniye sürebilir.
                    capture = await Task.Run(() =>
                    {
                        var newCapture =
                            new VideoCapture(deviceIndex);

                        return newCapture;
                    }).ConfigureAwait(false);

                    if (capture == null ||
                        !capture.IsOpened())
                    {
                        _logger.LogError(
                            $"Video yakalama cihazı {deviceIndex} açılamadı.");

                        OnVideoError(
                            $"Video yakalama cihazı {deviceIndex} açılamadı.");

                        capture?.Release();
                        capture?.Dispose();

                        return false;
                    }

                    ConfigureCapture(
                        capture,
                        requestedWidth,
                        requestedHeight,
                        requestedFrameRate);

                    int actualWidth =
                        SafeGetInt(
                            capture,
                            VideoCaptureProperties.FrameWidth);

                    int actualHeight =
                        SafeGetInt(
                            capture,
                            VideoCaptureProperties.FrameHeight);

                    double actualFps =
                        SafeGet(
                            capture,
                            VideoCaptureProperties.Fps);

                    _videoCapture = capture;
                    capture = null;

                    Interlocked.Exchange(
                        ref _frameNumber,
                        0);

                    ClearLatestFrame();

                    _cancellationTokenSource =
                        new CancellationTokenSource();

                    _isCapturing = true;

                    CancellationToken token =
                        _cancellationTokenSource.Token;

                    // Capture loop kesinlikle UI thread üzerinde çalışmasın.
                    _captureTask =
                        Task.Run(
                            () => CaptureFramesAsync(token),
                            CancellationToken.None);

                    _logger.LogInformation(
                        $"Video yakalama başlatıldı - " +
                        $"Cihaz: {deviceIndex}, " +
                        $"Çözünürlük: {actualWidth}x{actualHeight}, " +
                        $"FPS: {actualFps:F1}");

                    return true;
                }
                catch (Exception ex)
                {
                    capture?.Release();
                    capture?.Dispose();

                    _videoCapture?.Release();
                    _videoCapture?.Dispose();
                    _videoCapture = null;

                    _isCapturing = false;

                    _logger.LogError(
                        "Video yakalama başlatılamadı.",
                        ex);

                    OnVideoError(
                        "Video yakalama başlatılamadı.",
                        ex);

                    return false;
                }
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task StopCaptureAsync()
        {
            await _lifecycleLock.WaitAsync()
                .ConfigureAwait(false);

            try
            {
                if (!_isCapturing &&
                    _captureTask == null &&
                    _videoCapture == null)
                {
                    return;
                }

                _logger.LogInformation(
                    "Video yakalama durduruluyor...");

                _isCapturing = false;

                CancellationTokenSource cancellation =
                    _cancellationTokenSource;

                Task captureTask =
                    _captureTask;

                try
                {
                    cancellation?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Zaten kapatılmış.
                }

                if (captureTask != null)
                {
                    try
                    {
                        await captureTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal kapanış.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            $"Video yakalama görevi kapanırken hata oluştu: " +
                            $"{ex.Message}");
                    }
                }

                await CleanupCaptureResourcesAsync()
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Video yakalama durduruldu.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Video yakalama durdurulurken hata oluştu.",
                    ex);

                OnVideoError(
                    "Video yakalama durdurulurken hata oluştu.",
                    ex);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        /// <summary>
        /// Video capture aktifse kameradan ikinci kez Read() yapmaz.
        /// Capture loop tarafından saklanan en güncel frame'in kopyasını döndürür.
        ///
        /// Dönen Bitmap'in Dispose edilmesi çağıranın sorumluluğundadır.
        /// </summary>
        public Task<Bitmap> CaptureFrameAsync()
        {
            ThrowIfDisposed();

            lock (_latestFrameLock)
            {
                if (_latestFrame == null)
                {
                    return Task.FromResult<Bitmap>(null);
                }

                try
                {
                    return Task.FromResult(
                        (Bitmap)_latestFrame.Clone());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        $"Son video karesi kopyalanamadı: {ex.Message}");

                    return Task.FromResult<Bitmap>(null);
                }
            }
        }

        public string[] GetAvailableDevices()
        {
            ThrowIfDisposed();

            var devices =
                new List<string>();

            // Windows'ta klasik webcam index taraması.
            // Bu metod senkron tutuluyor çünkü interface böyle.
            for (int i = 0; i < 10; i++)
            {
                VideoCapture testCapture = null;

                try
                {
                    testCapture =
                        new VideoCapture(i);

                    if (testCapture.IsOpened())
                    {
                        string deviceName =
                            $"Kamera {i}";

                        int width =
                            SafeGetInt(
                                testCapture,
                                VideoCaptureProperties.FrameWidth);

                        int height =
                            SafeGetInt(
                                testCapture,
                                VideoCaptureProperties.FrameHeight);

                        if (width > 0 &&
                            height > 0)
                        {
                            deviceName +=
                                $" ({width}x{height})";
                        }

                        devices.Add(deviceName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        $"Kamera {i} kontrol edilirken hata oluştu: " +
                        $"{ex.Message}");
                }
                finally
                {
                    try
                    {
                        testCapture?.Release();
                    }
                    catch
                    {
                    }

                    testCapture?.Dispose();
                }
            }

            _logger.LogInformation(
                $"{devices.Count} video yakalama cihazı bulundu.");

            return devices.ToArray();
        }

        private async Task CaptureFramesAsync(
            CancellationToken cancellationToken)
        {
            int consecutiveReadFailures = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested &&
                       _isCapturing)
                {
                    int safeFrameRate =
                        Clamp(
                            FrameRate,
                            MinFrameRate,
                            MaxFrameRate);

                    double targetFrameDurationMs =
                        1000.0 / safeFrameRate;

                    var frameStopwatch =
                        Stopwatch.StartNew();

                    Bitmap capturedBitmap = null;

                    try
                    {
                        await _captureLock
                            .WaitAsync(cancellationToken)
                            .ConfigureAwait(false);

                        try
                        {
                            if (_videoCapture == null ||
                                !_videoCapture.IsOpened())
                            {
                                break;
                            }

                            using (var frame = new Mat())
                            {
                                bool readSuccess =
                                    _videoCapture.Read(frame);

                                if (readSuccess &&
                                    !frame.Empty())
                                {
                                    capturedBitmap =
                                        BitmapConverter.ToBitmap(frame);
                                }
                            }
                        }
                        finally
                        {
                            _captureLock.Release();
                        }

                        if (capturedBitmap == null)
                        {
                            consecutiveReadFailures++;

                            if (consecutiveReadFailures >=
                                MaxConsecutiveReadFailures)
                            {
                                string message =
                                    "Kameradan art arda çok sayıda kare okunamadı. " +
                                    "Video yakalama durduruluyor.";

                                _logger.LogError(message);

                                OnVideoError(message);

                                break;
                            }

                            await Task.Delay(
                                    20,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            continue;
                        }

                        consecutiveReadFailures = 0;

                        int frameNum =
                            Interlocked.Increment(
                                ref _frameNumber);

                        DateTime timestamp =
                            DateTime.Now;

                        ReplaceLatestFrame(
                            capturedBitmap);

                        // capturedBitmap artık _latestFrame tarafından sahipleniliyor.
                        capturedBitmap = null;

                        RaiseFrameCaptured(
                            timestamp,
                            frameNum);

                        frameStopwatch.Stop();

                        double remainingDelay =
                            targetFrameDurationMs -
                            frameStopwatch.Elapsed.TotalMilliseconds;

                        if (remainingDelay > 1)
                        {
                            await Task.Delay(
                                    TimeSpan.FromMilliseconds(
                                        remainingDelay),
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        capturedBitmap?.Dispose();
                        break;
                    }
                    catch (Exception ex)
                    {
                        capturedBitmap?.Dispose();

                        consecutiveReadFailures++;

                        _logger.LogWarning(
                            $"Video frame #{_frameNumber + 1} " +
                            $"işlenirken hata oluştu: {ex.Message}");

                        if (consecutiveReadFailures >=
                            MaxConsecutiveReadFailures)
                        {
                            OnVideoError(
                                "Video yakalama sırasında art arda çok sayıda hata oluştu.",
                                ex);

                            break;
                        }

                        try
                        {
                            await Task.Delay(
                                    50,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal iptal.
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Video yakalama döngüsünde kritik hata oluştu.",
                    ex);

                OnVideoError(
                    "Video yakalama döngüsünde kritik hata oluştu.",
                    ex);
            }
            finally
            {
                _isCapturing = false;
            }
        }

        /// <summary>
        /// Her aboneye ayrı Bitmap kopyası verilir.
        ///
        /// ÖNEMLİ:
        /// FrameCaptured aboneleri kendilerine verilen Bitmap'i
        /// işleri bittikten sonra Dispose etmelidir.
        /// </summary>
        private void RaiseFrameCaptured(
            DateTime timestamp,
            int frameNumber)
        {
            EventHandler<FrameCapturedEventArgs> handlers =
                FrameCaptured;

            if (handlers == null)
                return;

            Delegate[] subscribers =
                handlers.GetInvocationList();

            foreach (Delegate subscriber in subscribers)
            {
                Bitmap frameCopy = null;

                try
                {
                    lock (_latestFrameLock)
                    {
                        if (_latestFrame == null)
                            return;

                        frameCopy =
                            (Bitmap)_latestFrame.Clone();
                    }

                    var args =
                        new FrameCapturedEventArgs(
                            frameCopy,
                            timestamp,
                            frameNumber);

                    ((EventHandler<FrameCapturedEventArgs>)subscriber)
                        .Invoke(
                            this,
                            args);

                    // Ownership event subscriber'a geçti.
                    frameCopy = null;
                }
                catch (Exception ex)
                {
                    frameCopy?.Dispose();

                    _logger.LogError(
                        $"FrameCaptured abonesi hata oluşturdu. " +
                        $"Frame: {frameNumber}",
                        ex);
                }
            }
        }

        private void ReplaceLatestFrame(
            Bitmap newFrame)
        {
            if (newFrame == null)
                return;

            Bitmap oldFrame = null;

            lock (_latestFrameLock)
            {
                oldFrame = _latestFrame;
                _latestFrame = newFrame;
            }

            oldFrame?.Dispose();
        }

        private void ClearLatestFrame()
        {
            Bitmap oldFrame = null;

            lock (_latestFrameLock)
            {
                oldFrame = _latestFrame;
                _latestFrame = null;
            }

            oldFrame?.Dispose();
        }

        private void ConfigureCapture(
            VideoCapture capture,
            int width,
            int height,
            int fps)
        {
            if (capture == null)
                return;

            SafeSet(
                capture,
                VideoCaptureProperties.FrameWidth,
                width);

            SafeSet(
                capture,
                VideoCaptureProperties.FrameHeight,
                height);

            SafeSet(
                capture,
                VideoCaptureProperties.Fps,
                fps);

            // Bazı kameralar bu değeri desteklemez.
            // Desteklenmezse hata kabul etmiyoruz.
            try
            {
                capture.Set(
                    VideoCaptureProperties.BufferSize,
                    1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Kamera buffer boyutu ayarlanamadı: {ex.Message}");
            }
        }

        private void SafeSet(
            VideoCapture capture,
            VideoCaptureProperties property,
            double value)
        {
            try
            {
                bool result =
                    capture.Set(
                        property,
                        value);

                if (!result)
                {
                    _logger.LogWarning(
                        $"Kamera ayarı kabul edilmedi: " +
                        $"{property} = {value}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Kamera ayarı uygulanamadı " +
                    $"({property} = {value}): {ex.Message}");
            }
        }

        private double SafeGet(
            VideoCapture capture,
            VideoCaptureProperties property)
        {
            try
            {
                return capture.Get(property);
            }
            catch
            {
                return 0;
            }
        }

        private int SafeGetInt(
            VideoCapture capture,
            VideoCaptureProperties property)
        {
            double value =
                SafeGet(
                    capture,
                    property);

            if (double.IsNaN(value) ||
                double.IsInfinity(value))
            {
                return 0;
            }

            return (int)Math.Round(value);
        }

        private async Task CleanupCaptureResourcesAsync()
        {
            CancellationTokenSource cancellation =
                _cancellationTokenSource;

            _cancellationTokenSource = null;
            _captureTask = null;

            cancellation?.Dispose();

            await _captureLock
                .WaitAsync()
                .ConfigureAwait(false);

            try
            {
                if (_videoCapture != null)
                {
                    try
                    {
                        _videoCapture.Release();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            $"VideoCapture.Release hatası: {ex.Message}");
                    }

                    try
                    {
                        _videoCapture.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            $"VideoCapture.Dispose hatası: {ex.Message}");
                    }

                    _videoCapture = null;
                }
            }
            finally
            {
                _captureLock.Release();
            }

            ClearLatestFrame();
        }

        protected virtual void OnVideoError(
            VideoErrorEventArgs e)
        {
            if (e == null)
                return;

            EventHandler<VideoErrorEventArgs> handlers =
                VideoError;

            if (handlers == null)
                return;

            foreach (Delegate subscriber in
                     handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<VideoErrorEventArgs>)subscriber)
                        .Invoke(
                            this,
                            e);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        $"VideoError event abonesi hata oluşturdu: " +
                        $"{ex.Message}");
                }
            }
        }

        protected virtual void OnVideoError(
            string errorMessage,
            Exception exception = null)
        {
            OnVideoError(
                new VideoErrorEventArgs(
                    errorMessage,
                    exception));
        }

        private static int Clamp(
            int value,
            int minimum,
            int maximum)
        {
            if (value < minimum)
                return minimum;

            if (value > maximum)
                return maximum;

            return value;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(VideoCaptureService));
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                /*
                 * Capture loop Task.Run üzerinde çalıştığı ve
                 * StopCaptureAsync içerisindeki await'ler
                 * ConfigureAwait(false) kullandığı için
                 * burada klasik WPF SynchronizationContext
                 * deadlock riski önemli ölçüde azaltılmıştır.
                 */
                StopCaptureAsync()
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                try
                {
                    _logger.LogWarning(
                        $"VideoCaptureService kapatılırken hata oluştu: " +
                        $"{ex.Message}");
                }
                catch
                {
                }
            }

            ClearLatestFrame();

            /*
             * SemaphoreSlim nesnelerini burada Dispose etmiyorum.
             *
             * Bunun nedeni kapanma sırasında çalışan continuation'ların
             * semaphore'a erişme ihtimali olmasıdır. Uygulama ömrü boyunca
             * tek instance kullanılan bu servis için GC tarafından
             * temizlenmeleri daha güvenlidir.
             */
        }
    }
}