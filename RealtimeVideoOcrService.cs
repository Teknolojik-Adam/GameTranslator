using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public class RealtimeVideoOcrService : IRealtimeVideoOcrService, IDisposable
    {
        private const int MaxRecentResults = 10;
        private const int DefaultOcrIntervalMs = 180;
        private const double FrameChangeThreshold = 0.03;

        private readonly ILogger _logger;
        private readonly IVideoCaptureService _videoCaptureService;
        private readonly IOcrComparisonService _ocrComparisonService;
        private readonly IOcrAccuracyService _ocrAccuracyService;
        private readonly IOcrService _ocrService;
        private readonly AppSettings _appSettings;

        private readonly SemaphoreSlim _lifecycleGate =
            new SemaphoreSlim(1, 1);

        private readonly SemaphoreSlim _processingGate =
            new SemaphoreSlim(1, 1);

        private readonly SemaphoreSlim _frameSignal =
            new SemaphoreSlim(0, 1);

        private readonly object _pendingFrameLock =
            new object();

        private readonly object _resultsLock =
            new object();

        private readonly Queue<VideoOcrResult> _recentResults =
            new Queue<VideoOcrResult>();

        private CancellationTokenSource _cancellationTokenSource;
        private Task _processingTask;
        private Bitmap _pendingFrame;

        private int _frameNumber;
        private int _disposed;
        private volatile bool _isRunning;

        private string _groundTruth =
            string.Empty;

        private long _lastOcrTicks;
        private string _lastRecognizedText = string.Empty;
        private Mat _previousThumb;
        private readonly object _thumbLock = new object();
        private readonly object _statsLock = new object();
        private int _framesReceived;
        private int _framesDroppedThrottle;
        private int _framesDroppedNoChange;
        private int _ocrRuns;
        private int _duplicateTextsSkipped;
        private long _totalOcrTicks;
        private int _translationGeneration;

        public event EventHandler<VideoOcrResultEventArgs> OcrResultReady;
        public event EventHandler<VideoOcrErrorEventArgs> OcrError;
        public event EventHandler<OcrComparisonCompletedEventArgs> ComparisonCompleted;

        public bool IsRunning => _isRunning;

        public int FrameRate { get; set; } = 30;

        public bool EnableComparison { get; set; } = true;

        public bool EnableAccuracyScoring { get; set; }

        public RealtimeVideoOcrService(
            ILogger logger,
            IVideoCaptureService videoCaptureService,
            IOcrComparisonService ocrComparisonService,
            IOcrAccuracyService ocrAccuracyService,
            IOcrService ocrService,
            AppSettings appSettings)
        {
            _logger =
                logger ?? throw new ArgumentNullException(nameof(logger));

            _videoCaptureService =
                videoCaptureService ?? throw new ArgumentNullException(nameof(videoCaptureService));

            _ocrComparisonService =
                ocrComparisonService ?? throw new ArgumentNullException(nameof(ocrComparisonService));

            _ocrAccuracyService =
                ocrAccuracyService ?? throw new ArgumentNullException(nameof(ocrAccuracyService));

            _ocrService =
                ocrService ?? throw new ArgumentNullException(nameof(ocrService));

            _appSettings =
                appSettings ?? throw new ArgumentNullException(nameof(appSettings));

            _videoCaptureService.FrameCaptured +=
                OnFrameCaptured;

            _videoCaptureService.VideoError +=
                OnVideoError;

            _ocrComparisonService.ComparisonCompleted +=
                OnComparisonCompleted;
        }

        public async Task<bool> StartAsync(int deviceIndex = 0)
        {
            ThrowIfDisposed();

            await _lifecycleGate
                .WaitAsync()
                .ConfigureAwait(false);

            try
            {
                if (_isRunning)
                {
                    return true;
                }

                ClearPendingFrame();
                ClearRecentResults();
                lock (_thumbLock) { _previousThumb?.Dispose(); _previousThumb = null; }
                Volatile.Write(ref _lastOcrTicks, 0);
                Volatile.Write(ref _lastRecognizedText, string.Empty);
                lock (_statsLock) { _framesReceived = 0; _framesDroppedThrottle = 0; _framesDroppedNoChange = 0; _ocrRuns = 0; _duplicateTextsSkipped = 0; _totalOcrTicks = 0; }

                Interlocked.Exchange(
                    ref _frameNumber,
                    0);

                bool captureStarted =
                    await _videoCaptureService
                        .StartCaptureAsync(deviceIndex)
                        .ConfigureAwait(false);

                if (!captureStarted)
                {
                    _logger.LogError(
                        "Video yakalama başlatılamadı.");

                    return false;
                }

                _cancellationTokenSource =
                    new CancellationTokenSource();

                _isRunning = true;

                CancellationToken token =
                    _cancellationTokenSource.Token;

                _processingTask =
                    Task.Run(
                        () => ProcessFramesAsync(token));

                _logger.LogInformation(
                    "Gerçek zamanlı video OCR başlatıldı.");

                return true;
            }
            catch (Exception ex)
            {
                _isRunning = false;

                try
                {
                    await _videoCaptureService
                        .StopCaptureAsync()
                        .ConfigureAwait(false);
                }
                catch
                {
                }

                _logger.LogError(
                    "Gerçek zamanlı video OCR başlatılamadı.",
                    ex);

                RaiseOcrError(
                    "Gerçek zamanlı video OCR başlatılamadı.",
                    ex);

                return false;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async Task StopAsync()
        {
            await _lifecycleGate
                .WaitAsync()
                .ConfigureAwait(false);

            try
            {
                if (!_isRunning &&
                    _processingTask == null)
                {
                    return;
                }

                _isRunning = false;

                CancellationTokenSource cancellation =
                    _cancellationTokenSource;

                Task processingTask =
                    _processingTask;

                try
                {
                    cancellation?.Cancel();
                }
                catch
                {
                }

                SignalFrame();

                try
                {
                    await _videoCaptureService
                        .StopCaptureAsync()
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "Video yakalama durdurulamadı.",
                        ex);
                }

                if (processingTask != null)
                {
                    try
                    {
                        await processingTask
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            "Video OCR işlem görevi durdurulamadı.",
                            ex);
                    }
                }

                _processingTask = null;

                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }

                ClearPendingFrame();

                _logger.LogInformation(
                    "Gerçek zamanlı video OCR durduruldu.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Gerçek zamanlı video OCR durdurulurken hata oluştu.",
                    ex);

                RaiseOcrError(
                    "Gerçek zamanlı video OCR durdurulurken hata oluştu.",
                    ex);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async Task<VideoOcrResult> ProcessFrameAsync(
            Bitmap frame)
        {
            ThrowIfDisposed();

            if (frame == null)
                return null;

            await _processingGate
                .WaitAsync()
                .ConfigureAwait(false);

            Bitmap resultFrame = null;

            try
            {
                int frameNumber =
                    Interlocked.Increment(
                        ref _frameNumber);

                var stopwatch =
                    Stopwatch.StartNew();

                resultFrame =
                    (Bitmap)frame.Clone();

                var result =
                    new VideoOcrResult
                    {
                        Timestamp = DateTime.Now,
                        FrameNumber = frameNumber,
                        SourceFrame = resultFrame
                    };

                if (EnableComparison)
                {
                    var comparisonResult =
                        await _ocrComparisonService
                            .CompareEnginesAsync(
                                frame,
                                _appSettings.OcrLanguage)
                            .ConfigureAwait(false);

                    if (comparisonResult != null)
                    {
                        result.ComparisonResult =
                            comparisonResult;

                        result.UsedEngine =
                            comparisonResult.BestEngine;

                        if (comparisonResult.EngineResults != null &&
                            comparisonResult.EngineResults.ContainsKey(
                                comparisonResult.BestEngine))
                        {
                            var bestResult =
                                comparisonResult.EngineResults[
                                    comparisonResult.BestEngine];

                            result.RecognizedText =
                                bestResult?.RecognizedText ??
                                string.Empty;

                            result.Confidence =
                                bestResult != null
                                    ? bestResult.Confidence
                                    : 0;
                        }
                        else
                        {
                            result.RecognizedText =
                                string.Empty;

                            result.Confidence = 0;
                        }
                    }
                    else
                    {
                        result.RecognizedText =
                            string.Empty;

                        result.Confidence = 0;
                    }
                }
                else
                {
                    result.RecognizedText =
                        await _ocrService
                            .GetTextFromImage(
                                frame,
                                _appSettings.OcrLanguage)
                            .ConfigureAwait(false);

                    result.RecognizedText =
                        result.RecognizedText ??
                        string.Empty;

                    result.UsedEngine =
                        _appSettings.OcrEngine;

                    result.Confidence =
                        CalculateSimpleConfidence(
                            result.RecognizedText,
                            frame);
                }

                string groundTruth =
                    Volatile.Read(
                        ref _groundTruth);

                if (EnableAccuracyScoring &&
                    !string.IsNullOrWhiteSpace(groundTruth))
                {
                    result.AccuracyScore =
                        await _ocrAccuracyService
                            .CalculateAccuracyWithImageAsync(
                                frame,
                                result.RecognizedText,
                                groundTruth)
                            .ConfigureAwait(false);
                }

                stopwatch.Stop();

                result.ProcessingTime =
                    stopwatch.Elapsed;

                AddRecentResult(result);

                resultFrame = null;

                RaiseOcrResultReady(result);

                return result;
            }
            catch (Exception ex)
            {
                resultFrame?.Dispose();

                _logger.LogError(
                    "Video karesi OCR işlemi sırasında hata oluştu.",
                    ex);

                RaiseOcrError(
                    "Video karesi OCR işlemi sırasında hata oluştu.",
                    ex);

                return null;
            }
            finally
            {
                _processingGate.Release();
            }
        }

        public async Task<OcrAccuracyReport> GetAccuracyReportAsync()
        {
            ThrowIfDisposed();

            var testResults =
                new List<OcrTestResult>();

            string groundTruth =
                Volatile.Read(
                    ref _groundTruth);

            lock (_resultsLock)
            {
                foreach (VideoOcrResult result in _recentResults)
                {
                    if (result == null ||
                        result.AccuracyScore == null)
                    {
                        continue;
                    }

                    Bitmap image = null;

                    if (result.SourceFrame != null)
                    {
                        try
                        {
                            image =
                                (Bitmap)result.SourceFrame.Clone();
                        }
                        catch
                        {
                            image = null;
                        }
                    }

                    testResults.Add(
                        new OcrTestResult
                        {
                            TestId =
                                $"Kare_{result.FrameNumber}",

                            SourceImage =
                                image,

                            GroundTruth =
                                groundTruth,

                            RecognizedText =
                                result.RecognizedText,

                            EngineType =
                                result.UsedEngine,

                            TestTime =
                                result.Timestamp,

                            ProcessingTime =
                                result.ProcessingTime,

                            AccuracyScore =
                                result.AccuracyScore
                        });
                }
            }

            try
            {
                return await _ocrAccuracyService
                    .GenerateDetailedReportAsync(
                        testResults)
                    .ConfigureAwait(false);
            }
            finally
            {
                foreach (OcrTestResult result in testResults)
                {
                    try
                    {
                        result.SourceImage?.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        public void SetGroundTruth(string groundTruth)
        {
            string value =
                groundTruth ?? string.Empty;

            Interlocked.Exchange(
                ref _groundTruth,
                value);

            string displayValue =
                value.Length > 50
                    ? value.Substring(0, 50) + "..."
                    : value;

            _logger.LogInformation(
                $"Referans metin ayarlandı: {displayValue}");
        }

        private async Task ProcessFramesAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await _frameSignal
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (cancellationToken.IsCancellationRequested)
                        break;

                    Bitmap frame =
                        TakePendingFrame();

                    if (frame == null)
                        continue;

                    long nowTicks = Stopwatch.GetTimestamp();
                    long freq = Stopwatch.Frequency;
                    long elapsedMs = (nowTicks - Volatile.Read(ref _lastOcrTicks)) * 1000 / freq;
                    if (Volatile.Read(ref _lastOcrTicks) != 0 && elapsedMs < DefaultOcrIntervalMs)
                    {
                        lock (_statsLock) { _framesDroppedThrottle++; }
                        bool hasNewer;
                        lock (_pendingFrameLock) { hasNewer = _pendingFrame != null; }
                        if (hasNewer)
                        {
                            frame.Dispose();
                            continue;
                        }
                        long remain = DefaultOcrIntervalMs - elapsedMs;
                        if (remain > 5)
                        {
                            try { await Task.Delay((int)remain, cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { frame.Dispose(); break; }
                        }
                        if (Volatile.Read(ref _lastOcrTicks) != 0)
                        {
                            long now2 = Stopwatch.GetTimestamp();
                            long e2 = (now2 - Volatile.Read(ref _lastOcrTicks)) * 1000 / freq;
                            if (e2 < DefaultOcrIntervalMs)
                            {
                                frame.Dispose();
                                continue;
                            }
                        }
                    }

                    if (!IsFrameSignificantlyChanged(frame))
                    {
                        lock (_statsLock) { _framesDroppedNoChange++; }
                        frame.Dispose();
                        Volatile.Write(ref _lastOcrTicks, Stopwatch.GetTimestamp());
                        continue;
                    }

                    var stopwatch =
                        Stopwatch.StartNew();

                    try
                    {
                        var result = await ProcessFrameAsync(frame)
                            .ConfigureAwait(false);
                        if (result != null && !string.IsNullOrWhiteSpace(result.RecognizedText))
                        {
                            string normalized = NormalizeText(result.RecognizedText);
                            string last = Volatile.Read(ref _lastRecognizedText);
                            if (!string.IsNullOrEmpty(last) && string.Equals(normalized, last, StringComparison.Ordinal))
                            {
                                lock (_statsLock) { _duplicateTextsSkipped++; }
                            }
                            else
                            {
                                Volatile.Write(ref _lastRecognizedText, normalized);
                            }
                        }
                        Volatile.Write(ref _lastOcrTicks, Stopwatch.GetTimestamp());
                        lock (_statsLock) { _ocrRuns++; _totalOcrTicks += stopwatch.ElapsedTicks; }
                    }
                    finally
                    {
                        frame.Dispose();
                    }

                    stopwatch.Stop();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Video OCR işleme döngüsünde hata oluştu.",
                    ex);

                RaiseOcrError(
                    "Video OCR işleme döngüsünde hata oluştu.",
                    ex);
            }
        }

        private void OnFrameCaptured(
            object sender,
            FrameCapturedEventArgs e)
        {
            if (e == null ||
                e.Frame == null)
            {
                return;
            }

            if (!_isRunning ||
                Volatile.Read(ref _disposed) != 0)
            {
                e.Frame.Dispose();
                return;
            }

            lock (_statsLock) { _framesReceived++; }

            Bitmap oldFrame = null;

            lock (_pendingFrameLock)
            {
                oldFrame =
                    _pendingFrame;

                _pendingFrame =
                    e.Frame;
            }

            oldFrame?.Dispose();

            SignalFrame();
        }

        private Bitmap TakePendingFrame()
        {
            lock (_pendingFrameLock)
            {
                Bitmap frame =
                    _pendingFrame;

                _pendingFrame =
                    null;

                return frame;
            }
        }

        private void ClearPendingFrame()
        {
            Bitmap frame = null;

            lock (_pendingFrameLock)
            {
                frame =
                    _pendingFrame;

                _pendingFrame =
                    null;
            }

            frame?.Dispose();
        }

        private void SignalFrame()
        {
            try
            {
                _frameSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void AddRecentResult(
            VideoOcrResult result)
        {
            if (result == null)
                return;

            lock (_resultsLock)
            {
                _recentResults.Enqueue(
                    result);

                while (_recentResults.Count >
                       MaxRecentResults)
                {
                    VideoOcrResult oldResult =
                        _recentResults.Dequeue();

                    try
                    {
                        oldResult?.SourceFrame?.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void ClearRecentResults()
        {
            lock (_resultsLock)
            {
                while (_recentResults.Count > 0)
                {
                    VideoOcrResult result =
                        _recentResults.Dequeue();

                    try
                    {
                        result?.SourceFrame?.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private double CalculateSimpleConfidence(
            string recognizedText,
            Bitmap image)
        {
            if (string.IsNullOrWhiteSpace(
                recognizedText))
            {
                return 0;
            }

            string text =
                recognizedText.Trim();

            double confidence =
                0.40;

            if (text.Length >= 5)
                confidence += 0.08;

            if (text.Length >= 10)
                confidence += 0.08;

            if (text.Length >= 20)
                confidence += 0.06;

            int usefulCharacters =
                text.Count(
                    c =>
                        char.IsLetterOrDigit(c) ||
                        char.IsPunctuation(c) ||
                        char.IsWhiteSpace(c));

            double usefulRatio =
                text.Length > 0
                    ? (double)usefulCharacters / text.Length
                    : 0;

            confidence +=
                usefulRatio * 0.18;

            int uniqueCharacters =
                text.Distinct().Count();

            double diversity =
                text.Length > 0
                    ? (double)uniqueCharacters / text.Length
                    : 0;

            confidence +=
                Math.Min(
                    0.10,
                    diversity * 0.10);

            if (image != null &&
                image.Width > 0 &&
                image.Height > 0)
            {
                long imageArea =
                    (long)image.Width *
                    image.Height;

                if (imageArea >= 100000)
                {
                    confidence += 0.05;
                }
            }

            return Math.Max(
                0,
                Math.Min(
                    1,
                    confidence));
        }

        private static int NormalizeFrameRate(
            int frameRate)
        {
            if (frameRate < 1)
                return 1;

            if (frameRate > 60)
                return 60;

            return frameRate;
        }

        private void OnVideoError(
            object sender,
            VideoErrorEventArgs e)
        {
            if (e == null)
                return;

            _logger.LogError(
                $"Video hatası: {e.ErrorMessage}",
                e.Exception);

            RaiseOcrError(
                $"Video hatası: {e.ErrorMessage}",
                e.Exception);
        }

        private void OnComparisonCompleted(
            object sender,
            OcrComparisonCompletedEventArgs e)
        {
            EventHandler<OcrComparisonCompletedEventArgs> handlers =
                ComparisonCompleted;

            if (handlers == null)
                return;

            foreach (Delegate subscriber in
                     handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<OcrComparisonCompletedEventArgs>)subscriber)
                        .Invoke(
                            this,
                            e);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "ComparisonCompleted event işleyicisinde hata oluştu.",
                        ex);
                }
            }
        }

        private void RaiseOcrResultReady(
            VideoOcrResult result)
        {
            EventHandler<VideoOcrResultEventArgs> handlers =
                OcrResultReady;

            if (handlers == null)
                return;

            var args =
                new VideoOcrResultEventArgs(
                    result);

            foreach (Delegate subscriber in
                     handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<VideoOcrResultEventArgs>)subscriber)
                        .Invoke(
                            this,
                            args);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "OcrResultReady event işleyicisinde hata oluştu.",
                        ex);
                }
            }
        }

        private void RaiseOcrError(
            string errorMessage,
            Exception exception = null,
            int frameNumber = -1)
        {
            EventHandler<VideoOcrErrorEventArgs> handlers =
                OcrError;

            if (handlers == null)
                return;

            var args =
                new VideoOcrErrorEventArgs(
                    errorMessage,
                    exception,
                    frameNumber);

            foreach (Delegate subscriber in
                     handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<VideoOcrErrorEventArgs>)subscriber)
                        .Invoke(
                            this,
                            args);
                }
                catch
                {
                }
            }
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string t = text.Trim();
            t = Regex.Replace(t, @"\r\n|\r|\n", " ");
            t = Regex.Replace(t, @"\s{2,}", " ");
            return t;
        }

        private bool IsFrameSignificantlyChanged(Bitmap current)
        {
            if (current == null) return true;
            try
            {
                using (Mat curMat = BitmapConverter.ToMat(current))
                using (Mat curGray = new Mat())
                using (Mat curSmall = new Mat())
                {
                    if (curMat.Empty()) return true;
                    if (curMat.Channels() == 4) Cv2.CvtColor(curMat, curGray, ColorConversionCodes.BGRA2GRAY);
                    else if (curMat.Channels() == 3) Cv2.CvtColor(curMat, curGray, ColorConversionCodes.BGR2GRAY);
                    else curMat.CopyTo(curGray);
                    Cv2.Resize(curGray, curSmall, new OpenCvSharp.Size(64, 36));
                    lock (_thumbLock)
                    {
                        if (_previousThumb == null || _previousThumb.Empty() || _previousThumb.Width != 64 || _previousThumb.Height != 36)
                        {
                            if (_previousThumb != null) _previousThumb.Dispose();
                            _previousThumb = curSmall.Clone();
                            return true;
                        }
                        using (Mat diff = new Mat())
                        {
                            Cv2.Absdiff(curSmall, _previousThumb, diff);
                            Cv2.Threshold(diff, diff, 15, 255, ThresholdTypes.Binary);
                            int changed = Cv2.CountNonZero(diff);
                            int total = 64 * 36;
                            double ratio = (double)changed / total;
                            _previousThumb.Dispose();
                            _previousThumb = curSmall.Clone();
                            return ratio >= FrameChangeThreshold;
                        }
                    }
                }
            }
            catch { return true; }
        }

        public struct RealtimeStats
        {
            public int FramesReceived;
            public int FramesDroppedThrottle;
            public int FramesDroppedNoChange;
            public int OcrRuns;
            public int DuplicateTextsSkipped;
            public double AverageOcrMs;
        }

        public RealtimeStats GetStats()
        {
            lock (_statsLock)
            {
                double avg = _ocrRuns > 0 ? (_totalOcrTicks / (double)_ocrRuns) * 1000.0 / Stopwatch.Frequency : 0;
                return new RealtimeStats
                {
                    FramesReceived = _framesReceived,
                    FramesDroppedThrottle = _framesDroppedThrottle,
                    FramesDroppedNoChange = _framesDroppedNoChange,
                    OcrRuns = _ocrRuns,
                    DuplicateTextsSkipped = _duplicateTextsSkipped,
                    AverageOcrMs = avg
                };
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(
                    ref _disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(RealtimeVideoOcrService));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) != 0)
            {
                return;
            }

            try
            {
                StopAsync()
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                try
                {
                    _logger.LogError(
                        "RealtimeVideoOcrService kapatılırken hata oluştu.",
                        ex);
                }
                catch
                {
                }
            }

            _videoCaptureService.FrameCaptured -=
                OnFrameCaptured;

            _videoCaptureService.VideoError -=
                OnVideoError;

            _ocrComparisonService.ComparisonCompleted -=
                OnComparisonCompleted;

            ClearPendingFrame();
            ClearRecentResults();

            lock (_thumbLock)
            {
                _previousThumb?.Dispose();
                _previousThumb = null;
            }

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;

            _frameSignal.Dispose();
            _processingGate.Dispose();
            _lifecycleGate.Dispose();
        }
    }
}