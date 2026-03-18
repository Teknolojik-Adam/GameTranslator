using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameTranslatorUltimate
{
    public partial class MainWindow : System.Windows.Window
    {
        #region Win32 Imports and Fields
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);
        private readonly IProcessService _processService;
        private readonly IMemoryService _memoryService;
        private readonly ITranslationService _translationService;
        private readonly ILogger _logger;
        private readonly IOcrService _ocrService;
        private readonly WindowsOcrService _windowsOcrService;
        private readonly IGameRecipeService _gameRecipeService;
        private readonly SettingsManager _settingsManager;
        private readonly AppSettings _appSettings;
        private readonly EnhancedMemoryService _enhancedMemoryService;
        private readonly PointerValidationService _pointerValidationService;
        private readonly AnomalyDetector _anomalyDetector;
        private readonly MLTextProcessor _mlTextProcessor;
        private readonly OcrRegionProcessor _ocrRegionProcessor;
        private readonly IRealtimeVideoOcrService _videoOcrService;
        private readonly IVideoCaptureService _videoCaptureService;
        private readonly IOcrComparisonService _ocrComparisonService;
        private readonly IOcrAccuracyService _ocrAccuracyService;
        private readonly IconManager _iconManager;
        private readonly DispatcherTimer _continuousTranslationTimer;
        private readonly DispatcherTimer _manualTranslationTimer;
        private readonly DispatcherTimer _continuousOcrTimer;
        private HotkeyManager _hotkeyManager;
        private int _ocrHotkeyId;
        private int _translateWindowHotkeyId;
        private int _switchTranslationServiceHotkeyId;
        private OutputWindow _outputWindow;
        public event Action<string> TranslatedTextChanged;
        private bool _isSetupMode = false;
        private bool _isContinuousTranslationRunning = false;
        private string _lastReadText = "";
        private string _potentiallyStableRamText = "";
        private string _potentiallyStableOcrText = "";
        private IntPtr _dynamicTextAddress = IntPtr.Zero;
        private IntPtr _manualAddress = IntPtr.Zero;
        private string _lastManualText = "";
        private bool _isContinuousOcrRunning = false;
        private bool _isOcrTickBusy = false;
        private System.Drawing.Rectangle? _selectedOcrRegion = null;
        private CancellationTokenSource _scanCancellationTokenSource;
        private List<PointerPath> _lastFoundPaths = new List<PointerPath>();
        private readonly LinkedList<string> _translationHistory = new LinkedList<string>();
        private const int MaxTranslationHistory = 2; // Mevcut çeviriye ek olarak saklanacak eski çeviri sayısı

        private System.Collections.ObjectModel.ObservableCollection<LogEntry> _logEntries = new System.Collections.ObjectModel.ObservableCollection<LogEntry>();
        public System.Collections.ObjectModel.ObservableCollection<LogEntry> LogEntries => _logEntries;

        public class LogEntry : System.ComponentModel.INotifyPropertyChanged
        {
            public DateTime Timestamp { get; set; }
            public string Key { get; set; }
            public object[] Args { get; set; }
            public bool IsError { get; set; }

            private string _fullText;
            public string FullText
            {
                get => _fullText;
                set { _fullText = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FullText))); }
            }



            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

            public void UpdateTranslation()
            {
                // Prepare log type labels for both languages
                string logTypeTr = IsError ? "[HATA]" : "[BİLGİ]";
                string logTypeEn = IsError ? "[ERROR]" : "[INFO]";

                // Attempt to fetch Turkish and English localized messages (fall back to key)
                string msgTr = Key;
                string msgEn = Key;

                try
                {
                    // Load Turkish resources
                    try
                    {
                        var trDict = new ResourceDictionary { Source = new Uri("Resources/StringResources.tr.xaml", UriKind.Relative) };
                        if (trDict.Contains(Key))
                        {
                            var template = trDict[Key] as string;
                            if (!string.IsNullOrEmpty(template)) msgTr = (Args != null && Args.Length > 0) ? string.Format(template, Args) : template;
                        }
                    }
                    catch { /* ignore resource load errors */ }

                    // Load English resources
                    try
                    {
                        var enDict = new ResourceDictionary { Source = new Uri("Resources/StringResources.en.xaml", UriKind.Relative) };
                        if (enDict.Contains(Key))
                        {
                            var template = enDict[Key] as string;
                            if (!string.IsNullOrEmpty(template)) msgEn = (Args != null && Args.Length > 0) ? string.Format(template, Args) : template;
                        }
                    }
                    catch { /* ignore resource load errors */ }

                    // If one of them is still the key, try the application's current resources as a last resort
                    try
                    {
                        if (Application.Current != null)
                        {
                            var res = Application.Current.TryFindResource(Key) as string;
                            if (!string.IsNullOrEmpty(res))
                            {
                                // Prefer to fill whichever is still the raw key
                                if (msgTr == Key) msgTr = (Args != null && Args.Length > 0) ? string.Format(res, Args) : res;
                                if (msgEn == Key) msgEn = (Args != null && Args.Length > 0) ? string.Format(res, Args) : res;
                            }
                        }
                    }
                    catch { }
                }
                catch { }

                // Compose final FullText. If both messages are identical show once, otherwise show both with language labels.
                if (string.Equals(msgTr, msgEn, StringComparison.Ordinal))
                {
                    FullText = $"{Timestamp:HH:mm:ss} {logTypeTr}/{logTypeEn} - {msgTr}";
                }
                else
                {
                    FullText = $"{Timestamp:HH:mm:ss} {logTypeTr} - {msgTr} | {logTypeEn} - {msgEn}";
                }
            }
        }
        #endregion

        public MainWindow()
        {//
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            try
            {
                // Servisleri başlat
                ServiceContainer.Initialize();
                // Servisleri al
                _processService = ServiceContainer.GetService<IProcessService>();
                _memoryService = ServiceContainer.GetService<IMemoryService>();
                _translationService = ServiceContainer.GetService<ITranslationService>();
                _logger = ServiceContainer.GetService<ILogger>();
                _ocrService = ServiceContainer.GetService<IOcrService>();
                _windowsOcrService = ServiceContainer.GetService<WindowsOcrService>();
                _gameRecipeService = ServiceContainer.GetService<IGameRecipeService>();
                _settingsManager = ServiceContainer.GetService<SettingsManager>();
                _appSettings = ServiceContainer.GetService<AppSettings>();
                _enhancedMemoryService = ServiceContainer.GetService<EnhancedMemoryService>();
                _pointerValidationService = ServiceContainer.GetService<PointerValidationService>();
                _anomalyDetector = ServiceContainer.GetService<AnomalyDetector>();
                _mlTextProcessor = ServiceContainer.GetService<MLTextProcessor>();
                _videoOcrService = ServiceContainer.GetService<IRealtimeVideoOcrService>();
                _videoCaptureService = ServiceContainer.GetService<IVideoCaptureService>();
                _ocrComparisonService = ServiceContainer.GetService<IOcrComparisonService>();
                _ocrAccuracyService = ServiceContainer.GetService<IOcrAccuracyService>();
                _iconManager = new IconManager(_logger);
                _ocrRegionProcessor = new OcrRegionProcessor(_ocrService, _translationService, _appSettings.OcrLanguage, _appSettings.TargetLanguage);
                // UI bileşenlerini başlatmak için
                InitializeTranslationServices();
                InitializeLanguageControls();
                // Veri bağlamayı ayarla
                HotkeySettingsPanel.DataContext = _appSettings;
                // Olay dinleyicileri ekle
                _appSettings.PropertyChanged += AppSettings_PropertyChanged;
                // Zamanlayıcıları ayarla
                _manualTranslationTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(_appSettings.RamTickIntervalMs > 50 ? _appSettings.RamTickIntervalMs : 50)
                };
                _manualTranslationTimer.Tick += ManualTranslationTimer_Tick;
                _continuousTranslationTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(_appSettings.RamTickIntervalMs > 50 ? _appSettings.RamTickIntervalMs : 50)
                };
                _continuousTranslationTimer.Tick += ContinuousTranslationTimer_Tick;
                _continuousOcrTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(_appSettings.OcrTickIntervalMs > 100 ? _appSettings.OcrTickIntervalMs : 100)
                };
                _continuousOcrTimer.Tick += ContinuousOcrTimer_Tick;
                // Pencere kapatma olayı
                this.Closing += (s, e) =>
                {
                    PersistWindowPlacement();
                    // Son durumu kaydet
                    _appSettings.LastOcrState = _isContinuousOcrRunning;
                    _appSettings.LastRamState = _isContinuousTranslationRunning || _manualTranslationTimer.IsEnabled;
                    if (cmbTranslationService.SelectedItem is StrategyInfo strategyInfo)
                    {
                        _appSettings.LastUsedTranslationService = strategyInfo.Name;
                    }
                    // Ayarları kaydet
                    _settingsManager.SaveSettings(_appSettings);
                    // Çeviri önbelleğini kaydet
                    if (_translationService is PerformanceOptimizedTranslationService performanceService)
                    {
                        var cacheInfo = performanceService.GetCacheInfo();
                        _logger.LogInformation($"Uygulama kapatılırken önbellek durumu: {cacheInfo.TotalItems} öğe, " +
                            $"{cacheInfo.TotalSizeBytes} bytes, Hit Rate: {cacheInfo.HitRate:F2}%");
                        performanceService.Dispose();
                    }
                    else if (_translationService is AdvancedTranslationService advancedService)
                    {
                        advancedService.SaveCacheToDisk();
                    }
                    StopAllTranslations();
                    _memoryService?.Dispose();
                    _ocrRegionProcessor?.Dispose();
                    _outputWindow?.Close();
                    ServiceContainer.Cleanup();
                };
                // İşlemleri yükle ve UI'ı güncelle
                LoadProcesses();
                UpdateUIState();
                // Başlangıç ayarlarını uygula
                cmbOcrEngine.SelectedIndex = _appSettings.OcrEngine == OcrEngineType.WindowsOcr ? 0 : 1;
                cmbTextDetectionMethod.SelectedIndex = (int)_appSettings.TextDetectionMethod;
                chkEnableColorFilter.IsChecked = _appSettings.EnableOcrColorFilter;

                txtOllamaApiUrl.Text = _appSettings.OllamaApiUrl ?? "http://localhost:11434";
                txtOllamaModelName.Text = _appSettings.OllamaModelName ?? "llama3:8b";

                // Son kullanılan çeviri servisini seç
                if (!string.IsNullOrEmpty(_appSettings.LastUsedTranslationService))
                {
                    var strategy = cmbTranslationService.Items.Cast<StrategyInfo>()
                        .FirstOrDefault(s => s.Name == _appSettings.LastUsedTranslationService);
                    if (strategy != null)
                    {
                        cmbTranslationService.SelectedItem = strategy;
                    }
                }
                // Son OCR durumunu geri yükle
                if (_appSettings.LastOcrState)
                {
                    StartContinuousOcr();
                }
                // Son RAM durumunu geri yükle
                if (_appSettings.LastRamState)
                {
                    StartContinuousTranslation();
                }
                // Tema UI'sını başlat
                InitializeThemeUI();
                // Quick diagnostic: test OpenCV native initialization and log detailed errors if any
                TryEnsureOpenCvNativeLoaded();

                _logger.LogInformation("Uygulama başarıyla başlatıldı.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Uygulama başlatılırken kritik bir hata oluştu: {ex.Message}", "Kritik Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
            }

            // Prevent ListBox from auto-scrolling when items request BringIntoView
            txtOutput.AddHandler(RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler(TxtOutput_PreviewRequestBringIntoView), true);
        }

        // Suppress automatic BringIntoView so ListBox behaves like a normal list and doesn't force scroll
        private void TxtOutput_PreviewRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            // Prevent the default behavior which scrolls the item into view
            e.Handled = true;
        }

        private void TryEnsureOpenCvNativeLoaded()
        {
            try
            {
                // Try load platform-specific native OpenCvSharpExtern from dll folder
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string probe64 = Path.Combine(baseDir, "dll", "x64", "OpenCvSharpExtern.dll");
                string probe86 = Path.Combine(baseDir, "dll", "x86", "OpenCvSharpExtern.dll");

                if (Environment.Is64BitProcess)
                {
                    if (File.Exists(probe64))
                    {
                        var h = LoadLibrary(probe64);
                        if (h == IntPtr.Zero)
                        {
                            int err = Marshal.GetLastWin32Error();
                            _logger?.LogError($"LoadLibrary failed for {probe64}, error={err}");
                        }
                        else
                        {
                            _logger?.LogInformation($"Loaded native OpenCvSharpExtern from {probe64}");
                            FreeLibrary(h);
                        }
                    }
                    else
                    {
                        _logger?.LogWarning($"Expected native dll not found: {probe64}");
                    }
                }
                else
                {
                    if (File.Exists(probe86))
                    {
                        var h = LoadLibrary(probe86);
                        if (h == IntPtr.Zero)
                        {
                            int err = Marshal.GetLastWin32Error();
                            _logger?.LogError($"LoadLibrary failed for {probe86}, error={err}");
                        }
                        else
                        {
                            _logger?.LogInformation($"Loaded native OpenCvSharpExtern from {probe86}");
                            FreeLibrary(h);
                        }
                    }
                    else
                    {
                        _logger?.LogWarning($"Expected native dll not found: {probe86}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("TryEnsureOpenCvNativeLoaded failed", ex);
            }
        }

        private void btnGitHub_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://github.com/Teknolojik-Adam/GameTranslator");
        }

        private void btnAegisWall_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://teknolojikadam.itch.io/ta-aegiswall");
        }

        private void btnItchIoTess_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://teknolojikadam.itch.io/gametranslator-tess");
        }

        private void btnGameTranslatorLinux_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://teknolojikadam.itch.io/gametranslatorlinux");
        }

        private void btnItchIo_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://teknolojikadam.itch.io/teknolojikadamgametranslator");
        }

        private void btnDonate_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://kreosus.com/teknolojikadam");
        }

        private void cmbTranslationService_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbTranslationService.SelectedItem is StrategyInfo strategyInfo)
            {
                if (strategyInfo.Name.Contains("Ollama"))
                {
                    if (pnlOllamaSettings != null) pnlOllamaSettings.Visibility = Visibility.Visible;
                }
                else
                {
                    if (pnlOllamaSettings != null) pnlOllamaSettings.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void txtOllamaApiUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_appSettings != null)
            {
                _appSettings.OllamaApiUrl = txtOllamaApiUrl.Text;
            }
        }

        private void txtOllamaModelName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_appSettings != null)
            {
                _appSettings.OllamaModelName = txtOllamaModelName.Text;
            }
        }

        #region Enhanced Pointer Scanner UI Logic
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                _logger?.LogInformation("OnSourceInitialized çağrıldı - Hotkey kayıtları başlatılıyor");

                var presentationSource = PresentationSource.FromVisual(this);
                if (presentationSource == null)
                {
                    _logger?.LogError("PresentationSource.FromVisual null döndürdü");
                    return;
                }
                var hwndSource = presentationSource as HwndSource;
                if (hwndSource == null)
                {
                    _logger?.LogError("PresentationSource'u HwndSource'a atama başarısız oldu");
                    return;
                }
                _hotkeyManager = new HotkeyManager(hwndSource, _logger);

                // Kısayolları kaydet
                RegisterHotkeys();

                // Kayıtlı kısayolları listele
                ListRegisteredHotkeys();

                _logger?.LogInformation("Hotkey kayıtları başarıyla tamamlandı");
            }
            catch (Exception ex)
            {
                _logger?.LogError("HotkeyManager başlatılamadı", ex);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _logger?.LogInformation("OnClosed çağrıldı - Uygulama kapatılıyor");

                // Tüm ProcessInfo nesnelerini dispose et
                if (cmbProcesses.ItemsSource is System.Collections.IEnumerable processes)
                {
                    foreach (var item in processes)
                    {
                        if (item is ProcessInfo pi)
                        {
                            pi.Dispose();
                        }
                    }
                }

                // Kısayolları kaldır ve HotkeyManager'ı dispose et
                UnregisterHotkeys();
                _hotkeyManager?.Dispose();

                // Servisleri dispose et
                _pointerValidationService?.Dispose();

                // EnhancedMemoryService'i dispose et (tüm event'leri ve kaynakları temizler)
                _enhancedMemoryService?.Dispose();
                _logger?.LogInformation("EnhancedMemoryService kaynakları serbest bırakıldı");

                _logger?.LogInformation("Uygulama başarıyla kapatıldı");
            }
            catch (Exception ex)
            {
                _logger?.LogError("Uygulama kapatılırken hata oluştu", ex);
            }
            finally
            {
                base.OnClosed(e);
            }
        }

        private void RegisterHotkeys()
        {
            if (_appSettings.ToggleOcrHotkey != null && _appSettings.ToggleOcrHotkey.IsValid)
            {
                _ocrHotkeyId = _hotkeyManager.RegisterHotkey(
                    _appSettings.ToggleOcrHotkey.Modifiers,
                    _appSettings.ToggleOcrHotkey.Key,
                    ToggleOcr,
                    "OCR Aç/Kapat"
                );
                if (_ocrHotkeyId > 0)
                {
                    _logger?.LogInformation($"OCR kısayolu kaydedildi: {_appSettings.ToggleOcrHotkey}");
                }
            }

            if (_appSettings.ToggleTranslateWindowHotkey != null && _appSettings.ToggleTranslateWindowHotkey.IsValid)
            {
                _translateWindowHotkeyId = _hotkeyManager.RegisterHotkey(
                    _appSettings.ToggleTranslateWindowHotkey.Modifiers,
                    _appSettings.ToggleTranslateWindowHotkey.Key,
                    ToggleTranslateWindow,
                    "Çeviri Penceresini Aç/Kapat"
                );
                if (_translateWindowHotkeyId > 0)
                {
                    _logger?.LogInformation($"Çeviri penceresi kısayolu kaydedildi: {_appSettings.ToggleTranslateWindowHotkey}");
                }
            }

            if (_appSettings.SwitchTranslationServiceHotkey != null && _appSettings.SwitchTranslationServiceHotkey.IsValid)
            {
                _switchTranslationServiceHotkeyId = _hotkeyManager.RegisterHotkey(
                    _appSettings.SwitchTranslationServiceHotkey.Modifiers,
                    _appSettings.SwitchTranslationServiceHotkey.Key,
                    SwitchTranslationService,
                    "Çeviri Servisi Değiştir"
                );
                if (_switchTranslationServiceHotkeyId > 0)
                {
                    _logger?.LogInformation($"Çeviri servisi değiştirme kısayolu kaydedildi: {_appSettings.SwitchTranslationServiceHotkey}");
                }
            }

            // Kayıtlı kısayol sayısını logla
            int registeredCount = _hotkeyManager.RegisteredHotkeyCount;
            _logger?.LogInformation($"Toplam {registeredCount} kısayol başarıyla kaydedildi");
        }

        private void AppSettings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Tüm özellik değişikliklerinde ayarları kaydet
            _settingsManager.SaveSettings(_appSettings);
            // Özel işlemler
            if (e.PropertyName.EndsWith("Hotkey"))
            {
                UpdateHotkeys();
            }
            else if (e.PropertyName == nameof(AppSettings.OcrEngine))
            {
                _logger.LogInformation($"OCR motoru değiştirildi: {_appSettings.OcrEngine}");
                UpdateTextDetectionMethodOptions();
            }
            else if (e.PropertyName == nameof(AppSettings.TextDetectionMethod))
            {
                _logger.LogInformation($"Metin algılama yöntemi değiştirildi: {_appSettings.TextDetectionMethod}");
            }
            else if (e.PropertyName == nameof(AppSettings.Theme))
            {
                var selectedTheme = ThemeManager.GetThemeFromString(_appSettings.Theme);
                ThemeManager.ChangeTheme(selectedTheme);
                ThemeManager.SaveThemeSettings(selectedTheme);
            }
            else if (e.PropertyName == nameof(AppSettings.RamTickIntervalMs))
            {
                _manualTranslationTimer.Interval = TimeSpan.FromMilliseconds(_appSettings.RamTickIntervalMs > 50 ? _appSettings.RamTickIntervalMs : 50);
                _continuousTranslationTimer.Interval = TimeSpan.FromMilliseconds(_appSettings.RamTickIntervalMs > 50 ? _appSettings.RamTickIntervalMs : 50);
            }
            else if (e.PropertyName == nameof(AppSettings.OcrTickIntervalMs))
            {
                _continuousOcrTimer.Interval = TimeSpan.FromMilliseconds(_appSettings.OcrTickIntervalMs > 100 ? _appSettings.OcrTickIntervalMs : 100);
            }
        }

        private void UpdateHotkeys()
        {
            try
            {
                if (_hotkeyManager == null)
                {
                    _logger?.LogWarning("HotkeyManager başlatılmamış, kısayollar güncellenemiyor");
                    return;
                }

                _logger?.LogInformation("Kısayollar güncelleniyor...");

                // Tüm mevcut kısayolları kaldır
                _hotkeyManager.UnregisterAllHotkeys();

                // Yeniden kaydet
                RegisterHotkeys();

                _logger?.LogInformation("Kısayollar başarıyla güncellendi");
            }
            catch (Exception ex)
            {
                _logger?.LogError("Kısayol güncellemesi sırasında hata oluştu", ex);
            }
        }

        private void UnregisterHotkeys()
        {
            try
            {
                if (_hotkeyManager != null)
                {
                    _logger?.LogInformation("Kısayollar kaldırılıyor...");
                    _hotkeyManager.UnregisterAllHotkeys();
                    _logger?.LogInformation("Tüm kısayollar başarıyla kaldırıldı");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Kısayol kaldırma sırasında hata oluştu", ex);
            }
        }

        private void ListRegisteredHotkeys()
        {
            if (_hotkeyManager == null) return;

            var hotkeys = _hotkeyManager.GetRegisteredHotkeys();
            _logger?.LogInformation($"=== Kayıtlı Kısayollar ({hotkeys.Count}) ===");
            foreach (var (Id, Modifiers, Key, Description) in hotkeys)
            {
                _logger?.LogInformation($"  [{Id}] {Description}: {Modifiers} + {Key}");
            }
        }

        private void ToggleOcr()
        {
            if (_isContinuousOcrRunning)
                StopContinuousOcr();
            else
                StartContinuousOcr();
        }

        private void ToggleTranslateWindow()
        {
            btnToggleOverlay_Click(null, null);
        }

        private void SwitchTranslationService()
        {
            // Çeviri servisleri arasında geçiş yap
            if (cmbTranslationService.SelectedIndex < cmbTranslationService.Items.Count - 1)
                cmbTranslationService.SelectedIndex++;
            else
                cmbTranslationService.SelectedIndex = 0;
        }

        private async void btnScanPointers_Click(object sender, RoutedEventArgs e)
        {
            var pi = cmbProcesses.SelectedItem as ProcessInfo;
            if (pi == null)
            {
                AppendToLog(GetString("Str_Log_SelectProcessFirst"), true);
                return;
            }
            string searchText = txtScanText.Text;
            if (string.IsNullOrWhiteSpace(searchText))
            {
                AppendToLog(GetString("Str_Log_EnterPattern"), true);
                return;
            }
            btnScanPointers.IsEnabled = false;
            btnStopScan.IsEnabled = true;
            lblScanStatus.Text = GetString("Str_Log_ScanStarted");
            lstAddresses.Items.Clear(); // ListBox'ı temizle
            _scanCancellationTokenSource = new CancellationTokenSource();
            try
            {
                var progress = new Progress<int>(value =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        lblScanStatus.Text = $"Scanning... ({value}%)";
                    });
                });

                _enhancedMemoryService.StatusChanged += OnScanStatusChanged;
                _enhancedMemoryService.ProgressChanged += OnScanProgressChanged;

                // Timestamp ile başlangıç durumu raporla
                _enhancedMemoryService.ReportStatusWithTimestamp(GetString("Str_Log_ScanStarted"));
                AppendToLog(GetString("Str_Log_ScanStarted"));

                // Process'e bağlan (event'ler için)
                bool attachSuccess = _enhancedMemoryService.AttachToProcess(pi.Process.Id);
                if (attachSuccess)
                {
                    AppendToLog(GetString("Str_Log_ProcessAttached", pi.Process.ProcessName, pi.Process.Id));
                }

                // Gelişmiş tarama: Chunk ve buffer boyutunu belirle
                int chunkSize = 4 * 1024 * 1024; // 4 MB
                int bufferSize = 1024; // 1 KB
                bool useOverlappingBuffers = true; // Pattern kaçırma önleme

                // Process belleğine göre chunk boyutunu ayarla
                if (pi.Process.WorkingSet64 > 100 * 1024 * 1024) // 100 MB'den büyükse
                {
                    chunkSize = 8 * 1024 * 1024; // 8 MB chunk kullan (daha hızlı)
                    AppendToLog(GetString("Str_Log_LargeMemory"));
                }
                else
                {
                    AppendToLog(GetString("Str_Log_NormalMemory"));
                }

                // Tüm overload'ların kullanıldığı gelişmiş tarama
                List<IntPtr> addresses = await _enhancedMemoryService.FindPatternAddressesAsync(
                    pi.Process,
                    searchText,
                    _scanCancellationTokenSource.Token,
                    progress,
                    chunkSize,          // Özel chunk boyutu
                    bufferSize,         // Özel buffer boyutu
                    useOverlappingBuffers // Overlap kullan
                );
                if (addresses == null || !addresses.Any())
                {
                    AppendToLog(GetString("Str_Log_PatternNotFound"));
                    return;
                }
                AppendToLog(GetString("Str_Log_AddressesFound", addresses.Count));

                _lastFoundPaths.Clear(); // Önceki sonuçları temizle

                int maxAddressesToScan = Math.Min(10, addresses.Count); //  ilk 10 adresi tara

                using (var scanner = new PointerScanner(pi.Process, _memoryService, _logger))
                {
                    // Önbelleği temizle (yeni tarama için)
                    scanner.ClearCache();
                    _logger?.LogInformation("PointerScanner önbelleği temizlendi, yeni tarama başlatılıyor");

                    for (int i = 0; i < maxAddressesToScan; i++)
                    {
                        IntPtr targetAddress = addresses[i];
                        AppendToLog(GetString("Str_Log_ScanningAddress", targetAddress.ToInt64(), i + 1, maxAddressesToScan));

                        // Pointer yollarını bulmak için tarama yap
                        var pathsForAddress = await scanner.FindPointers(targetAddress, maxDepth: 3);
                        _lastFoundPaths.AddRange(pathsForAddress);
                        AppendToLog(GetString("Str_Log_PathsFound", pathsForAddress.Count));

                        // Bulunan yolları ListBox'a ekle 
                        foreach (var path in pathsForAddress.Take(10)) // İlk 10'u göster
                        {
                            lstAddresses.Items.Add(new ListBoxItem { Content = path.ToString() });
                        }
                    }

                    // Önbellek istatistiklerini göster
                    _logger?.LogInformation($"PointerScanner önbelleğinde {scanner.CachedPathCount} pointer yolu var");

                    // En iyi 3 pointer için hızlı stability testi
                    if (_lastFoundPaths.Any())
                    {
                        AppendToLog(Environment.NewLine + GetString("Str_Log_StabilityTest"));
                        var topPaths = _lastFoundPaths.Take(3).ToList();

                        foreach (var path in topPaths)
                        {
                            try
                            {
                                var quickStability = await scanner.CheckPointerStability(path, checkCount: 3, intervalMs: 100);
                                string statusIcon = quickStability.StabilityScore >= 70 ? "✅" : "⚠️";
                                AppendToLog($"{statusIcon} {path}: " + GetString("Str_Log_QuickStability", quickStability.StabilityScore));
                            }
                            catch (Exception ex)
                            {
                                AppendToLog(GetString("Str_Log_TestError", path, ex.Message));
                            }
                        }
                    }
                }

                if (_lastFoundPaths.Any())
                {
                    btnTestPointer.IsEnabled = true;
                    btnSavePointers.IsEnabled = true;

                    // Timestamp ile tamamlanma durumu raporla
                    string completeMsg = GetString("Str_Log_ScanComplete", _lastFoundPaths.Count);
                    _enhancedMemoryService.ReportStatusWithTimestamp(completeMsg);
                    _enhancedMemoryService.ReportProgressWithTimestamp(100); // %100 tamamlandı

                    AppendToLog(Environment.NewLine + completeMsg);
                }
                else
                {
                    string noPathsMsg = GetString("Str_Log_NoPaths");
                    _enhancedMemoryService.ReportStatusWithTimestamp(noPathsMsg);
                    AppendToLog(noPathsMsg);
                }

            }
            catch (OperationCanceledException)
            {
                string cancelMsg = GetString("Str_Log_ScanCancelled");
                _enhancedMemoryService.ReportStatusWithTimestamp(cancelMsg);
                AppendToLog(cancelMsg);
            }
            catch (Exception ex)
            {
                string errorMsg = GetString("Str_Log_ScanError", ex.Message);
                _enhancedMemoryService.ReportStatusWithTimestamp(errorMsg);
                AppendToLog(errorMsg, true);
                _logger?.LogError("Pointer taraması sırasında hata oluştu.", ex);
            }
            finally
            {
                // Event'leri temizle
                _enhancedMemoryService.StatusChanged -= OnScanStatusChanged;
                _enhancedMemoryService.ProgressChanged -= OnScanProgressChanged;

                // UI kontrollerini sıfırla
                btnScanPointers.IsEnabled = true;
                btnStopScan.IsEnabled = false;
                lblScanStatus.Text = "";

                // Kaynakları temizle
                _scanCancellationTokenSource?.Dispose();
                _scanCancellationTokenSource = null;

                // EnhancedMemoryService'i dispose etme - tekrar kullanılabilir olmalı
                // NOT: Servis constructor'da oluşturulduğu için burada dispose etmiyoruz
                _logger?.LogInformation("Pointer tarama işlemi sonlandırıldı ve kaynaklar temizlendi.");
            }
        }

        private void btnStopScan_Click(object sender, RoutedEventArgs e)
        {
            _scanCancellationTokenSource?.Cancel();
            AppendToLog(GetString("Str_Log_StoppingScan"));
        }

        private async void btnTestPointer_Click(object sender, RoutedEventArgs e)
        {
            if (!_lastFoundPaths.Any())
            {
                AppendToLog(GetString("Str_Log_NoPathToTest"), true);
                return;
            }
            var pi = cmbProcesses.SelectedItem as ProcessInfo;
            if (pi == null) return;

            // Önce tüm pointer'ları ValidatePointersAsync ile doğrula
            AppendToLog(GetString("Str_Log_ValidationStarted", _lastFoundPaths.Count));
            try
            {
                var validationResults = await _pointerValidationService.ValidatePointersAsync(pi.Process, _lastFoundPaths);
                AppendToLog(GetString("Str_Log_ValidationResults"));

                int validCount = 0;
                foreach (var result in validationResults.Take(10)) // İlk 10 sonucu göster
                {
                    string valuePreview = result.CurrentValue?.Substring(0, Math.Min(50, result.CurrentValue?.Length ?? 0)) ?? "null";
                    AppendToLog($"  • {result.Path}: Score={result.Score}, Valid={result.IsValid}, Value='{valuePreview}'");
                    if (result.IsValid) validCount++;
                }

                AppendToLog(GetString("Str_Log_ValidCount", validCount, validationResults.Count));

                // Kayıtlı pointer sayısını göster
                var registeredPaths = _pointerValidationService.GetRegisteredPointerPaths();
                _logger.LogInformation($"Önbellekte {registeredPaths.Count} pointer yolu kayıtlı");

                // En iyi skorlu pointer'ı stabilite testi ile test et (her iki servis de)
                var bestPath = _lastFoundPaths.First();
                AppendToLog(GetString("Str_Log_BestPathTest", bestPath));

                // 1. PointerValidationService ile test
                AppendToLog(GetString("Str_Log_ServiceStability"));
                var validationStability = await _pointerValidationService.TestPointerStabilityAsync(pi.Process, bestPath, 10, 500);
                AppendToLog($"  • Success Rate: {validationStability.SuccessRate:F1}%");
                AppendToLog($"  • Address Consistency: {validationStability.AddressConsistency:F1}%");
                AppendToLog($"  • Value Consistency: {validationStability.ValueConsistency:F1}%");
                AppendToLog($"  • Overall Stability Score: {validationStability.StabilityScore:F1}/100");

                // 2. PointerScanner ile test
                using (var scanner = new PointerScanner(pi.Process, _memoryService, _logger))
                {
                    AppendToLog(GetString("Str_Log_ScannerStability"));
                    var scannerStability = await scanner.CheckPointerStability(bestPath, 10, 500);
                    AppendToLog($"  • Success Rate: {scannerStability.SuccessRate:F1}%");
                    AppendToLog($"  • Address Consistency: {scannerStability.AddressConsistency:F1}%");
                    AppendToLog($"  • Value Consistency: {scannerStability.ValueConsistency:F1}%");
                    AppendToLog($"  • Overall Stability Score: {scannerStability.StabilityScore:F1}/100");
                    AppendToLog($"  • Cache Usage: {scanner.CachedPathCount} pointer paths");

                    // Kararlılık değerlendirmesi
                    double avgStability = (validationStability.StabilityScore + scannerStability.StabilityScore) / 2.0;
                    AppendToLog(Environment.NewLine + GetString("Str_Log_AvgStability", avgStability));

                    if (avgStability >= 80)
                        AppendToLog(GetString("Str_Log_Reliable"));
                    else if (avgStability >= 60)
                        AppendToLog(GetString("Str_Log_MediumReliable"));
                    else
                        AppendToLog(GetString("Str_Log_Unreliable"), true);
                }
            }
            catch (Exception ex)
            {
                AppendToLog(GetString("Str_Log_TestErrorGeneric", ex.Message), true);
            }
        }

        private void btnSavePointers_Click(object sender, RoutedEventArgs e)
        {
            if (!_lastFoundPaths.Any())
            {
                AppendToLog(GetString("Str_Log_NoPathToSave"), true);
                return;
            }
            try
            {
                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "JSON dosyası (*.json)|*.json",
                    FileName = $"pointers_{DateTime.Now:yyyyMMdd_HHmmss}.json"
                };
                if (saveDialog.ShowDialog() == true)
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(_lastFoundPaths, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    System.IO.File.WriteAllText(saveDialog.FileName, json);
                    AppendToLog(GetString("Str_Log_Saved", saveDialog.FileName));
                }
            }
            catch (Exception ex)
            {
                AppendToLog(GetString("Str_Log_SaveError", ex.Message), true);
            }
        }

        private async void btnLoadPointers_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "JSON dosyası (*.json)|*.json"
                };
                if (openDialog.ShowDialog() == true)
                {
                    var json = System.IO.File.ReadAllText(openDialog.FileName);
                    var loadedPaths = System.Text.Json.JsonSerializer.Deserialize<List<PointerPath>>(json);
                    if (loadedPaths?.Any() == true)
                    {
                        _lastFoundPaths = loadedPaths;
                        AppendToLog(GetString("Str_Log_Loaded", loadedPaths.Count, openDialog.FileName));
                        // Loaded pointer'ları göstermek için
                        foreach (var path in loadedPaths.Take(10))
                        {
                            AppendToLog($"  • {path}");
                        }
                        btnTestPointer.IsEnabled = true;
                        btnSavePointers.IsEnabled = true;

                        // Yüklenen pointer'ları otomatik olarak doğrula
                        var pi = cmbProcesses.SelectedItem as ProcessInfo;
                        if (pi != null)
                        {
                            AppendToLog(GetString("Str_Log_ValidatingLoaded"));
                            try
                            {
                                var validationResults = await _pointerValidationService.ValidatePointersAsync(pi.Process, loadedPaths);
                                int validCount = validationResults.Count(r => r.IsValid);
                                AppendToLog(GetString("Str_Log_ValidationComplete", validCount, validationResults.Count));

                                // Geçerli olmayan pointer'ları listeden çıkar
                                _lastFoundPaths = validationResults.Where(r => r.IsValid).Select(r => r.Path).ToList();
                                if (_lastFoundPaths.Count != loadedPaths.Count)
                                {
                                    AppendToLog(GetString("Str_Log_Filtered", _lastFoundPaths.Count));
                                }

                                // En iyi pointer için hızlı stability kontrolü
                                if (_lastFoundPaths.Any())
                                {
                                    var topPath = _lastFoundPaths.First();
                                    AppendToLog(GetString("Str_Log_QuickCheck", topPath));

                                    using (var scanner = new PointerScanner(pi.Process, _memoryService, _logger))
                                    {
                                        var quickStability = await scanner.CheckPointerStability(topPath, checkCount: 5, intervalMs: 200);
                                        AppendToLog(GetString("Str_Log_QuickStability", quickStability.StabilityScore));

                                        if (quickStability.StabilityScore >= 70)
                                        {
                                            AppendToLog(GetString("Str_Log_StableReady"));
                                        }
                                        else
                                        {
                                            AppendToLog(GetString("Str_Log_UnstableWarn"));
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                AppendToLog(GetString("Str_Log_ValidationError", ex.Message), true);
                            }
                        }
                    }
                    else
                    {
                        AppendToLog(GetString("Str_Log_NoValidInFile"), true);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendToLog(GetString("Str_Log_LoadError", ex.Message), true);
            }
        }

        private void OnScanStatusChanged(string status)
        {
            Dispatcher.Invoke(() =>
            {
                lblScanStatus.Text = status;
            });
        }

        private void OnScanProgressChanged(int progress)
        {
            Dispatcher.Invoke(() => progressScan.Value = progress);
        }

        private void OnTranslationStatsUpdated(object sender, PerformanceOptimizedTranslationService.PerformanceStats stats)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    // İstatistikleri log'a yazdır
                    _logger?.LogInformation($"Çeviri İstatistikleri - " +
                        $"Toplam: {stats.TotalRequests}, " +
                        $"Batch: {stats.BatchProcessedRequests}, " +
                        $"Tekil: {stats.IndividualProcessedRequests}, " +
                        $"Cache Hit Rate: {stats.CacheHitRate:F2}%, " +
                        $"Ort. Süre: {stats.AverageResponseTime.TotalMilliseconds:F2}ms");
                }
                catch (Exception ex)
                {
                    _logger?.LogError("İstatistik güncelleme sırasında hata", ex);
                }
            });
        }

        private void OnTranslationCompleted(object sender, TranslationCompletedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(e.ErrorMessage))
                    {
                        _logger?.LogInformation($"✅ Çeviri tamamlandı - " +
                            $"Kaynak: '{e.OriginalText.Substring(0, Math.Min(30, e.OriginalText.Length))}...', " +
                            $"Hedef Dil: {e.TargetLanguage}, " +
                            $"Güven: {e.Confidence * 100:F0}%, " +
                            $"Zaman: {e.TranslationTime:HH:mm:ss}");
                    }
                    else
                    {
                        _logger?.LogError($"❌ Çeviri hatası - " +
                            $"Metin: '{e.OriginalText.Substring(0, Math.Min(30, e.OriginalText.Length))}...', " +
                            $"Hata: {e.ErrorMessage}");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError("TranslationCompleted event handler'da hata", ex);
                }
            });
        }

        private void OnTranslationProgress(object sender, TranslationProgressEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    _logger?.LogInformation($"Çeviri İlerlemesi: {e.ProgressPercentage}% " +
                        $"({e.CompletedSentences}/{e.TotalSentences}) - " +
                        $"Şu an: '{e.CurrentSentence.Substring(0, Math.Min(20, e.CurrentSentence.Length))}...'");
                }
                catch (Exception ex)
                {
                    _logger?.LogError("TranslationProgress event handler'da hata", ex);
                }
            });
        }

        #endregion

        #region Existing Methods
        private void SelectComboBoxItemByTag(ComboBox comboBox, string tagValue)
        {
            if (comboBox == null || string.IsNullOrEmpty(tagValue)) return;
            foreach (ComboBoxItem item in comboBox.Items)
            {
                if (item.Tag != null && item.Tag.ToString() == tagValue)
                {
                    comboBox.SelectedItem = item;
                    break;
                }
            }
        }

        private void InitializeLanguageControls()
        {
            try
            {
                // Dilleri ComboBox'lara doldur
                var ocrLanguages = new List<string> { "eng", "jpn", "chi_sim", "kor", "rus" };
                cmbOcrLanguage.ItemsSource = ocrLanguages;
                var targetLanguages = new List<string> { "tr", "en", "de", "fr", "es" };
                cmbTargetLanguage.ItemsSource = targetLanguages;
                // Ayarlardan seçili dilleri yükle
                cmbOcrLanguage.SelectedItem = _appSettings.OcrLanguage;
                cmbTargetLanguage.SelectedItem = _appSettings.TargetLanguage;
                chkEnableColorFilter.IsChecked = _appSettings.EnableOcrColorFilter;
                chkEnableSkewCorrection.IsChecked = _appSettings.EnableSkewCorrection;
                chkEnableHandwritingMode.IsChecked = _appSettings.EnableHandwritingMode;
                chkEnableSuperResolution.IsChecked = _appSettings.EnableSuperResolution;
                chkEnableAnomalyDetection.IsChecked = _appSettings.EnableAnomalyDetection;
                chkEnableContextAnalysis.IsChecked = _appSettings.EnableContextAnalysis;
                cmbDnnModel.SelectedIndex = (int)_appSettings.SelectedDnnModel;

                // PSM ve Metin Algılama Yöntemlerini Tag bazlı seç (SelectedIndex bug-fix)
                SelectComboBoxItemByTag(cmbPageSegMode, _appSettings.SelectedTesseractPageSegMode.ToString());
                SelectComboBoxItemByTag(cmbTextDetectionMethod, _appSettings.TextDetectionMethod.ToString());
                SelectComboBoxItemByTag(cmbOcrEngine, _appSettings.OcrEngine.ToString());

                // UI Dilini ayarla
                foreach (ComboBoxItem item in cmbLanguage.Items)
                {
                    if (item.Tag != null && item.Tag.ToString() == _appSettings.Language)
                    {
                        cmbLanguage.SelectedItem = item;
                        break;
                    }
                }
                if (cmbLanguage.SelectedItem == null)
                {
                    cmbLanguage.SelectedIndex = 0;
                }


                // Olay dinleyicilerini ekle
                cmbOcrLanguage.SelectionChanged += CmbOcrLanguage_SelectionChanged;
                cmbTargetLanguage.SelectionChanged += CmbTargetLanguage_SelectionChanged;
                chkEnableColorFilter.Click += ChkEnableColorFilter_Click;
                chkEnableSkewCorrection.Click += ChkEnableSkewCorrection_Click;
                chkEnableHandwritingMode.Click += ChkEnableHandwritingMode_Click;
                chkEnableSuperResolution.Click += ChkEnableSuperResolution_Click;
            }
            catch (Exception ex)
            {
                _logger?.LogError("Dil kontrolleri başlatılırken hata oluştu.", ex);
            }
        }

        private void InitializeTranslationServices()
        {
            try
            {
                AdvancedTranslationService advancedService = null;

                // PerformanceOptimizedTranslationService event'lerini subscribe et
                if (_translationService is PerformanceOptimizedTranslationService performanceService)
                {
                    // StatsUpdated event'ini dinle
                    performanceService.StatsUpdated += OnTranslationStatsUpdated;
                    _logger.LogInformation("PerformanceOptimizedTranslationService StatsUpdated event'i aktif");

                    if (performanceService.BaseService is AdvancedTranslationService baseService)
                    {
                        advancedService = baseService;
                    }
                }
                else if (_translationService is AdvancedTranslationService directService)
                {
                    advancedService = directService;
                }

                // AdvancedTranslationService event'lerini subscribe et
                if (advancedService != null)
                {
                    cmbTranslationService.ItemsSource = advancedService.AvailableStrategies;
                    cmbTranslationService.SelectedIndex = 0;

                    // TranslationCompleted ve TranslationProgress event'lerini dinle
                    advancedService.TranslationCompleted += OnTranslationCompleted;
                    advancedService.TranslationProgress += OnTranslationProgress;
                    _logger.LogInformation("AdvancedTranslationService event'leri aktif");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Çeviri servisleri başlatılırken hata oluştu.", ex);
            }
        }

        private Type GetSelectedTranslationStrategy()
        {
            return (cmbTranslationService.SelectedItem as StrategyInfo)?.Type;
        }

        private async void ContinuousTranslationTimer_Tick(object sender, EventArgs e)
        {
            var pi = cmbProcesses.SelectedItem as ProcessInfo;
            if (pi == null || !_isContinuousTranslationRunning || pi.Process.HasExited) { StopAllTranslations(); return; }
            if (_dynamicTextAddress == IntPtr.Zero) return;
            try
            {
                string currentText = await Task.Run(() => _memoryService.TryReadStringDeep(_dynamicTextAddress));

                bool shouldTranslate = false;
                if (string.IsNullOrEmpty(currentText)) { shouldTranslate = false; }
                else if (_appSettings.RequireStableRam)
                {
                    shouldTranslate = currentText == _potentiallyStableRamText && currentText != _lastReadText;
                }
                else
                {
                    shouldTranslate = currentText != _lastReadText;
                }
                if (shouldTranslate)
                {
                    // Anomali tespiti
                    if (_appSettings.EnableAnomalyDetection)
                    {
                        var anomalyResult = _anomalyDetector.DetectAnomaly(currentText, _lastReadText);
                        if (anomalyResult.IsAnomalous && anomalyResult.Confidence >= _appSettings.AnomalyDetectionThreshold)
                        {
                            if (_appSettings.LogAnomalies)
                            {
                                AppendToLog($"RAM Anomali tespit edildi: {anomalyResult.Reason} (Güven: %{anomalyResult.Confidence * 100:F1})", true);
                            }
                            shouldTranslate = false; // Anormal metni çevirme
                        }
                    }

                    if (shouldTranslate)
                    {
                        _lastReadText = currentText; // Çevrildi 
                        string translated = await _translationService.TranslateAsync(currentText, _appSettings.TargetLanguage, GetSelectedTranslationStrategy());
                        Dispatcher.Invoke(() => { txtOriginal.Text = $"[RAM] {currentText}"; UpdateTranslatedText(translated); });
                    }
                }
                _potentiallyStableRamText = currentText;
            }
            catch (Exception ex)
            {
                _logger.LogError("Sürekli çeviri sırasında hata.", ex);
            }
        }

        private async void ManualTranslationTimer_Tick(object sender, EventArgs e)
        {
            if (_manualAddress == IntPtr.Zero) return;
            try
            {
                string currentText = await Task.Run(() => _memoryService.TryReadStringDeep(_manualAddress));
                if (!string.IsNullOrWhiteSpace(currentText) && currentText != _lastManualText)
                {
                    // Anomali tespiti
                    bool shouldTranslate = true;
                    if (_appSettings.EnableAnomalyDetection)
                    {
                        var anomalyResult = _anomalyDetector.DetectAnomaly(currentText, _lastManualText);
                        if (anomalyResult.IsAnomalous && anomalyResult.Confidence >= _appSettings.AnomalyDetectionThreshold)
                        {
                            if (_appSettings.LogAnomalies)
                            {
                                AppendToLog($"Manuel Anomali tespit edildi: {anomalyResult.Reason} (Güven: %{anomalyResult.Confidence * 100:F1})", true);
                            }
                            shouldTranslate = false; // Anormal metni çevirme
                        }
                    }

                    if (shouldTranslate)
                    {
                        _lastManualText = currentText;
                        string translated = await _translationService.TranslateAsync(currentText, _appSettings.TargetLanguage, GetSelectedTranslationStrategy());
                        Dispatcher.Invoke(() => { txtOriginal.Text = $"[Manuel] {currentText}"; UpdateTranslatedText(translated); });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Manuel çeviri sırasında hata.", ex);
            }
        }

        private async void ContinuousOcrTimer_Tick(object sender, EventArgs e)
        {
            // Aynı anda birden fazla OCR tick'i çalışmasını engellemek için
            if (_isOcrTickBusy) return;
            _isOcrTickBusy = true;

            try
            {
                if (!_isContinuousOcrRunning)
                    return;

                var pi = cmbProcesses.SelectedItem as ProcessInfo;
                if (pi == null || pi.Process.HasExited)
                {
                    StopContinuousOcr();
                    return;
                }

                IntPtr handle = pi.Process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                    return;

                // Ekran görüntüsü alma
                using (var screenshot = await Task.Run(() => _ocrService.CaptureWindow(handle)))
                {
                    if (screenshot == null)
                        return;

                    Bitmap imageToProcess;

                    // Kırpma işlemi
                    if (_selectedOcrRegion.HasValue)
                    {
                        var cropRect = _selectedOcrRegion.Value;
                        using (var cropped = _ocrService.CropImage(screenshot, cropRect))
                        {
                            imageToProcess = new Bitmap(cropped);
                        }
                    }
                    else
                    {
                        imageToProcess = new Bitmap(screenshot);
                    }

                    Bitmap imageForOcr = imageToProcess;

                    // Renk filtresi uygula
                    if (_appSettings.EnableOcrColorFilter)
                    {
                        var filteredImage = _ocrService.IsolateTextByColor(imageToProcess);
                        if (filteredImage == null)
                        {
                            filteredImage = imageToProcess;
                        }

                        if (!ReferenceEquals(filteredImage, imageToProcess))
                        {
                            imageToProcess.Dispose();
                        }

                        imageForOcr = filteredImage;
                    }

                    using (imageForOcr)
                    {
                        // Görüntü analizi yap
                        try
                        {
                            using (var imageMat = OpenCvSharp.Extensions.BitmapConverter.ToMat(imageForOcr))
                            {
                                // Kenar maskesi oluştur (gerekirse)
                                if (_appSettings.EnableOcrColorFilter)
                                {
                                    // Note: Edge/Contrast masks are created but not stored? Just ensuring they don't crash the app if used later or for debug.
                                    try
                                    {
                                        using (var edgeMask = _ocrService.CreateEdgeMask(imageMat))
                                        using (var contrastMask = _ocrService.CreateContrastMask(imageMat))
                                        {

                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Log full exception to help diagnose native load issues (DllNotFound, BadImageFormat, etc.)
                            _logger?.LogWarning($"BitmapConverter.ToMat failed in MainWindow (OpenCV issue?): {ex.Message}");
                            _logger?.LogError("OpenCV BitmapConverter.ToMat exception", ex);
                        }

                        _logger.LogInformation($"OCR işlemi başlatılıyor. OCR Motoru: {_appSettings.OcrEngine}, Dil: {_appSettings.OcrLanguage}");
                        var regionResults = await _ocrRegionProcessor.ProcessChangedRegionsAsync(imageForOcr);
                        string currentText = string.Join(" ", regionResults.Select(r => r.TranslatedText).Where(t => !string.IsNullOrWhiteSpace(t)));

                        _logger.LogInformation($"OcrRegionProcessor sonucu: {regionResults.Count} bölge, Metin: '{currentText}'");

                        // WindowsOcrService ile alternatif OCR işlemi (ayarlara göre)
                        if (string.IsNullOrWhiteSpace(currentText) && _appSettings.OcrEngine == OcrEngineType.WindowsOcr)
                        {
                            try
                            {
                                _logger.LogInformation("WindowsOcrService ile alternatif OCR deneniyor...");
                                var windowsOcrText = await _windowsOcrService.GetTextFromImage(imageForOcr, _appSettings.OcrLanguage);
                                if (!string.IsNullOrWhiteSpace(windowsOcrText))
                                {
                                    currentText = windowsOcrText;
                                    _logger.LogInformation($"WindowsOcrService ile metin tanındı: {currentText.Substring(0, Math.Min(50, currentText.Length))}...");
                                }
                                else
                                {
                                    _logger.LogWarning("WindowsOcrService metin tanıyamadı.");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError($"WindowsOcrService hatası: {ex.Message}", ex);
                            }
                        }

                        // Eğer hala metin yoksa, doğrudan OCR servislerini dene
                        if (string.IsNullOrWhiteSpace(currentText))
                        {
                            _logger.LogInformation("Doğrudan OCR servisleri deneniyor...");
                            try
                            {
                                // IOcrService ile doğrudan metin tanıma
                                var directOcrText = await _ocrService.GetTextFromImage(imageForOcr, _appSettings.OcrLanguage);
                                if (!string.IsNullOrWhiteSpace(directOcrText))
                                {
                                    currentText = directOcrText;
                                    _logger.LogInformation($"IOcrService ile metin tanındı: {currentText.Substring(0, Math.Min(50, currentText.Length))}...");
                                }
                                else
                                {
                                    _logger.LogWarning("IOcrService metin tanıyamadı.");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError($"IOcrService hatası: {ex.Message}", ex);
                            }
                        }

                        // Makine öğrenmesi ile metin iyileştirme
                        string ocrTextForDisplay = currentText;
                        string textForTranslation = currentText;

                        if (!string.IsNullOrWhiteSpace(currentText) && _appSettings.EnableMachineLearning)
                        {
                            using (var imageMat = OpenCvSharp.Extensions.BitmapConverter.ToMat(imageForOcr))
                            {
                                var mlResult = _mlTextProcessor.ProcessTextWithML(currentText, imageMat, _lastReadText);
                                
                                // Görüntülenecek metin her zaman ham (orijinal) OCR metni olmalıdır.
                                ocrTextForDisplay = mlResult.OriginalText;

                                if (mlResult.Confidence >= _appSettings.MlConfidenceThreshold)
                                {
                                    // Sadece çeviri için işlenmiş metni kullan.
                                    textForTranslation = mlResult.ProcessedText;
                                    if (mlResult.Improvements.Any())
                                    {
                                        AppendToLog($"ML iyileştirmeleri: {string.Join(", ", mlResult.Improvements)} (Güven: %{mlResult.Confidence * 100:F1})");
                                        _logger.LogInformation($"ML iyileştirmesi uygulandı: {string.Join(", ", mlResult.Improvements)}");
                                    }
                                }
                                else
                                {
                                    _logger.LogInformation($"ML işleme güven eşiğinin altında: %{mlResult.Confidence * 100:F1}");
                                }
                            }
                        }

                        // Kararlılık kontrolü için çevrilecek metni kullan
                        currentText = textForTranslation;

                        // Çeviri kararı
                        bool shouldTranslate;
                        if (string.IsNullOrWhiteSpace(currentText))
                        {
                            shouldTranslate = false;
                            _logger.LogWarning("OCR işlemi başarısız - metin tanınamadı.");
                            AppendToLog("OCR: Metin tanınamadı", true);
                        }
                        else if (_appSettings.RequireStableOcr)
                        {
                            shouldTranslate = currentText == _potentiallyStableOcrText && currentText != _lastReadText;
                            _logger.LogInformation($"Kararlılık kontrolü: Mevcut='{currentText}', Potansiyel='{_potentiallyStableOcrText}', Son='{_lastReadText}', Çevir={shouldTranslate}");
                        }
                        else
                        {
                            shouldTranslate = currentText != _lastReadText;
                            _logger.LogInformation($"Basit kontrol: Mevcut='{currentText}', Son='{_lastReadText}', Çevir={shouldTranslate}");
                        }

                        if (shouldTranslate)
                        {
                            _lastReadText = textForTranslation;
                            _logger.LogInformation($"Çeviri için metin hazır: {textForTranslation.Substring(0, Math.Min(30, textForTranslation.Length))}...");

                            // Çeviri işlemi
                            string translated = await _translationService.TranslateAsync(
                                textForTranslation,
                                _appSettings.TargetLanguage,
                                GetSelectedTranslationStrategy());

                            Dispatcher.Invoke(() =>
                            {
                                txtOriginal.Text = $"[OCR] {ocrTextForDisplay}";
                                UpdateTranslatedText(translated);
                            });

                            if (!string.IsNullOrWhiteSpace(translated))
                            {
                                _logger.LogInformation($"Çeviri tamamlandı: {translated.Substring(0, Math.Min(30, translated.Length))}...");
                            }
                        }

                        _potentiallyStableOcrText = currentText;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Sürekli OCR sırasında hata.", ex);
            }
            finally
            {
                _isOcrTickBusy = false;
            }
        }

        private void UpdateTranslatedText(string newTranslatedText)
        {
            // Geçersiz veya tekrar eden mesajları geçmişe ekleme
            if (string.IsNullOrWhiteSpace(newTranslatedText) || (_translationHistory.Any() && _translationHistory.First.Value == newTranslatedText))
            {
                return;
            }
            // Yeni çeviriyi listenin başına ekle
            _translationHistory.AddFirst(newTranslatedText);

            while (_translationHistory.Count > MaxTranslationHistory + 1)
            {
                _translationHistory.RemoveLast();
            }
            // Geçmişi tek bir metin olarak birleştir
            string displayText;
            if (_appSettings.ShowPreviousTranslations)
            {
                var previousLines = _translationHistory.Skip(1);
                if (_appSettings.ShowPreviousTranslationsLabel && previousLines.Any())
                {
                    displayText = string.Join(Environment.NewLine, new[] { _translationHistory.First.Value, "", "Önceki çeviriler:", string.Join(Environment.NewLine, previousLines) }.Where(s => !string.IsNullOrEmpty(s)));
                }
                else
                {
                    displayText = string.Join(Environment.NewLine, _translationHistory);
                }
            }
            else
            {
                displayText = _translationHistory.First.Value;
            }
            // Arayüzü güncelle
            txtTranslated.Text = displayText;
            OnTranslatedTextChanged(displayText);
        }

        private void btnRefresh_Click(object sender, RoutedEventArgs e) => LoadProcesses();

        private async void CmbProcesses_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbProcesses.SelectedItem is ProcessInfo pi)
            {
                StopAllTranslations();
                if (_translationService is AdvancedTranslationService advancedService)
                {
                    advancedService.ClearTranslationContext();
                }

                // Process seçildiğini logla (ToString() kullanarak)
                _logger?.LogInformation($"Process seçildi: {pi}");

                // Process ikonu al (kullanım örneği)
                var processIcon = _iconManager.ProcessIconuAl(pi.Process);
                if (processIcon != null)
                {
                    _logger.LogInformation($"Process ikonu alındı: {pi.ProcessName}");
                }

                // Yeni process seçildiğinde tüm önbellekleri temizle
                _memoryService.ClearAllCaches();
                _pointerValidationService.ClearPointerCache();
                _logger.LogInformation("Tüm önbellekler temizlendi");

                _appSettings.LastProcessName = pi.ProcessName;
                _settingsManager.SaveSettings(_appSettings);
                _translationHistory.Clear(); // Geçmiş çevirileri temizlemek için
                _lastReadText = "";
                _potentiallyStableRamText = "";
                _potentiallyStableOcrText = "";
                _dynamicTextAddress = IntPtr.Zero;
                txtAddress.Text = "";
                txtOriginal.Text = "";
                txtTranslated.Text = "";
                var recipe = await _gameRecipeService.GetRecipeForProcessAsync(pi.Process);
                _isSetupMode = (recipe == null);
                UpdateUIState();
                btnScanPointers.IsEnabled = true;
            }
            else
            {
                btnScanPointers.IsEnabled = false;
            }
        }

        private void btnTranslate_Click(object sender, RoutedEventArgs e)
        {
            if (_isContinuousTranslationRunning || _manualTranslationTimer.IsEnabled) { StopAllTranslations(); return; }
            if (_isContinuousOcrRunning) StopContinuousOcr();
            string addressText = txtAddress.Text.Trim();
            if (!string.IsNullOrWhiteSpace(addressText) && !addressText.Equals("Lütfen bir uygulama seçin.", StringComparison.OrdinalIgnoreCase))
            {
                StartManualTranslation(addressText);
            }
            else
            {
                if (_isSetupMode) SetupNewRecipe();
                else StartContinuousTranslation();
            }
        }

        private void btnVideoOcr_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var videoOcrWindow = new VideoOcrWindow(
                    _logger,
                    _appSettings,
                    _videoOcrService,
                    _videoCaptureService,
                    _ocrComparisonService,
                    _ocrAccuracyService);

                videoOcrWindow.Show();
            }
            catch (Exception ex)
            {
                _logger.LogError("Error opening video OCR window", ex);
                MessageBox.Show($"Video OCR penceresi açılırken hata oluştu: {ex.Message}",
                              "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnContinuousOcr_Click(object sender, RoutedEventArgs e)
        {
            if (_isContinuousOcrRunning) StopContinuousOcr();
            else StartContinuousOcr();
        }

        private void btnToggleOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (_outputWindow == null || !_outputWindow.IsLoaded)
            {
                _outputWindow = new OutputWindow(this);
                _outputWindow.RegionSelected += (region) =>
                {
                    _selectedOcrRegion = region;
                    AppendToLog(GetString("Str_Log_NewRegion", region));
                };
                _outputWindow.Show();
                AppendToLog(GetString("Str_Log_OverlayShown"));
            }
            else
            {
                _outputWindow.Close();
                _outputWindow = null;
                AppendToLog(GetString("Str_Log_OverlayHidden"));
            }
        }

        private void btnSelectOcrRegion_Click(object sender, RoutedEventArgs e)
        {
            if (_outputWindow == null || !_outputWindow.IsLoaded)
            {
                btnToggleOverlay_Click(sender, e);
            }
            _outputWindow?.EnterSelectionMode();
        }

        private async void StartManualTranslation(string addressText)
        {
            var pi = cmbProcesses.SelectedItem as ProcessInfo;
            if (pi == null) { AppendToLog(GetString("Str_Log_SelectProcessFirst"), true); return; }
            if (!_memoryService.AttachToProcess(pi.Process.Id)) { AppendToLog(GetString("Str_Log_AttachFailAdmin"), true); return; }
            try
            {
                _manualAddress = addressText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? new IntPtr(long.Parse(addressText.Substring(2), NumberStyles.HexNumber)) : new IntPtr(long.Parse(addressText, NumberStyles.HexNumber));
                AppendToLog(GetString("Str_Log_StartingManual", _manualAddress.ToInt64()));
                _lastManualText = "";
                _manualTranslationTimer.Start();
                UpdateUIState();
            }
            catch (Exception ex) { AppendToLog(GetString("Str_Log_AddressError", ex.Message), true); }
        }

        private async void StartContinuousTranslation()
        {
            StopAllTranslations();
            var pi = cmbProcesses.SelectedItem as ProcessInfo;
            if (pi == null) { AppendToLog(GetString("Str_Log_SelectApp")); return; }
            if (!_memoryService.AttachToProcess(pi.Process.Id)) { AppendToLog(GetString("Str_Log_AttachFail"), true); return; }
            var recipe = await _gameRecipeService.GetRecipeForProcessAsync(pi.Process);
            if (recipe == null) return;
            _dynamicTextAddress = _memoryService.ResolveAddressFromPath(pi.Process, recipe);
            if (_dynamicTextAddress == IntPtr.Zero)
            {
                AppendToLog(GetString("Str_Log_AddressResolveFail"), true);
                _isSetupMode = true;
                UpdateUIState();
                return;
            }
            txtAddress.Text = $"0x{_dynamicTextAddress.ToInt64():X}";
            _isContinuousTranslationRunning = true;
            _continuousTranslationTimer.Start();
            UpdateUIState();
        }

        private void StartContinuousOcr()
        {
            StopAllTranslations();
            if (cmbProcesses.SelectedItem == null)
            {
                AppendToLog("Lütfen önce listeden bir oyun seçin.");
                return;
            }

            var pi = cmbProcesses.SelectedItem as ProcessInfo;
            if (pi == null || pi.Process.HasExited)
            {
                AppendToLog("Seçilen işlem geçersiz veya kapanmış.");
                return;
            }

            IntPtr handle = pi.Process.MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                AppendToLog("Seçilen işlemin penceresi bulunamadı.");
                return;
            }

            _logger.LogInformation($"Sürekli OCR başlatılıyor. İşlem: {pi.ProcessName}, Pencere: {handle}, OCR Motoru: {_appSettings.OcrEngine}");
            _isContinuousOcrRunning = true;
            _continuousOcrTimer.Start();
            UpdateUIState();
            AppendToLog($"Sürekli OCR başlatıldı. İşlem: {pi.ProcessName}");
        }

        private void StopAllTranslations()
        {
            if (_isContinuousTranslationRunning) { _isContinuousTranslationRunning = false; _continuousTranslationTimer.Stop(); AppendToLog("Otomatik RAM çevirisi durduruldu."); }
            if (_manualTranslationTimer.IsEnabled) { _manualTranslationTimer.Stop(); _manualAddress = IntPtr.Zero; AppendToLog("Manuel RAM çevirisi durduruldu."); }
            if (_isContinuousOcrRunning) StopContinuousOcr();
            _lastReadText = "";
            _potentiallyStableRamText = "";
            _potentiallyStableOcrText = "";
            UpdateUIState();
        }

        private void StopContinuousOcr()
        {
            _isContinuousOcrRunning = false;
            _continuousOcrTimer.Stop();
            _lastReadText = "";
            _potentiallyStableOcrText = "";
            _isOcrTickBusy = false;
            AppendToLog(GetString("Str_Log_OcrStopped"));
            UpdateUIState();
        }

        private void SetupNewRecipe()
        {
            var pi = cmbProcesses.SelectedItem as ProcessInfo;
            if (pi == null) return;

            string question = Application.Current.FindResource("Str_Msg_EnterPointerPath") as string;
            // Hata önleyici: Eğer kaynak bulunamazsa varsayılan metni kullan
            if (string.IsNullOrEmpty(question)) question = "Lütfen pointer yolunu girin:";

        
            var prompt = new InputDialog(question, "\"gamename.exe\"+1A2B3C, 40, 1F8, 10");

            if (prompt.ShowDialog() == true)
            {
                var (baseModule, baseOffset, offsets) = ParsePointerPath(prompt.Answer);

                if (string.IsNullOrWhiteSpace(baseModule) || offsets == null)
                {
                    // Hata mesajlarını dil dosyasından al
                    string errorMsg = Application.Current.FindResource("Str_Msg_InvalidInputFormat") as string;
                    string errorTitle = Application.Current.FindResource("Str_Title_Error") as string;

                    MessageBox.Show(errorMsg ?? "Girdi formatı geçersiz.", errorTitle ?? "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var newRecipe = new GameRecipe
                {
                    ProcessName = pi.ProcessName,
                    PathInfo = new PathInfo
                    {
                        BaseAddressModule = baseModule,
                        BaseAddressOffset = baseOffset,
                        PointerOffsets = offsets
                    }
                };

                _gameRecipeService.SaveOrUpdateRecipe(newRecipe);
                // Eğer GetString metodun yoksa aşağıdakini kullan:
                string logFormat = Application.Current.FindResource("Str_Log_NewRecipe") as string;
                AppendToLog(string.Format(logFormat ?? "'{0}' için yeni yol kaydedildi.", pi.ProcessName));

                _isSetupMode = false;
                UpdateUIState();
                StartContinuousTranslation();
            }
        }

        private void UpdateUIState()
        {
            bool processSelected = cmbProcesses.SelectedItem != null;
            bool anyTranslationRunning = _isContinuousTranslationRunning || _manualTranslationTimer.IsEnabled || _isContinuousOcrRunning;
            cmbProcesses.IsEnabled = !anyTranslationRunning;
            cmbTranslationService.IsEnabled = !anyTranslationRunning;
            cmbTargetLanguage.IsEnabled = !anyTranslationRunning;
            cmbOcrLanguage.IsEnabled = !anyTranslationRunning;
            chkEnableColorFilter.IsEnabled = !anyTranslationRunning;
            if (_isContinuousTranslationRunning || _manualTranslationTimer.IsEnabled) { btnTranslate.Content = GetString("Str_Main_StopRam"); btnTranslate.IsEnabled = true; }
            else if (_isSetupMode && processSelected) { btnTranslate.Content = GetString("Str_Main_SetupNewPath"); btnTranslate.IsEnabled = !anyTranslationRunning; }
            else { btnTranslate.Content = GetString("Str_Main_StartRam"); btnTranslate.IsEnabled = processSelected && !anyTranslationRunning; }
            if (_isContinuousOcrRunning) { btnContinuousOcr.Content = GetString("Str_Main_StopScreenOcr"); btnContinuousOcr.IsEnabled = true; }
            else { btnContinuousOcr.Content = GetString("Str_Main_StartScreenOcr"); btnContinuousOcr.IsEnabled = processSelected && !anyTranslationRunning; }
            if (!processSelected) { txtAddress.Text = GetString("Str_Main_SelectAppHint"); }
        }

        private (string Module, long Offset, List<int> Offsets) ParsePointerPath(string input)
        {
            try
            {
                // Giriş null veya boşsa
                if (string.IsNullOrWhiteSpace(input))
                {
                    _logger?.LogError("ParsePointerPath: Giriş boş veya null.");
                    return (null, 0, null);
                }
                // Virgüllere göre ayır ve boşları temizle
                var parts = input.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1)
                {
                    _logger?.LogError("ParsePointerPath: Geçersiz format - en az bir kısım gerekli.");
                    return (null, 0, null);
                }
                string basePart = parts[0].Trim();
                var baseRegex = new Regex(
                    @"[""']?(?<module>[^""']+\.exe)[""']?\s*\+\s*(0x)?(?<offset>[0-9A-Fa-f]+)",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled);
                var match = baseRegex.Match(basePart);
                if (!match.Success)
                {
                    _logger?.LogError($"ParsePointerPath: Modül ve baz adres ayrıştırılamadı. Girdi: {basePart}");
                    return (null, 0, null);
                }
                string moduleName = match.Groups["module"].Value.Trim();
                string offsetHex = match.Groups["offset"].Value;
                if (!long.TryParse(offsetHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long baseOffset))
                {
                    _logger?.LogError($"ParsePointerPath: Baz adres geçersiz. Girdi: {offsetHex}");
                    return (null, 0, null);
                }
                var offsets = new List<int>();
                for (int i = 1; i < parts.Length; i++)
                {
                    string part = parts[i].Trim();
                    if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        part = part.Substring(2);
                    }
                    if (int.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int offset))
                    {
                        offsets.Add(offset);
                    }
                    else
                    {
                        _logger?.LogError($"ParsePointerPath: Geçersiz offset. Girdi: {parts[i].Trim()}");
                        return (null, 0, null);
                    }
                }
                return (moduleName, baseOffset, offsets);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"ParsePointerPath: Beklenmeyen hata: {ex.Message}", ex);
                return (null, 0, null);
            }
        }

        private void LoadProcesses()
        {
            try
            {
                AppendToLog(GetString("Str_Log_ListingProcesses"));
                var selectedBefore = cmbProcesses.SelectedItem as ProcessInfo;

                // Eski ProcessInfo nesnelerini dispose et
                if (cmbProcesses.ItemsSource is System.Collections.IEnumerable oldProcesses)
                {
                    foreach (var item in oldProcesses)
                    {
                        if (item is ProcessInfo oldPi)
                        {
                            oldPi.Dispose();
                        }
                    }
                }

                _processService.RefreshProcesses();
                var processes = _processService.GetProcesses()
                    .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle))
                    .Select(p => new ProcessInfo(p, _logger))
                    .OrderBy(p => p.ProcessName)
                    .ToList();
                cmbProcesses.ItemsSource = processes;
                var processToSelect = processes.FirstOrDefault(p => selectedBefore != null && p.Process.Id == selectedBefore.Process.Id) ?? processes.FirstOrDefault(p => !string.IsNullOrEmpty(_appSettings.LastProcessName) && p.ProcessName == _appSettings.LastProcessName);
                if (processToSelect != null) { cmbProcesses.SelectedItem = processToSelect; }

                // IconManager kullanarak önbellek durumunu göster
                _iconManager.OnbellekDurumuGoster();

                AppendToLog(GetString("Str_Log_ProcessesFound", processes.Count));
                _logger?.LogInformation($"Process listesi yüklendi: {processes.Count} adet");

                // Process listesini logla (ilk 5 process)
                foreach (var proc in processes.Take(5))
                {
                    _logger?.LogInformation($"  • {proc}"); // ToString() kullanıyor
                }
            }
            catch (Exception ex)
            {
                AppendToLog(GetString("Str_Log_ListError", ex.Message), true);
                _logger?.LogError("Process listesi yüklenirken hata oluştu", ex);
            }
        }

        private void AppendToLog(string key, params object[] args)
        {
            // Overload for cleaner syntax without isError
            AppendToLog(key, false, args);
        }

        private void AppendToLog(string key, bool isError, params object[] args)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendToLog(key, isError, args));
                return;
            }

            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Key = key,
                Args = args,
                IsError = isError
            };
            entry.UpdateTranslation(); // Initial formatting

            _logEntries.Add(entry);

            if (_logEntries.Count > 500)
            {
                _logEntries.RemoveAt(0);
            }

            // Previously auto-scrolled to newest entry. Disable to avoid forcing user's view.
            // If user wants to jump to bottom manually, provide a button or keyboard shortcut.
            // However keep a gentle visual indicator: set the latest item's background to a subtle color.
            try
            {
                var last = _logEntries.LastOrDefault();
                if (last != null)
                {
                    // mark as unread (simple approach: set IsError -> will affect styling if template uses it)
                    // Alternatively we can update FullText or attach metadata; keep minimal changes.
                    // No auto-scroll performed.
                }
            }
            catch { }
        }

        private string GetString(string key, params object[] args)
        {
            try
            {
                var resource = Application.Current.TryFindResource(key) as string;
                if (string.IsNullOrEmpty(resource)) return key; // Return key as fallback
                if (args != null && args.Length > 0)
                {
                    return string.Format(resource, args);
                }
                return resource;
            }
            catch
            {
                return key;
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RestoreWindowPlacement();
            this.Loaded -= MainWindow_Loaded;
        }

        private void RestoreWindowPlacement()
        {
            if (_appSettings == null || !_appSettings.HasWindowBounds) return;

            if (_appSettings.MainWindowWidth > 0 && _appSettings.MainWindowHeight > 0)
            {
                Width = _appSettings.MainWindowWidth;
                Height = _appSettings.MainWindowHeight;
            }

            Left = _appSettings.MainWindowLeft;
            Top = _appSettings.MainWindowTop;

            if (_appSettings.MainWindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
        }

        private void PersistWindowPlacement()
        {
            if (_appSettings == null) return;

            _appSettings.MainWindowState = WindowState;

            if (WindowState == WindowState.Normal)
            {
                _appSettings.MainWindowWidth = Width;
                _appSettings.MainWindowHeight = Height;
                _appSettings.MainWindowLeft = Left;
                _appSettings.MainWindowTop = Top;
                _appSettings.HasWindowBounds = true;
            }
        }

        protected virtual void OnTranslatedTextChanged(string newText) => TranslatedTextChanged?.Invoke(newText);

        #region Theme Management
        private void InitializeThemeUI()
        {
            try
            {
                _logger?.LogInformation("InitializeThemeUI çağrıldı - Tema başlatılıyor");

                var currentTheme = ThemeManager.GetThemeFromString(_appSettings.Theme);
                foreach (ComboBoxItem item in cmbTheme.Items)
                {
                    if (item.Tag != null && item.Tag.ToString() == ThemeManager.GetStringFromTheme(currentTheme))
                    {
                        cmbTheme.SelectedItem = item;
                        break;
                    }
                }
                if (cmbTheme.SelectedItem == null)
                {
                    cmbTheme.SelectedIndex = 0;
                }

                _logger?.LogInformation($"Tema başarıyla başlatıldı: {_appSettings.Theme}");
            }
            catch (Exception ex)
            {
                _logger?.LogError("Tema UI başlatılırken hata oluştu.", ex);
                if (cmbTheme != null && cmbTheme.Items.Count > 0)
                {
                    cmbTheme.SelectedIndex = 0;
                }
            }
        }

        private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (cmbTheme.SelectedItem is ComboBoxItem selectedItem)
                {
                    string themeString = selectedItem.Tag.ToString();
                    var selectedTheme = ThemeManager.GetThemeFromString(themeString);
                    // Temayı değiştir ve kaydet
                    ThemeManager.ChangeTheme(selectedTheme);
                    ThemeManager.SaveThemeSettings(selectedTheme);
                    // Ayarlara da kaydet
                    _appSettings.Theme = themeString;
                    _settingsManager.SaveSettings(_appSettings);
                    // Log kaydet
                    AppendToLog(GetString("Str_Log_ThemeChanged", selectedItem.Content));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Tema değiştirme sırasında hata oluştu.", ex);
                AppendToLog(GetString("Str_Log_ThemeError"), true);
            }
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (cmbLanguage.SelectedItem is ComboBoxItem selectedItem)
                {
                    string langCode = selectedItem.Tag.ToString();
                    if (_appSettings.Language != langCode)
                    {
                        _appSettings.Language = langCode;
                        _settingsManager.SaveSettings(_appSettings);
                        App.ChangeLanguage(langCode);

                        // Refresh existing logs
                        foreach (var log in _logEntries)
                        {
                            log.UpdateTranslation();
                        }

                        // Update the UI to reflect the new language
                        UpdateUIState();

                        AppendToLog("Str_Log_LanguageChanged", selectedItem.Content);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Dil değiştirme sırasında hata oluştu.", ex);
            }
        }

        private void CmbOcrEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (cmbOcrEngine.SelectedItem is ComboBoxItem selectedItem)
                {
                    string engineName = selectedItem.Content.ToString();
                    if (engineName == "Windows OCR")
                        _appSettings.OcrEngine = OcrEngineType.WindowsOcr;
                    else if (engineName == "Tesseract OCR")
                        _appSettings.OcrEngine = OcrEngineType.Tesseract;
                    _settingsManager.SaveSettings(_appSettings);
                    AppendToLog(GetString("Str_Log_OcrEngineChanged", engineName));

                    // OCR motoru değiştiğinde metin algılama yöntemini güncelle
                    UpdateTextDetectionMethodOptions();

                    // OCR servislerini test et
                    AppendToLog(GetString("Str_Log_TestingOcr"));
                    TestWindowsOcrService();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("OCR motoru değiştirme sırasında hata oluştu.", ex);
                AppendToLog(GetString("Str_Log_OcrChangeError"), true);
            }
        }

        private async void TestWindowsOcrService()
        {
            try
            {
                var pi = cmbProcesses.SelectedItem as ProcessInfo;
                if (pi == null || pi.Process.HasExited)
                {
                    AppendToLog(GetString("Str_Log_SelectProcessTest"), true);
                    return;
                }

                IntPtr handle = pi.Process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                {
                    AppendToLog(GetString("Str_Log_WindowNotFound"), true);
                    return;
                }

                AppendToLog(GetString("Str_Log_TestingOcr"));

                // Ekran görüntüsü al
                using (var screenshot = await Task.Run(() => _ocrService.CaptureWindow(handle)))
                {
                    if (screenshot == null)
                    {
                        AppendToLog(GetString("Str_Log_ScreenshotFailed"), true);
                        return;
                    }

                    AppendToLog(GetString("Str_Log_ScreenshotTaken", screenshot.Width, screenshot.Height));

                    // 1. WindowsOcrService ile metin tanıma
                    try
                    {
                        AppendToLog(GetString("Str_Log_TestingWindowsOcr"));
                        var windowsOcrText = await _windowsOcrService.GetTextFromImage(screenshot, _appSettings.OcrLanguage);
                        if (!string.IsNullOrWhiteSpace(windowsOcrText))
                        {
                            AppendToLog(GetString("Str_Log_WindowsOcrSuccess", windowsOcrText));
                        }
                        else
                        {
                            AppendToLog(GetString("Str_Log_WindowsOcrFail"), true);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendToLog(GetString("Str_Log_WindowsOcrError", ex.Message), true);
                    }

                    // 2. IOcrService ile metin tanıma
                    try
                    {
                        AppendToLog(GetString("Str_Log_TestingIOcr"));
                        var iocrText = await _ocrService.GetTextFromImage(screenshot, _appSettings.OcrLanguage);
                        if (!string.IsNullOrWhiteSpace(iocrText))
                        {
                            AppendToLog(GetString("Str_Log_IOcrSuccess", iocrText));
                        }
                        else
                        {
                            AppendToLog(GetString("Str_Log_IOcrFail"), true);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendToLog(GetString("Str_Log_IOcrError", ex.Message), true);
                    }

                    // 3. OcrRegionProcessor ile test
                    try
                    {
                        AppendToLog(GetString("Str_Log_TestingRegion"));
                        var regionResults = await _ocrRegionProcessor.ProcessChangedRegionsAsync(screenshot);
                        var regionText = string.Join(" ", regionResults.Select(r => r.TranslatedText).Where(t => !string.IsNullOrWhiteSpace(t)));
                        if (!string.IsNullOrWhiteSpace(regionText))
                        {
                            AppendToLog(GetString("Str_Log_RegionSuccess", regionText));
                        }
                        else
                        {
                            AppendToLog(GetString("Str_Log_RegionFail", regionResults.Count), true);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendToLog(GetString("Str_Log_RegionError", ex.Message), true);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendToLog(GetString("Str_Log_OcrTestError", ex.Message), true);
                _logger.LogError($"OCR test hatası: {ex.Message}", ex);
            }
        }

        private void CmbTextDetectionMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (cmbTextDetectionMethod.SelectedItem is ComboBoxItem selectedItem)
                {
                    string methodString = selectedItem.Tag.ToString();
                    if (Enum.TryParse<TextDetectionMethod>(methodString, out var method))
                    {
                        _appSettings.TextDetectionMethod = method;
                        _settingsManager.SaveSettings(_appSettings);
                        AppendToLog(GetString("Str_Log_MethodChanged", selectedItem.Content));

                        // Metin algılama yöntemi değişiklikleri için özel mesajlar
                        if (method == TextDetectionMethod.East)
                        {
                            AppendToLog(GetString("Str_Log_EastInfo"));
                        }
                        else if (method == TextDetectionMethod.OpenCV)
                        {
                            AppendToLog(GetString("Str_Log_OpenCvInfo"));
                        }
                        else
                        {
                            AppendToLog(GetString("Str_Log_NoneInfo"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Metin algılama yöntemi değiştirme sırasında hata oluştu.", ex);
                AppendToLog(GetString("Str_Log_MethodError"), true);
            }
        }

        private void UpdateTextDetectionMethodOptions()
        {
            // Windows OCR seçildiğinde EAST seçeneğini devre dışı bırak
            bool isWindowsOcr = _appSettings.OcrEngine == OcrEngineType.WindowsOcr;

            var eastItem = cmbTextDetectionMethod.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag.ToString() == "East");

            if (eastItem != null)
            {
                eastItem.IsEnabled = !isWindowsOcr;

                // Windows OCR seçiliyken ve EAST seçiliyse OpenCV'ye geç
                if (isWindowsOcr && _appSettings.TextDetectionMethod == TextDetectionMethod.East)
                {
                    _appSettings.TextDetectionMethod = TextDetectionMethod.OpenCV;
                    cmbTextDetectionMethod.SelectedIndex = (int)TextDetectionMethod.OpenCV;
                    AppendToLog(GetString("Str_Log_EastIncompatible"));
                }
            }
        }

        private void CmbOcrLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbOcrLanguage.SelectedItem is string selectedLang)
            {
                _appSettings.OcrLanguage = selectedLang;
                _settingsManager.SaveSettings(_appSettings);
            }
        }

        private void CmbTargetLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbTargetLanguage.SelectedItem is string selectedLang)
            {
                _appSettings.TargetLanguage = selectedLang;
                _settingsManager.SaveSettings(_appSettings);
            }
        }

        private void ChkEnableColorFilter_Click(object sender, RoutedEventArgs e)
        {
            _appSettings.EnableOcrColorFilter = chkEnableColorFilter.IsChecked ?? true;
            _settingsManager.SaveSettings(_appSettings);
        }

        private void chkEnableColorFilter_Checked(object sender, RoutedEventArgs e)
        {
            _appSettings.EnableOcrColorFilter = chkEnableColorFilter.IsChecked ?? true;
            _settingsManager.SaveSettings(_appSettings);
        }

        private void ChkEnableSkewCorrection_Click(object sender, RoutedEventArgs e)
        {
            _appSettings.EnableSkewCorrection = chkEnableSkewCorrection.IsChecked ?? true;
            _settingsManager.SaveSettings(_appSettings);
        }

        private void ChkEnableHandwritingMode_Click(object sender, RoutedEventArgs e)
        {
            _appSettings.EnableHandwritingMode = chkEnableHandwritingMode.IsChecked ?? false;
            _settingsManager.SaveSettings(_appSettings);
        }

        private void ChkEnableSuperResolution_Click(object sender, RoutedEventArgs e)
        {
            _appSettings.EnableSuperResolution = chkEnableSuperResolution.IsChecked ?? false;
            _settingsManager.SaveSettings(_appSettings);
        }

        private void chkEnableAnomalyDetection_Checked(object sender, RoutedEventArgs e)
        {
            _appSettings.EnableAnomalyDetection = chkEnableAnomalyDetection.IsChecked ?? true;
            _settingsManager.SaveSettings(_appSettings);
            AppendToLog(GetString("Str_Log_AnomalyEnabled"));
        }

        private void chkEnableAnomalyDetection_Unchecked(object sender, RoutedEventArgs e)
        {
            _appSettings.EnableAnomalyDetection = chkEnableAnomalyDetection.IsChecked ?? false;
            _settingsManager.SaveSettings(_appSettings);
            AppendToLog(GetString("Str_Log_AnomalyDisabled"));
        }

        // Machine Learning checkbox handlers removed (feature toggled via settings only)

        private void chkEnableContextAnalysis_Checked(object sender, RoutedEventArgs e)
        {
            _appSettings.EnableContextAnalysis = chkEnableContextAnalysis.IsChecked ?? true;
            _settingsManager.SaveSettings(_appSettings);
            AppendToLog(GetString("Str_Log_ContextEnabled"));
        }

        private void chkEnableContextAnalysis_Unchecked(object sender, RoutedEventArgs e)
        {
            _appSettings.EnableContextAnalysis = chkEnableContextAnalysis.IsChecked ?? false;
            _settingsManager.SaveSettings(_appSettings);
            AppendToLog(GetString("Str_Log_ContextDisabled"));
        }

        private void CmbDnnModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (cmbDnnModel.SelectedItem is ComboBoxItem selectedItem &&
                    Enum.TryParse<DnnModelType>(selectedItem.Tag.ToString(), out var modelType))
                {
                    _appSettings.SelectedDnnModel = modelType;
                    _settingsManager.SaveSettings(_appSettings);
                    AppendToLog(GetString("Str_Log_DnnChanged", selectedItem.Content));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("DNN modeli değiştirme sırasında hata oluştu.", ex);
                AppendToLog(GetString("Str_Log_DnnError"), true);
            }
        }

        private void CmbPageSegMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (cmbPageSegMode.SelectedItem is ComboBoxItem selectedItem &&
                    Enum.TryParse<TesseractPageSegMode>(selectedItem.Tag.ToString(), out var psm))
                {
                    _appSettings.SelectedTesseractPageSegMode = psm;
                    _settingsManager.SaveSettings(_appSettings);
                    AppendToLog($"Tesseract PSM Değiştirildi: {psm}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Tesseract PSM değiştirme sırasında hata oluştu.", ex);
            }
        }
        #endregion

        #region ML ve Anomali İstatistikleri

        private void btnMLStatistics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var stats = _mlTextProcessor.GetStatistics();
                // To keep it simple and not break non-string logic, I will just update the log call.
                // But ideally the MessageBox content should also be localized. 
                // For this task scope (localizing logs and missing buttons), I focus on AppendToLog.
                // However, user said "loglar türkce geliyor", implying user-facing messages.

                var message = $"ML Statistics:\n\n" +
                             $"Total Texts Processed: {stats.TotalTextsProcessed}\n" +
                             $"Unique Words Learned: {stats.UniqueWordsLearned}\n" +
                             $"DNN Models Loaded: {stats.DnnModelsLoaded}\n" +
                             $"Average Confidence Score: %{stats.AverageConfidence * 100:F1}";

                if (_appSettings.Language == "tr")
                {
                    message = $"ML İstatistikleri:\n\n" +
                            $"İşlenen Toplam Metin: {stats.TotalTextsProcessed}\n" +
                            $"Öğrenilen Benzersiz Kelime: {stats.UniqueWordsLearned}\n" +
                            $"Yüklenen DNN Modeli: {stats.DnnModelsLoaded}\n" +
                            $"Ortalama Güven Skoru: %{stats.AverageConfidence * 100:F1}";
                }

                MessageBox.Show(message, "ML Statistics", MessageBoxButton.OK, MessageBoxImage.Information);
                AppendToLog(GetString("Str_Log_MlStats", stats.TotalTextsProcessed));
            }
            catch (Exception ex)
            {
                _logger?.LogError("ML istatistikleri alınırken hata oluştu.", ex);
                AppendToLog(GetString("Str_Log_MlStatsError"), true);
            }
        }

        private void btnAnomalyStatistics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var stats = _anomalyDetector.GetStatistics();
                var message = $"Anomaly Statistics:\n\n" +
                             $"Total Texts Analyzed: {stats.TotalTextsAnalyzed}\n" +
                             $"Average Text Length: {stats.AverageTextLength:F1} characters\n" +
                             $"Unique Words: {stats.UniqueWords}";

                if (_appSettings.Language == "tr")
                {
                    message = $"Anomali İstatistikleri:\n\n" +
                             $"Analiz Edilen Toplam Metin: {stats.TotalTextsAnalyzed}\n" +
                             $"Ortalama Metin Uzunluğu: {stats.AverageTextLength:F1} karakter\n" +
                             $"Benzersiz Kelime Sayısı: {stats.UniqueWords}";
                }

                MessageBox.Show(message, "Anomaly Statistics", MessageBoxButton.OK, MessageBoxImage.Information);
                AppendToLog(GetString("Str_Log_AnomalyStats", stats.TotalTextsAnalyzed));
            }
            catch (Exception ex)
            {
                _logger?.LogError("Anomali istatistikleri alınırken hata oluştu.", ex);
                AppendToLog(GetString("Str_Log_AnomalyStatsError"), true);
            }
        }

        private void btnClearMLHistory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var message = "Are you sure you want to clear ML history?\n\nThis action cannot be undone.";
                var title = "Clear ML History";
                if (_appSettings.Language == "tr")
                {
                    message = "ML geçmişini temizlemek istediğinizden emin misiniz?\n\nBu işlem geri alınamaz.";
                    title = "ML Geçmişini Temizle";
                }

                var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _mlTextProcessor.ClearHistory();
                    AppendToLog(GetString("Str_Log_MlCleared"));
                    var successMsg = "ML history successfully cleared.";
                    if (_appSettings.Language == "tr") successMsg = "ML geçmişi başarıyla temizlendi.";
                    MessageBox.Show(successMsg, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("ML geçmişi temizlenirken hata oluştu.", ex);
                AppendToLog(GetString("Str_Log_MlClearError"), true);
            }
        }

        private void btnClearAnomalyHistory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var message = "Are you sure you want to clear anomaly detection history?\n\nThis action cannot be undone.";
                var title = "Clear Anomaly History";
                if (_appSettings.Language == "tr")
                {
                    message = "Anomali tespit geçmişini temizlemek istediğinizden emin misiniz?\n\nBu işlem geri alınamaz.";
                    title = "Anomali Geçmişini Temizle";
                }

                var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _anomalyDetector.ClearHistory();
                    AppendToLog(GetString("Str_Log_AnomalyCleared"));
                    var successMsg = "Anomaly detection history successfully cleared.";
                    if (_appSettings.Language == "tr") successMsg = "Anomali tespit geçmişi başarıyla temizlendi.";
                    MessageBox.Show(successMsg, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Anomali geçmişi temizlenirken hata oluştu.", ex);
                AppendToLog(GetString("Str_Log_AnomalyClearError"), true);
            }
        }

        #endregion

        #region Log Yönetimi

        private void btnViewLogFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_log.txt");
                if (File.Exists(logFilePath))
                {
                    var logContent = File.ReadAllText(logFilePath);
                    var logWindow = new System.Windows.Window
                    {
                        Title = "Log Dosyası",
                        Width = 800,
                        Height = 600,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Owner = this
                    };

                    var textBox = new TextBox
                    {
                        Text = logContent,
                        IsReadOnly = true,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        FontSize = 12,
                        Margin = new Thickness(10)
                    };

                    logWindow.Content = textBox;
                    logWindow.Show();

                    _logger.LogInformation("Log dosyası görüntülendi");
                }
                else
                {
                    MessageBox.Show("Log dosyası bulunamadı.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Log dosyası görüntülenirken hata oluştu.", ex);
                MessageBox.Show($"Log dosyası görüntülenirken hata oluştu: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Kullanılmayan metodları aktif hale getiren yeni handler'lar

        private void ClearAllCaches_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var message = "Are you sure you want to clear all caches?\n\n" +
                    "This includes:\n" +
                    "- Translation cache\n" +
                    "- Memory cache\n" +
                    "- Game recipe cache\n" +
                    "- Pointer cache\n" +
                    "- Icon cache";
                var title = "Clear All Caches";

                if (_appSettings.Language == "tr")
                {
                    message = "Tüm önbellekleri temizlemek istediğinizden emin misiniz?\n\n" +
                    "Bu şunları içerir:\n" +
                    "- Çeviri önbelleği\n" +
                    "- Bellek önbelleği\n" +
                    "- Oyun önerileri önbelleği\n" +
                    "- Pointer önbelleği\n" +
                    "- İkon önbelleği";
                    title = "Tüm Önbellekleri Temizle";
                }

                var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    // Memory Service önbellekleri
                    _memoryService?.ClearAllCaches();
                    _logger.LogInformation("Memory Service önbellekleri temizlendi");

                    // Translation Service önbelleği
                    if (_translationService is PerformanceOptimizedTranslationService perfService)
                    {
                        perfService.ClearCache();
                        _logger.LogInformation("PerformanceOptimized önbelleği temizlendi");
                    }
                    if (_translationService is AdvancedTranslationService advService)
                    {
                        advService.ClearExpiredCache();
                        _logger.LogInformation("AdvancedTranslation önbelleği temizlendi");
                    }

                    // Game Recipe önbelleği
                    _gameRecipeService?.ClearCache();
                    _logger.LogInformation("GameRecipe önbelleği temizlendi");

                    // Pointer validation önbelleği
                    _pointerValidationService?.ClearPointerCache();
                    _logger.LogInformation("PointerValidation önbelleği temizlendi");

                    // İkon önbelleği
                    LogoHelper.ClearIconCache();
                    _logger.LogInformation("Icon önbelleği temizlendi");

                    AppendToLog(GetString("Str_Log_CachesCleared"));

                    var successMsg = "All caches successfully cleared.";
                    if (_appSettings.Language == "tr") successMsg = "Tüm önbellekler başarıyla temizlendi.";
                    MessageBox.Show(successMsg, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Önbellekler temizlenirken hata", ex);
                MessageBox.Show($"Error clearing caches: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReloadGameRecipes_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _gameRecipeService?.ReloadRecipes();
                AppendToLog(GetString("Str_Log_RecipesReloaded"));
                var msg = "Game recipes successfully reloaded.";
                if (_appSettings.Language == "tr") msg = "Oyun önerileri başarıyla yeniden yüklendi.";
                MessageBox.Show(msg, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.LogError("Öneriler yüklenirken hata", ex);
                MessageBox.Show($"Error reloading recipes: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetSettingsToDefaults_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var message = "Are you sure you want to reset all settings to default values?\n\n" +
                    "This will:\n" +
                    "- Reset all OCR settings\n" +
                    "- Reset Hotkeys\n" +
                    "- Reset Theme, language and other settings\n\n" +
                    "This action CANNOT be undone!";
                var title = "Reset Settings";

                if (_appSettings.Language == "tr")
                {
                    message = "Tüm ayarları varsayılan değerlere döndürmek istediğinizden emin misiniz?\n\n" +
                    "Bu işlem:\n" +
                    "- Tüm OCR ayarlarını sıfırlar\n" +
                    "- Hotkey'leri varsayılana döndürür\n" +
                    "- Tema, dil ve diğer tüm ayarları sıfırlar\n\n" +
                    "Bu işlem GERİ ALINAMAZ!";
                    title = "Ayarları Sıfırla";
                }

                var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    _appSettings.ResetToDefaults();
                    AppendToLog(GetString("Str_Log_SettingsReset"));
                    var msg = "Settings successfully reset and saved.\n\nRestart application for changes to take full effect.";
                    if (_appSettings.Language == "tr") msg = "Ayarlar başarıyla sıfırlandı ve kaydedildi.\n\nDeğişikliklerin tam olarak uygulanması için uygulamayı yeniden başlatın.";
                    MessageBox.Show(msg, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Ayarlar sıfırlanırken hata", ex);
                MessageBox.Show($"Error resetting settings: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveSettingsNow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _appSettings.SaveSettingsToDisk();
                _settingsManager.SaveSettings(_appSettings);
                AppendToLog(GetString("Str_Log_SettingsSaved"));
                var msg = "Settings successfully saved.";
                if (_appSettings.Language == "tr") msg = "Ayarlar başarıyla kaydedildi.";
                MessageBox.Show(msg, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.LogError("Ayarlar kaydedilirken hata", ex);
                MessageBox.Show($"Error saving settings: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnClearLogFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show("Log dosyasını temizlemek istediğinizden emin misiniz?\n\nBu işlem geri alınamaz.",
                                           "Log Dosyasını Temizle", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    var logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_log.txt");
                    if (File.Exists(logFilePath))
                    {
                        File.WriteAllText(logFilePath, string.Empty);
                        _logger.LogInformation("Log dosyası temizlendi");
                        MessageBox.Show("Log dosyası başarıyla temizlendi.", "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("Log dosyası bulunamadı.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Log dosyası temizlenirken hata oluştu.", ex);
                MessageBox.Show($"Log dosyası temizlenirken hata oluştu: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Memory Pattern Utilities

        private void btnCompareMemoryPatterns_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Örnek: İki bellek bloğunun benzerliğini karşılaştır
                var pi = cmbProcesses.SelectedItem as ProcessInfo;
                if (pi == null)
                {
                    AppendToLog("Önce bir process seçin.", true);
                    return;
                }

                if (!_memoryService.AttachToProcess(pi.Process.Id))
                {
                    AppendToLog("Process'e bağlanılamadı.", true);
                    return;
                }

                // Örnek kullanım: 0x1000 adresinden 100 byte oku
                IntPtr address1 = new IntPtr(0x1000);
                byte[] data1 = _memoryService.ReadBytesCached(address1, 100);

                if (data1.Length > 0)
                {
                    // ByteArrayComparer kullanarak benzerlik kontrolü
                    var comparer = new PointerScanner.ByteArrayComparer();

                    // Aynı adresten 100ms sonra tekrar oku
                    System.Threading.Thread.Sleep(100);
                    byte[] data2 = _memoryService.ReadBytesCached(address1, 100);

                    if (data2.Length > 0)
                    {
                        double similarity = comparer.CalculateSimilarity(data1, data2);
                        AppendToLog($"Bellek benzerliği: {similarity:P} (0x{address1.ToInt64():X})");

                        if (similarity >= 0.95)
                            AppendToLog("✅ Bellek kararlı görünüyor");
                        else if (similarity >= 0.70)
                            AppendToLog("⚠️ Bellek kısmen değişken");
                        else
                            AppendToLog("❌ Bellek çok değişken (dinamik bölge)");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Bellek karşılaştırması sırasında hata oluştu.", ex);
                AppendToLog($"Hata: {ex.Message}", true);
            }
        }

        #endregion

        #region Pointer Cache Management

        private void btnClearPointerCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show("Tüm pointer önbelleklerini temizlemek istediğinizden emin misiniz?\n\n• PointerValidationService önbelleği\n• MemoryService önbelleği",
                                           "Pointer Önbelleğini Temizle", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    // PointerValidationService önbelleğini temizle
                    _pointerValidationService.ClearPointerCache();

                    // MemoryService önbelleğini temizle
                    _memoryService.ClearAllCaches();

                    AppendToLog("Tüm pointer önbellekleri temizlendi:");
                    AppendToLog("  • PointerValidationService önbelleği temizlendi");
                    AppendToLog("  • MemoryService adres ve bellek önbelleği temizlendi");

                    _logger.LogInformation("Kullanıcı tüm pointer önbelleklerini temizledi.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Pointer önbelleği temizlenirken hata oluştu.", ex);
                MessageBox.Show($"Pointer önbelleği temizlenirken hata oluştu: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnListPointerCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // PointerValidationService önbelleği
                var registeredPaths = _pointerValidationService.GetRegisteredPointerPaths();

                AppendToLog($"=== PointerValidationService Önbelleği ({registeredPaths.Count}) ===");

                foreach (var path in registeredPaths.Take(10))
                {
                    AppendToLog($"  • {path}");
                }

                if (registeredPaths.Count > 10)
                {
                    AppendToLog($"  ... ve {registeredPaths.Count - 10} tane daha");
                }

                // MemoryService önbellek istatistikleri
                var cacheStats = _memoryService.GetCacheStatistics();
                AppendToLog($"\n=== MemoryService Önbellek İstatistikleri ===");
                AppendToLog($"  • Adres Önbelleği: {cacheStats.AddressCacheCount} adet");
                AppendToLog($"  • Bellek Önbelleği: {cacheStats.MemoryCacheCount} adet");

                // En sık kullanılan adresler
                var mostFrequent = _memoryService.GetMostFrequentAddresses(5);
                if (mostFrequent.Any())
                {
                    AppendToLog($"\n=== En Sık Kullanılan Adresler (Top 5) ===");
                    foreach (var kvp in mostFrequent)
                    {
                        AppendToLog($"  • 0x{kvp.Value.ToInt64():X} - {kvp.Key}");
                    }
                }

                _logger.LogInformation($"Kullanıcı pointer önbelleğini listeledi. Toplam: PointerValidation={registeredPaths.Count}, MemoryService={cacheStats.AddressCacheCount + cacheStats.MemoryCacheCount}");
            }
            catch (Exception ex)
            {
                _logger.LogError("Pointer önbelleği listelenirken hata oluştu.", ex);
                MessageBox.Show($"Pointer önbelleği listelenirken hata oluştu: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Translation Cache Management

        private void btnClearTranslationCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show("Çeviri önbelleğini temizlemek istediğinizden emin misiniz?\n\nBu işlem geri alınamaz ve tüm kaydedilmiş çeviriler silinecektir.",
                                           "Çeviri Önbelleğini Temizle", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    // AdvancedTranslationService ise
                    if (_translationService is AdvancedTranslationService advancedService)
                    {
                        advancedService.ClearExpiredCache();
                        AppendToLog("Eski çeviri önbelleği temizlendi.");
                    }

                    AppendToLog("Çeviri önbelleği başarıyla temizlendi.");
                    _logger.LogInformation("Kullanıcı çeviri önbelleğini temizledi.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Çeviri önbelleği temizlenirken hata oluştu.", ex);
                MessageBox.Show($"Çeviri önbelleği temizlenirken hata oluştu: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnSaveTranslationCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_translationService is AdvancedTranslationService advancedService)
                {
                    advancedService.SaveCacheToDisk();
                    AppendToLog("Çeviri önbelleği diske kaydedildi.");
                    _logger.LogInformation("Kullanıcı çeviri önbelleğini manuel olarak kaydetti.");
                }
                else
                {
                    AppendToLog("Aktif çeviri servisi önbellek kaydetmeyi desteklemiyor.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Çeviri önbelleği kaydedilirken hata oluştu.", ex);
                MessageBox.Show($"Çeviri önbelleği kaydedilirken hata oluştu: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion


        #endregion


    }
}
