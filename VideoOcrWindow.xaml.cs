using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace P5S_ceviri
{
    public partial class VideoOcrWindow : Window
    {
        private readonly ILogger _logger;
        private readonly AppSettings _appSettings;
        private readonly IRealtimeVideoOcrService _videoOcrService;
        private readonly IVideoCaptureService _videoCaptureService;
        private readonly IOcrComparisonService _ocrComparisonService;
        private readonly IOcrAccuracyService _ocrAccuracyService;

        private DispatcherTimer _updateTimer;
        private int _framesProcessed = 0;
        private double _totalProcessingTime = 0;
        private List<VideoOcrResult> _recentResults = new List<VideoOcrResult>();

        public VideoOcrWindow(
            ILogger logger,
            AppSettings appSettings,
            IRealtimeVideoOcrService videoOcrService,
            IVideoCaptureService videoCaptureService,
            IOcrComparisonService ocrComparisonService,
            IOcrAccuracyService ocrAccuracyService)
        {
            InitializeComponent();

            _logger = logger;
            _appSettings = appSettings;
            _videoOcrService = videoOcrService;
            _videoCaptureService = videoCaptureService;
            _ocrComparisonService = ocrComparisonService;
            _ocrAccuracyService = ocrAccuracyService;

            InitializeUI();
            SubscribeToEvents();
            LoadSettings();
        }

        private void InitializeUI()
        {
            // Çözünürlük seçim kutusunu baþlat
            ResolutionComboBox.SelectedIndex = 0;

            // Metin göstergelerini mevcut deðerlerle baþlat
            if (FrameRateText != null && _appSettings != null)
            {
                FrameRateText.Text = $"{_appSettings.VideoOcrFrameRate} FPS";
            }

            if (ConfidenceThresholdText != null && _appSettings != null)
            {
                ConfidenceThresholdText.Text = $"{_appSettings.OcrConfidenceThreshold:P0}";
            }

            // Güncelleme zamanlayýcýsýný baþlat
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _updateTimer.Tick += UpdateTimer_Tick;
            _updateTimer.Start();

            // Baþlangýçtaki arayüz durumunu ayarla
            UpdateUIState(false);
        }

        private void SubscribeToEvents()
        {
            _videoOcrService.OcrResultReady += OnOcrResultReady;
            _videoOcrService.OcrError += OnOcrError;
            _videoOcrService.ComparisonCompleted += OnComparisonCompleted;
        }

        private void LoadSettings()
        {
            // _appSettings için null kontrolü
            if (_appSettings != null)
            {
                // Video ayarlarýný yükle - null referans sorunlarýný önlemek için olaylarý geçici olarak devre dýþý býrak
                FrameRateSlider.ValueChanged -= FrameRateSlider_ValueChanged;
                FrameRateSlider.Value = _appSettings.VideoOcrFrameRate;
                FrameRateSlider.ValueChanged += FrameRateSlider_ValueChanged;

                ConfidenceThresholdSlider.ValueChanged -= ConfidenceThresholdSlider_ValueChanged;
                ConfidenceThresholdSlider.Value = _appSettings.OcrConfidenceThreshold;
                ConfidenceThresholdSlider.ValueChanged += ConfidenceThresholdSlider_ValueChanged;

                EnableComparisonCheckBox.IsChecked = _appSettings.EnableOcrComparison;
                EnableAccuracyScoringCheckBox.IsChecked = _appSettings.EnableOcrAccuracyScoring;
                EnableRegionDetectionCheckBox.IsChecked = _appSettings.EnableOcrRegionDetection;
            }

            // Mevcut kameralarý yükle
            LoadAvailableCameras();
        }

        private async void LoadAvailableCameras()
        {
            try
            {
                if (_videoCaptureService == null)
                {
                    StatusBarText.Text = "Video yakalama hizmeti mevcut deðil";
                    return;
                }

                var devices = _videoCaptureService.GetAvailableDevices();
                CameraComboBox.Items.Clear();

                foreach (var device in devices)
                {
                    CameraComboBox.Items.Add(device);
                }

                if (devices.Length > 0)
                {
                    CameraComboBox.SelectedIndex = _appSettings.VideoOcrDeviceIndex;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Mevcut kameralar yüklenirken hata oluþtu", ex);
                StatusBarText.Text = "Kameralar yüklenirken hata oluþtu";
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusBarText.Text = "Video OCR baþlatýlýyor...";
                StartButton.IsEnabled = false;

                // Ayarlarý güncelle
                UpdateSettingsFromUI();

                // Video OCR hizmetini baþlat
                var started = await _videoOcrService.StartAsync(_appSettings.VideoOcrDeviceIndex);

                if (started)
                {
                    UpdateUIState(true);
                    StatusBarText.Text = "Video OCR baþarýyla baþlatýldý";
                    StatusText.Text = "Çalýþýyor";
                    StatusText.Foreground = new SolidColorBrush(Colors.Green);
                }
                else
                {
                    StatusBarText.Text = "Video OCR baþlatýlamadý";
                    StartButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Video OCR baþlatýlýrken hata oluþtu", ex);
                StatusBarText.Text = "Video OCR baþlatýlýrken hata oluþtu";
                StartButton.IsEnabled = true;
            }
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusBarText.Text = "Video OCR durduruluyor...";
                StopButton.IsEnabled = false;

                await _videoOcrService.StopAsync();

                UpdateUIState(false);
                StatusBarText.Text = "Video OCR durduruldu";
                StatusText.Text = "Durduruldu";
                StatusText.Foreground = new SolidColorBrush(Colors.Red);
            }
            catch (Exception ex)
            {
                _logger.LogError("Video OCR durdurulurken hata oluþtu", ex);
                StatusBarText.Text = "Video OCR durdurulurken hata oluþtu";
                StopButton.IsEnabled = true;
            }
        }

        private async void CaptureFrameButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var frame = await _videoCaptureService.CaptureFrameAsync();
                if (frame != null)
                {
                    var result = await _videoOcrService.ProcessFrameAsync(frame);
                    if (result != null)
                    {
                        UpdateOCRResult(result);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Kare yakalanýrken hata oluþtu", ex);
                StatusBarText.Text = "Kare yakalanýrken hata oluþtu";
            }
        }

        private async void GenerateReportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusBarText.Text = "Doðruluk raporu oluþturuluyor...";
                GenerateReportButton.IsEnabled = false;

                var report = await _videoOcrService.GetAccuracyReportAsync();

                if (report != null)
                {
                    ShowAccuracyReport(report);
                }

                StatusBarText.Text = "Rapor oluþturuldu";
            }
            catch (Exception ex)
            {
                _logger.LogError("Rapor oluþturulurken hata oluþtu", ex);
                StatusBarText.Text = "Rapor oluþturulurken hata oluþtu";
            }
            finally
            {
                GenerateReportButton.IsEnabled = true;
            }
        }

        private void CameraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CameraComboBox.SelectedIndex >= 0)
            {
                // _appSettings için null kontrolü
                if (_appSettings != null)
                {
                    _appSettings.VideoOcrDeviceIndex = CameraComboBox.SelectedIndex;
                }
            }
        }

        private void FrameRateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var frameRate = (int)e.NewValue;

            // Kontrol henüz baþlatýlmadýysa çökmemesi için null kontrolü
            if (FrameRateText != null)
            {
                FrameRateText.Text = $"{frameRate} FPS";
            }

            // _appSettings için null kontrolü
            if (_appSettings != null)
            {
                _appSettings.VideoOcrFrameRate = frameRate;
            }

            if (_videoOcrService != null)
            {
                _videoOcrService.FrameRate = frameRate;
            }
        }

        private void ResolutionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResolutionComboBox.SelectedItem is ComboBoxItem item && item.Tag is string resolution)
            {
                var parts = resolution.Split(',');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int width) &&
                    int.TryParse(parts[1], out int height))
                {
                    // _appSettings için null kontrolü
                    if (_appSettings != null)
                    {
                        _appSettings.VideoOcrWidth = width;
                        _appSettings.VideoOcrHeight = height;
                    }
                }
            }
        }

        private void ConfidenceThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var threshold = e.NewValue;

            // Kontrol henüz baþlatýlmadýysa çökmemesi için null kontrolü
            if (ConfidenceThresholdText != null)
            {
                ConfidenceThresholdText.Text = $"{threshold:P0}";
            }

            // _appSettings için null kontrolü
            if (_appSettings != null)
            {
                _appSettings.OcrConfidenceThreshold = threshold;
            }
        }

        private void GroundTruthTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_videoOcrService != null)
            {
                _videoOcrService.SetGroundTruth(GroundTruthTextBox.Text);
            }
        }

        private void UpdateSettingsFromUI()
        {
            // _appSettings için null kontrolü
            if (_appSettings != null)
            {
                _appSettings.VideoOcrFrameRate = (int)FrameRateSlider.Value;
                _appSettings.OcrConfidenceThreshold = ConfidenceThresholdSlider.Value;
                _appSettings.EnableOcrComparison = EnableComparisonCheckBox.IsChecked ?? false;
                _appSettings.EnableOcrAccuracyScoring = EnableAccuracyScoringCheckBox.IsChecked ?? false;
                _appSettings.EnableOcrRegionDetection = EnableRegionDetectionCheckBox.IsChecked ?? false;
                _appSettings.VideoOcrDeviceIndex = CameraComboBox.SelectedIndex;
            }
        }

        private void UpdateUIState(bool isRunning)
        {
            StartButton.IsEnabled = !isRunning;
            StopButton.IsEnabled = isRunning;
            CameraComboBox.IsEnabled = !isRunning;
            FrameRateSlider.IsEnabled = !isRunning;
            ResolutionComboBox.IsEnabled = !isRunning;
        }

        private void UpdateOCRResult(VideoOcrResult result)
        {
            Dispatcher.Invoke(() =>
            {
                // Tanýnan metni güncelle
                RecognizedTextBlock.Text = result.RecognizedText ?? "Metin tanýnmadý";

                // Motor ve güvenilirlik bilgisini güncelle
                EngineText.Text = result.UsedEngine.ToString();
                ConfidenceText.Text = $"{result.Confidence:P1}";
                FrameText.Text = result.FrameNumber.ToString();

                // Varsa doðruluk oranýný güncelle
                if (result.AccuracyScore != null)
                {
                    AccuracyText.Text = $"{result.AccuracyScore.OverallScore:P1}";
                }

                // Ýstatistikleri güncelle
                _framesProcessed++;
                _totalProcessingTime += result.ProcessingTime.TotalMilliseconds;
                _recentResults.Add(result);

                // Sadece son sonuçlarý tut
                if (_recentResults.Count > _appSettings.OcrResultHistorySize)
                {
                    _recentResults.RemoveAt(0);
                }
            });
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            // Ýstatistik göstergesini güncelle
            FramesProcessedText.Text = _framesProcessed.ToString();

            if (_framesProcessed > 0)
            {
                var avgTime = _totalProcessingTime / _framesProcessed;
                AvgProcessingTimeText.Text = $"{avgTime:F1} ms";
                PerformanceText.Text = $"Ort: {avgTime:F1}ms, Kare: {_framesProcessed}";
            }

            // En iyi motoru güncelle
            if (_recentResults.Any())
            {
                var bestEngine = _recentResults
                    .GroupBy(r => r.UsedEngine)
                    .OrderByDescending(g => g.Average(r => r.Confidence))
                    .First().Key;
                BestEngineText.Text = bestEngine.ToString();
            }

            // Genel doðruluk oranýný güncelle
            if (_recentResults.Any(r => r.AccuracyScore != null))
            {
                var avgAccuracy = _recentResults
                    .Where(r => r.AccuracyScore != null)
                    .Average(r => r.AccuracyScore.OverallScore);
                OverallAccuracyText.Text = $"{avgAccuracy:P1}";
            }
        }

        private void OnOcrResultReady(object sender, VideoOcrResultEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateOCRResult(e.Result);

                // Eðer bir kare varsa video görüntüsünü güncelle
                if (e.Result.SourceFrame != null)
                {
                    UpdateVideoDisplay(e.Result.SourceFrame);
                }
            });
        }

        private void OnOcrError(object sender, VideoOcrErrorEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                StatusBarText.Text = $"OCR Hatasý: {e.ErrorMessage}";
                _logger.LogError($"OCR Hatasý: {e.ErrorMessage}", e.Exception);
            });
        }

        private void OnComparisonCompleted(object sender, OcrComparisonCompletedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var result = e.Result;
                _logger.LogInformation($"Karþýlaþtýrma tamamlandý - En Ýyi: {result.BestEngine}, Güvenilirlik: {result.BestConfidence:P}");
            });
        }

        private void UpdateVideoDisplay(Bitmap frame)
        {
            try
            {
                var bitmapSource = ConvertBitmapToBitmapSource(frame);
                VideoDisplay.Source = bitmapSource;
                VideoPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                _logger.LogError("Video görüntüsü güncellenirken hata oluþtu", ex);
            }
        }

        private BitmapSource ConvertBitmapToBitmapSource(Bitmap bitmap)
        {
            var hBitmap = bitmap.GetHbitmap();
            try
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }

        private void ShowAccuracyReport(OcrAccuracyReport report)
        {
            var reportWindow = new AccuracyReportWindow(report);
            reportWindow.Owner = this;
            reportWindow.ShowDialog();
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                if (_videoOcrService != null)
                {
                    _videoOcrService.StopAsync().Wait(5000);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Pencere kapatýlýrken video OCR durdurulurken hata oluþtu", ex);
            }

            _updateTimer?.Stop();
            base.OnClosed(e);
        }
    }
}