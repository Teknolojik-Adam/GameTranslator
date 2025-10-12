using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public class RealtimeVideoOcrService : IRealtimeVideoOcrService, IDisposable
    {
        private readonly ILogger _logger;
        private readonly IVideoCaptureService _videoCaptureService;
        private readonly IOcrComparisonService _ocrComparisonService;
        private readonly IOcrAccuracyService _ocrAccuracyService;
        private readonly AppSettings _appSettings;

        private CancellationTokenSource _cancellationTokenSource;
        private Task _processingTask;
        private int _frameNumber = 0;
        private string _groundTruth = "";
        private readonly ConcurrentQueue<VideoOcrResult> _recentResults = new ConcurrentQueue<VideoOcrResult>();
        private readonly object _lockObject = new object();
        private bool _disposed = false;

        public event EventHandler<VideoOcrResultEventArgs> OcrResultReady;
        public event EventHandler<VideoOcrErrorEventArgs> OcrError;
        public event EventHandler<OcrComparisonCompletedEventArgs> ComparisonCompleted;

        public bool IsRunning { get; private set; }
        public int FrameRate { get; set; } = 30;
        public bool EnableComparison { get; set; } = true;
        public bool EnableAccuracyScoring { get; set; } = false;

        public RealtimeVideoOcrService(
            ILogger logger,
            IVideoCaptureService videoCaptureService,
            IOcrComparisonService ocrComparisonService,
            IOcrAccuracyService ocrAccuracyService,
            AppSettings appSettings)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _videoCaptureService = videoCaptureService ?? throw new ArgumentNullException(nameof(videoCaptureService));
            _ocrComparisonService = ocrComparisonService ?? throw new ArgumentNullException(nameof(ocrComparisonService));
            _ocrAccuracyService = ocrAccuracyService ?? throw new ArgumentNullException(nameof(ocrAccuracyService));
            _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));

            // Olay bağlama
            _videoCaptureService.FrameCaptured += OnFrameCaptured;
            _videoCaptureService.VideoError += OnVideoError;

            _ocrComparisonService.ComparisonCompleted += OnComparisonCompleted;
        }

        public async Task<bool> StartAsync(int deviceIndex = 0)
        {
            try
            {
                if (IsRunning)
                {
                    _logger.LogWarning("Gerçek zamanlı video OCR zaten çalışıyor");
                    return true;
                }

                _logger.LogInformation("Gerçek zamanlı video OCR hizmeti başlatılıyor...");

                // Video yakalamayı başlatmak için
                var captureStarted = await _videoCaptureService.StartCaptureAsync(deviceIndex);
                if (!captureStarted)
                {
                    _logger.LogError("Video yakalama başlatılamadı");
                    return false;
                }

                // Kare işleme görevini başlat
                _cancellationTokenSource = new CancellationTokenSource();
                _processingTask = ProcessFramesAsync(_cancellationTokenSource.Token);

                IsRunning = true;
                _frameNumber = 0;

                _logger.LogInformation("Gerçek zamanlı video OCR hizmeti başarıyla başlatıldı");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("Gerçek zamanlı video OCR hizmeti başlatılamadı", ex);
                OnOcrError("Gerçek zamanlı video OCR hizmeti başlatılamadı", ex);
                return false;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                if (!IsRunning)
                {
                    return;
                }

                _logger.LogInformation("Gerçek zamanlı video OCR hizmeti durduruluyor...");

                IsRunning = false;

                // İşlemi iptal et
                _cancellationTokenSource?.Cancel();
                if (_processingTask != null)
                {
                    await _processingTask;
                }

                // Video yakalamayı durdur
                await _videoCaptureService.StopCaptureAsync();

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                _logger.LogInformation("Gerçek zamanlı video OCR hizmeti durduruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError("Gerçek zamanlı video OCR hizmeti durdurulurken hata oluştu", ex);
                OnOcrError("Gerçek zamanlı video OCR hizmeti durdurulurken hata oluştu", ex);
            }
        }

        public async Task<VideoOcrResult> ProcessFrameAsync(Bitmap frame)
        {
            if (frame == null)
            {
                return null;
            }

            var frameNum = Interlocked.Increment(ref _frameNumber);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var result = new VideoOcrResult
                {
                    Timestamp = DateTime.Now,
                    FrameNumber = frameNum,
                    SourceFrame = frame
                };

                if (EnableComparison)
                {
                    // En iyi sonucu almak için karşılaştırma yap
                    var comparisonResult = await _ocrComparisonService.CompareEnginesAsync(frame, _appSettings.OcrLanguage);
                    result.ComparisonResult = comparisonResult;
                    result.UsedEngine = comparisonResult.BestEngine;

                    if (comparisonResult.EngineResults.ContainsKey(comparisonResult.BestEngine))
                    {
                        var bestResult = comparisonResult.EngineResults[comparisonResult.BestEngine];
                        result.RecognizedText = bestResult.RecognizedText;
                        result.Confidence = bestResult.Confidence;
                    }
                    else
                    {
                        result.RecognizedText = "";
                        result.Confidence = 0;
                    }
                }
                else
                {
                    // Tek motor kullan (mevcut uygulama ayarı)
                    var ocrService = new OcrService(_logger, _appSettings);
                    result.RecognizedText = await ocrService.GetTextFromImage(frame, _appSettings.OcrLanguage);
                    result.UsedEngine = _appSettings.OcrEngine;
                    result.Confidence = CalculateSimpleConfidence(result.RecognizedText, frame);
                }

                // Referans metin mevcutsa doğruluk puanını hesapla
                if (EnableAccuracyScoring && !string.IsNullOrEmpty(_groundTruth))
                {
                    result.AccuracyScore = await _ocrAccuracyService.CalculateAccuracyWithImageAsync(
                        frame, result.RecognizedText, _groundTruth);
                }

                stopwatch.Stop();
                result.ProcessingTime = stopwatch.Elapsed;

                // Son sonuçlar kuyruğa ekle
                _recentResults.Enqueue(result);
                while (_recentResults.Count > 100) // Yalnızca son 100 sonucu sakla
                {
                    _recentResults.TryDequeue(out _);
                }

                OnOcrResultReady(new VideoOcrResultEventArgs(result));
                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError($"Kare işleminde hata oluştu {frameNum}", ex);
                OnOcrError($"Kare işleminde hata oluştu {frameNum}", ex, frameNum);
                return null;
            }
        }

        public async Task<OcrAccuracyReport> GetAccuracyReportAsync()
        {
            return await Task.Run(async () =>
            {
                var testResults = new List<OcrTestResult>();

                foreach (var result in _recentResults.ToArray())
                {
                    if (result.AccuracyScore != null)
                    {
                        testResults.Add(new OcrTestResult
                        {
                            TestId = $"Kare_{result.FrameNumber}",
                            SourceImage = result.SourceFrame,
                            GroundTruth = _groundTruth,
                            RecognizedText = result.RecognizedText,
                            EngineType = result.UsedEngine,
                            TestTime = result.Timestamp,
                            ProcessingTime = result.ProcessingTime,
                            AccuracyScore = result.AccuracyScore
                        });
                    }
                }

                return await _ocrAccuracyService.GenerateDetailedReportAsync(testResults);
            });
        }

        public void SetGroundTruth(string groundTruth)
        {
            _groundTruth = groundTruth ?? "";
            _logger.LogInformation($"Referans metin ayarlandı: {(_groundTruth.Length > 50 ? _groundTruth.Substring(0, 50) + "..." : _groundTruth)}");
        }

        private async Task ProcessFramesAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && IsRunning)
                {
                    // Bu metot, kareler yakalandığında çağrılır
                    await Task.Delay(100, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // İptal edildi
            }
            catch (Exception ex)
            {
                _logger.LogError("Video OCR işleme döngüsünde hata oluştu", ex);
                OnOcrError("Video OCR işleme döngüsünde hata oluştu", ex);
            }
        }

        private async void OnFrameCaptured(object sender, FrameCapturedEventArgs e)
        {
            if (!IsRunning)
                return;

            try
            {
                await ProcessFrameAsync(e.Frame);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Yakalanan kare işleminde hata oluştu {e.FrameNumber}", ex);
                OnOcrError($"Yakalanan kare işleminde hata oluştu {e.FrameNumber}", ex, e.FrameNumber);
            }
        }

        private void OnVideoError(object sender, VideoErrorEventArgs e)
        {
            _logger.LogError($"Video hatası: {e.ErrorMessage}", e.Exception);
            OnOcrError($"Video hatası: {e.ErrorMessage}", e.Exception);
        }

        private void OnComparisonCompleted(object sender, OcrComparisonCompletedEventArgs e)
        {
            ComparisonCompleted?.Invoke(this, e);
        }

        private double CalculateSimpleConfidence(string recognizedText, Bitmap image)
        {
            if (string.IsNullOrWhiteSpace(recognizedText))
                return 0;

            double confidence = 0.5;

            // Metin uzunluğu faktörü
            if (recognizedText.Length > 10) confidence += 0.2;
            else if (recognizedText.Length > 5) confidence += 0.1;

            // Görüntü boyutu faktörü
            if (image != null)
            {
                var imageArea = image.Width * image.Height;
                if (imageArea > 100000) confidence += 0.1;
                else if (imageArea > 50000) confidence += 0.05;
            }

            // Karakter çeşitliliği
            var uniqueChars = recognizedText.Distinct().Count();
            var diversity = (double)uniqueChars / recognizedText.Length;
            confidence += diversity * 0.2;

            return Math.Min(1.0, confidence);
        }

        protected virtual void OnOcrResultReady(VideoOcrResultEventArgs e)
        {
            OcrResultReady?.Invoke(this, e);
        }

        protected virtual void OnOcrError(VideoOcrErrorEventArgs e)
        {
            OcrError?.Invoke(this, e);
        }

        protected virtual void OnOcrError(string errorMessage, Exception exception = null, int frameNumber = -1)
        {
            OnOcrError(new VideoOcrErrorEventArgs(errorMessage, exception, frameNumber));
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
                    StopAsync().Wait(5000); // 5 saniyeye kadar bekle

                    if (_videoCaptureService != null)
                    {
                        _videoCaptureService.FrameCaptured -= OnFrameCaptured;
                        _videoCaptureService.VideoError -= OnVideoError;
                    }

                    if (_ocrComparisonService != null)
                    {
                        _ocrComparisonService.ComparisonCompleted -= OnComparisonCompleted;
                    }
                }
                _disposed = true;
            }
        }

        ~RealtimeVideoOcrService()
        {
            Dispose(false);
        }
    }
}