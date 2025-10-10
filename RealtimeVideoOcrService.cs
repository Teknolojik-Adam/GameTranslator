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
                    _logger.LogWarning("Ger�ek zamanl� video OCR zaten �al���yor");
                    return true;
                }

                _logger.LogInformation("Ger�ek zamanl� video OCR hizmeti ba�lat�l�yor...");

                // Video yakalamay� ba�lat
                var captureStarted = await _videoCaptureService.StartCaptureAsync(deviceIndex);
                if (!captureStarted)
                {
                    _logger.LogError("Video yakalama ba�lat�lamad�");
                    return false;
                }

                // ��leme g�revini ba�lat
                _cancellationTokenSource = new CancellationTokenSource();
                _processingTask = ProcessFramesAsync(_cancellationTokenSource.Token);

                IsRunning = true;
                _frameNumber = 0;

                _logger.LogInformation("Ger�ek zamanl� video OCR hizmeti ba�ar�yla ba�lat�ld�");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("Ger�ek zamanl� video OCR hizmeti ba�lat�lamad�", ex);
                OnOcrError("Ger�ek zamanl� video OCR hizmeti ba�lat�lamad�", ex);
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

                _logger.LogInformation("Ger�ek zamanl� video OCR hizmeti durduruluyor...");

                IsRunning = false;

                // ��leme g�revini durdur
                _cancellationTokenSource?.Cancel();
                if (_processingTask != null)
                {
                    await _processingTask;
                }

                // Video yakalamay� durdur
                await _videoCaptureService.StopCaptureAsync();

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                _logger.LogInformation("Ger�ek zamanl� video OCR hizmeti durduruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError("Ger�ek zamanl� video OCR hizmeti durdurulurken hata olu�tu", ex);
                OnOcrError("Ger�ek zamanl� video OCR hizmeti durdurulurken hata olu�tu", ex);
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
                    // En iyi sonucu almak i�in kar��la�t�rma yap
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
                    // Tek motor kullan (mevcut uygulama ayar�)
                    var ocrService = new OcrService(_logger, _appSettings);
                    result.RecognizedText = await ocrService.GetTextFromImage(frame, _appSettings.OcrLanguage);
                    result.UsedEngine = _appSettings.OcrEngine;
                    result.Confidence = CalculateSimpleConfidence(result.RecognizedText, frame);
                }

                // Referans metin mevcutsa do�ruluk puan�n� hesapla
                if (EnableAccuracyScoring && !string.IsNullOrEmpty(_groundTruth))
                {
                    result.AccuracyScore = await _ocrAccuracyService.CalculateAccuracyWithImageAsync(
                        frame, result.RecognizedText, _groundTruth);
                }

                stopwatch.Stop();
                result.ProcessingTime = stopwatch.Elapsed;

                // Son sonu�lar kuyru�una ekle
                _recentResults.Enqueue(result);
                while (_recentResults.Count > 100) // Yaln�zca son 100 sonucu sakla
                {
                    _recentResults.TryDequeue(out _);
                }

                OnOcrResultReady(new VideoOcrResultEventArgs(result));
                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError($"Kare i�lenirken hata olu�tu {frameNum}", ex);
                OnOcrError($"Kare i�lenirken hata olu�tu {frameNum}", ex, frameNum);
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
            _logger.LogInformation($"Referans metin ayarland�: {(_groundTruth.Length > 50 ? _groundTruth.Substring(0, 50) + "..." : _groundTruth)}");
        }

        private async Task ProcessFramesAsync(CancellationToken cancellationToken)
        {
            try
            { // As�l i�leme OnFrameCaptured i�inde ger�ekle�ir
                while (!cancellationToken.IsCancellationRequested && IsRunning)
                {
                    // Bu metot, kareler yakaland���nda �a�r�l�r
                   
                    await Task.Delay(100, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
               
            }
            catch (Exception ex)
            {
                _logger.LogError("Video OCR i�leme d�ng�s�nde hata olu�tu", ex);
                OnOcrError("Video OCR i�leme d�ng�s�nde hata olu�tu", ex);
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
                _logger.LogError($"Yakalanan kare i�lenirken hata olu�tu {e.FrameNumber}", ex);
                OnOcrError($"Yakalanan kare i�lenirken hata olu�tu {e.FrameNumber}", ex, e.FrameNumber);
            }
        }

        private void OnVideoError(object sender, VideoErrorEventArgs e)
        {
            _logger.LogError($"Video hatas�: {e.ErrorMessage}", e.Exception);
            OnOcrError($"Video hatas�: {e.ErrorMessage}", e.Exception);
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

            // Metin uzunlu�u fakt�r�
            if (recognizedText.Length > 10) confidence += 0.2;
            else if (recognizedText.Length > 5) confidence += 0.1;

            // G�r�nt� boyutu fakt�r�
            if (image != null)
            {
                var imageArea = image.Width * image.Height;
                if (imageArea > 100000) confidence += 0.1;
                else if (imageArea > 50000) confidence += 0.05;
            }

            // Karakter �e�itlili�i
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