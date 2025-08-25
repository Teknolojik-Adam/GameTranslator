using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.ComponentModel;

namespace P5S_ceviri
{
    public partial class MainWindow : Window
    {
        #region Win32 Imports and Fields
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        private readonly IProcessService _processService;
        private readonly IMemoryService _memoryService;
        private readonly ITranslationService _translationService;
        private readonly ILogger _logger;
        private readonly IOcrService _ocrService;
        private readonly IGameRecipeService _gameRecipeService;
        private readonly SettingsManager _settingsManager;
        private readonly AppSettings _appSettings;
        private readonly EnhancedMemoryService _enhancedMemoryService;
        private readonly PointerValidationService _pointerValidationService;
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
        private IntPtr _dynamicTextAddress = IntPtr.Zero;
        private IntPtr _manualAddress = IntPtr.Zero;
        private string _lastManualText = "";
        private bool _isContinuousOcrRunning = false;
        private bool _isOcrTickBusy = false;
        private System.Drawing.Rectangle? _selectedOcrRegion = null;
        private CancellationTokenSource _scanCancellationTokenSource;
        private List<PointerPath> _lastFoundPaths = new List<PointerPath>();

        #endregion

        public MainWindow()
        {
            InitializeComponent();
            try
            {
                // Initialize services
                ServiceContainer.Initialize();
                _processService = ServiceContainer.GetService<IProcessService>();
                _memoryService = ServiceContainer.GetService<IMemoryService>();
                _translationService = ServiceContainer.GetService<ITranslationService>();
                _logger = ServiceContainer.GetService<ILogger>();
                _ocrService = ServiceContainer.GetService<IOcrService>();
                _gameRecipeService = ServiceContainer.GetService<IGameRecipeService>();
                _settingsManager = ServiceContainer.GetService<SettingsManager>();
                _appSettings = ServiceContainer.GetService<AppSettings>();
                _enhancedMemoryService = new EnhancedMemoryService(_logger);
                _pointerValidationService = new PointerValidationService(_memoryService, _logger);

                InitializeTranslationServices();
                InitializeLanguageControls();

                HotkeySettingsPanel.DataContext = _appSettings;
                _appSettings.PropertyChanged += AppSettings_PropertyChanged;


                _manualTranslationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _manualTranslationTimer.Tick += ManualTranslationTimer_Tick;

                _continuousTranslationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _continuousTranslationTimer.Tick += ContinuousTranslationTimer_Tick;

                _continuousOcrTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _continuousOcrTimer.Tick += ContinuousOcrTimer_Tick;



                this.Closing += (s, e) =>
                {
                    if (_translationService is AdvancedTranslationService advancedService)
                    {
                        advancedService.SaveCacheToDisk();
                    }
                    StopAllTranslations();
                    _memoryService?.Dispose();

                    _outputWindow?.Close();
                    ServiceContainer.Cleanup();
                };

                LoadProcesses();
                InitializeThemeUI();
                UpdateUIState();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Uygulama başlatılırken kritik bir hata oluştu: {ex.Message}", "Kritik Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
            }
        }

        #region Enhanced Pointer Scanner UI Logic

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            try
            {
                var presentationSource = PresentationSource.FromVisual(this);
                if (presentationSource == null)
                {
                    _logger?.LogError("PresentationSource.FromVisual returned null");
                    return;
                }

                var hwndSource = presentationSource as HwndSource;
                if (hwndSource == null)
                {
                    _logger?.LogError("Failed to cast PresentationSource to HwndSource");
                    return;
                }

                _hotkeyManager = new HotkeyManager(hwndSource, _logger);

                // Kısayolları kaydet
                RegisterHotkeys();
            }
            catch (Exception ex)
            {
                _logger?.LogError("HotkeyManager initialization failed", ex);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Kısayolları kaldır
            UnregisterHotkeys();

            base.OnClosed(e);
        }

        private void RegisterHotkeys()
        {
            if (_appSettings.ToggleOcrHotkey != null)
                _ocrHotkeyId = _hotkeyManager.RegisterHotkey(_appSettings.ToggleOcrHotkey.Modifiers, _appSettings.ToggleOcrHotkey.Key, ToggleOcr);

            if (_appSettings.ToggleTranslateWindowHotkey != null)
                _translateWindowHotkeyId = _hotkeyManager.RegisterHotkey(_appSettings.ToggleTranslateWindowHotkey.Modifiers, _appSettings.ToggleTranslateWindowHotkey.Key, ToggleTranslateWindow);

            if (_appSettings.SwitchTranslationServiceHotkey != null)
                _switchTranslationServiceHotkeyId = _hotkeyManager.RegisterHotkey(_appSettings.SwitchTranslationServiceHotkey.Modifiers, _appSettings.SwitchTranslationServiceHotkey.Key, SwitchTranslationService);
        }

        private void AppSettings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName.EndsWith("Hotkey"))
            {
                UpdateHotkeys();
                _settingsManager.SaveSettings(_appSettings);
            }
        }

        private void UpdateHotkeys()
        {
            UnregisterHotkeys();
            RegisterHotkeys();
        }

        private void UnregisterHotkeys()
        {
            try
            {
                if (_hotkeyManager != null)
                {
                    _hotkeyManager.UnregisterHotkey(_ocrHotkeyId);
                    _hotkeyManager.UnregisterHotkey(_translateWindowHotkeyId);
                    _hotkeyManager.UnregisterHotkey(_switchTranslationServiceHotkeyId);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Failed to unregister hotkeys", ex);
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
                AppendToLog("Lütfen önce bir oyun/uygulama seçin.", true);
                return;
            }

            string searchText = txtScanText.Text;
            if (string.IsNullOrWhiteSpace(searchText))
            {
                AppendToLog("Lütfen pattern girin.", true);
                return;
            }

            btnScanPointers.IsEnabled = false;
            btnStopScan.IsEnabled = true;

            lblScanStatus.Text = "Tarama başlatılıyor...";
            lstAddresses.Items.Clear(); // ListBox'ı temizle

            _scanCancellationTokenSource = new CancellationTokenSource();

            try
            {

                var progress = new Progress<int>(value =>
                {

                    Dispatcher.Invoke(() =>
                    {

                        lblScanStatus.Text = $"Tarama devam ediyor... ({value}%)";
                    });
                });


                _enhancedMemoryService.StatusChanged += OnScanStatusChanged;
                _enhancedMemoryService.ProgressChanged += OnScanProgressChanged;

                AppendToLog("Pattern taraması başlatılıyor...");


                List<IntPtr> addresses = await _enhancedMemoryService.FindPatternAddressesAsync(
                    pi.Process, searchText, _scanCancellationTokenSource.Token, progress);

                if (addresses == null || !addresses.Any())
                {
                    AppendToLog("Belirtilen pattern bulunamadı.");
                    return;
                }

                AppendToLog($"{addresses.Count} adet adres bulundu.");


                _lastFoundPaths.Clear(); // Önceki sonuçları temizle


                int maxAddressesToScan = Math.Min(10, addresses.Count); //  ilk 10 adresi tara

                for (int i = 0; i < maxAddressesToScan; i++)
                {
                    IntPtr targetAddress = addresses[i];
                    AppendToLog($"Pointer taraması başlatılıyor: 0x{targetAddress.ToInt64():X} ({i + 1}/{maxAddressesToScan})");

                    var scanner = new PointerScanner(pi.Process, _logger);

                    // Pointer yollarını bulmak için tarama yap
                    var pathsForAddress = await scanner.FindPointers(targetAddress, maxDepth: 3);

                    _lastFoundPaths.AddRange(pathsForAddress);

                    AppendToLog($" • {pathsForAddress.Count} pointer yolu bulundu.");

                    // Bulunan yolları ListBox'a ekle 
                    foreach (var path in pathsForAddress.Take(10)) // İlk 10'u göster
                    {
                        lstAddresses.Items.Add(new ListBoxItem { Content = path.ToString() });
                    }
                }

                if (_lastFoundPaths.Any())
                {
                    btnTestPointer.IsEnabled = true;
                    btnSavePointers.IsEnabled = true;
                    AppendToLog("Pointer taraması tamamlandı. Pointer'ları test edebilir veya kaydedebilirsiniz.");
                }
                else
                {
                    AppendToLog("Hiçbir pointer yolu bulunamadı.");
                }


            }
            catch (OperationCanceledException)
            {
                AppendToLog("Pattern taraması kullanıcı tarafından durduruldu.");
            }
            catch (Exception ex)
            {
                AppendToLog($"Tarama sırasında hata: {ex.Message}", true);
                _logger?.LogError("Pointer taraması sırasında hata oluştu.", ex);
            }
            finally
            {

                _enhancedMemoryService.StatusChanged -= OnScanStatusChanged;
                _enhancedMemoryService.ProgressChanged -= OnScanProgressChanged;
                btnScanPointers.IsEnabled = true;
                btnStopScan.IsEnabled = false;

                lblScanStatus.Text = "";
                _scanCancellationTokenSource?.Dispose();
                _scanCancellationTokenSource = null;
            }
        }

        private void btnStopScan_Click(object sender, RoutedEventArgs e)
        {
            _scanCancellationTokenSource?.Cancel();
            AppendToLog("Pattern taraması durdurma komutu verildi...");
        }

        private async void btnTestPointer_Click(object sender, RoutedEventArgs e)
        {
            if (!_lastFoundPaths.Any())
            {
                AppendToLog("Test edilecek pointer yolu bulunamadı. Önce tarama yapın.", true);
                return;
            }

            var pi = cmbProcesses.SelectedItem as ProcessInfo;
            if (pi == null) return;

            // En iyi skorlu pointer'ı test et
            var bestPath = _lastFoundPaths.First();
            AppendToLog($"Pointer stabilite testi başlatılıyor: {bestPath}");

            try
            {
                var stabilityResult = await _pointerValidationService.TestPointerStabilityAsync(pi.Process, bestPath, 15, 500);

                AppendToLog($"Stabilite Testi Sonuçları:");
                AppendToLog($"  • Başarı Oranı: {stabilityResult.SuccessRate:F1}%");
                AppendToLog($"  • Adres Tutarlılığı: {stabilityResult.AddressConsistency:F1}%");
                AppendToLog($"  • Değer Tutarlılığı: {stabilityResult.ValueConsistency:F1}%");
                AppendToLog($"  • Genel Stabilite Skoru: {stabilityResult.StabilityScore}/100");

                if (stabilityResult.StabilityScore >= 80)
                    AppendToLog("Bu pointer güvenilir görünüyor!");
                else if (stabilityResult.StabilityScore >= 60)
                    AppendToLog("Bu pointer orta derecede güvenilir.");
                else
                    AppendToLog("Bu pointer güvenilir değil, başka pointer'lar deneyin.", true);
            }
            catch (Exception ex)
            {
                AppendToLog($"Stabilite testi sırasında hata: {ex.Message}", true);
            }
        }

        private void btnSavePointers_Click(object sender, RoutedEventArgs e)
        {
            if (!_lastFoundPaths.Any())
            {
                AppendToLog("Kaydedilecek pointer bulunamadı.", true);
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
                    AppendToLog($"Pointer'lar kaydedildi: {saveDialog.FileName}");
                }
            }
            catch (Exception ex)
            {
                AppendToLog($"Kaydetme sırasında hata: {ex.Message}", true);
            }
        }

        private void btnLoadPointers_Click(object sender, RoutedEventArgs e)
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
                        AppendToLog($"{loadedPaths.Count} adet pointer yolu yüklendi: {openDialog.FileName}");

                        // Loaded pointer'ları göstermek için
                        foreach (var path in loadedPaths.Take(10))
                        {
                            AppendToLog($"  • {path}");
                        }

                        btnTestPointer.IsEnabled = true;
                        btnSavePointers.IsEnabled = true;
                    }
                    else
                    {
                        AppendToLog("Dosyada geçerli pointer bulunamadı.", true);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendToLog($"Yükleme sırasında hata: {ex.Message}", true);
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


        #endregion

        #region Existing Methods

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

                // Olay dinleyicilerini ekle
                cmbOcrLanguage.SelectionChanged += CmbOcrLanguage_SelectionChanged;
                cmbTargetLanguage.SelectionChanged += CmbTargetLanguage_SelectionChanged;
                chkEnableColorFilter.Click += ChkEnableColorFilter_Click;
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
                if (_translationService is AdvancedTranslationService advancedService)
                {
                    cmbTranslationService.ItemsSource = advancedService.AvailableStrategies;
                    cmbTranslationService.SelectedIndex = 0;
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
                if (!string.IsNullOrEmpty(currentText) && currentText != _lastReadText)
                {
                    _lastReadText = currentText;
                    string translated = await _translationService.TranslateAsync(currentText, _appSettings.TargetLanguage, GetSelectedTranslationStrategy());
                    Dispatcher.Invoke(() => { txtOriginal.Text = $"[RAM] {currentText}"; txtTranslated.Text = translated; OnTranslatedTextChanged(translated); });
                }
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
                    _lastManualText = currentText;
                    string translated = await _translationService.TranslateAsync(currentText, _appSettings.TargetLanguage, GetSelectedTranslationStrategy());
                    Dispatcher.Invoke(() => { txtOriginal.Text = $"[Manuel] {currentText}"; txtTranslated.Text = translated; OnTranslatedTextChanged(translated); });
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

                    //  kırpma
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
                    if (_appSettings.EnableOcrColorFilter)
                    {
                        imageForOcr = _ocrService.IsolateTextByColor(imageToProcess);
                        imageToProcess.Dispose(); // Dispose original if we created a new filtered one
                    }

                    using (imageForOcr)
                    {
                        string currentText = await _ocrService.GetTextAdaptiveAsync(imageForOcr, _appSettings.OcrLanguage);


                        if (string.IsNullOrWhiteSpace(currentText) || currentText == _lastReadText)
                            return;

                        _lastReadText = currentText;

                        // Çeviri 
                        string translated = await _translationService.TranslateAsync(
                            currentText,
                            _appSettings.TargetLanguage,
                            GetSelectedTranslationStrategy());


                        Dispatcher.Invoke(() =>
                        {
                            txtOriginal.Text = $"[OCR] {currentText}";
                            txtTranslated.Text = translated;
                            OnTranslatedTextChanged(translated);
                        });
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
                _appSettings.LastProcessName = pi.ProcessName;
                _settingsManager.SaveSettings(_appSettings);
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
                    AppendToLog($"Yeni OCR bölgesi seçildi: {region}");
                };
                _outputWindow.Show();
                AppendToLog("Çeviri penceresi gösterildi.");
            }
            else
            {
                _outputWindow.Close();
                _outputWindow = null;
                AppendToLog("Çeviri penceresi gizlendi.");
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
            if (pi == null) { AppendToLog("Lütfen önce listeden bir uygulama seçin.", true); return; }
            if (!_memoryService.AttachToProcess(pi.Process.Id)) { AppendToLog("Uygulamaya bağlanılamadı. Yönetici olarak çalıştırmayı deneyin.", true); return; }
            try
            {
                _manualAddress = addressText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? new IntPtr(long.Parse(addressText.Substring(2), NumberStyles.HexNumber)) : new IntPtr(long.Parse(addressText, NumberStyles.HexNumber));
                AppendToLog($"Gerçek zamanlı adres okuma başlatılıyor: {_manualAddress.ToInt64():X}");
                _lastManualText = "";
                _manualTranslationTimer.Start();
                UpdateUIState();
            }
            catch (Exception ex) { AppendToLog($"Adres analiz etme hatası: {ex.Message}", true); }
        }

        private async void StartContinuousTranslation()
        {
            StopAllTranslations();
            var pi = cmbProcesses.SelectedItem as ProcessInfo;
            if (pi == null) { AppendToLog("Lütfen bir uygulama seçin."); return; }
            if (!_memoryService.AttachToProcess(pi.Process.Id)) { AppendToLog("Uygulamaya bağlanılamadı.", true); return; }
            var recipe = await _gameRecipeService.GetRecipeForProcessAsync(pi.Process);
            if (recipe == null) return;
            _dynamicTextAddress = _memoryService.ResolveAddressFromPath(pi.Process, recipe);
            if (_dynamicTextAddress == IntPtr.Zero)
            {
                AppendToLog("Adres çözümlenemedi! Yol geçersiz veya oyun güncellenmiş olabilir.", true);
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
            if (cmbProcesses.SelectedItem == null) { AppendToLog("Lütfen önce listeden bir oyun seçin."); return; }
            _isContinuousOcrRunning = true;
            _continuousOcrTimer.Start();
            UpdateUIState();
        }



        private void StopAllTranslations()
        {
            if (_isContinuousTranslationRunning) { _isContinuousTranslationRunning = false; _continuousTranslationTimer.Stop(); AppendToLog("Otomatik RAM çevirisi durduruldu."); }
            if (_manualTranslationTimer.IsEnabled) { _manualTranslationTimer.Stop(); _manualAddress = IntPtr.Zero; AppendToLog("Manuel RAM çevirisi durduruldu."); }
            if (_isContinuousOcrRunning) StopContinuousOcr();

            UpdateUIState();
        }

        private void StopContinuousOcr()
        {
            _isContinuousOcrRunning = false;
            _continuousOcrTimer.Stop();
            _lastReadText = "";
            _isOcrTickBusy = false;
            AppendToLog("Ekran çevirisi durduruldu.");
            UpdateUIState();
        }

        private void SetupNewRecipe()
        {
            var pi = cmbProcesses.SelectedItem as ProcessInfo;
            if (pi == null) return;
            var prompt = new InputDialog("Lütfen Cheat Engine ile bulduğunuz kalıcı pointer yolunu girin:", "\"OyunAdi.exe\"+1A2B3C, 40, 1F8, 10");
            if (prompt.ShowDialog() == true)
            {
                var (baseModule, baseOffset, offsets) = ParsePointerPath(prompt.Answer);
                if (string.IsNullOrWhiteSpace(baseModule) || offsets == null) { MessageBox.Show("Girdi formatı geçersiz.", "Hatalı Giriş", MessageBoxButton.OK, MessageBoxImage.Error); return; }
                var newRecipe = new GameRecipe { ProcessName = pi.ProcessName, PathInfo = new PathInfo { BaseAddressModule = baseModule, BaseAddressOffset = baseOffset, PointerOffsets = offsets } };
                _gameRecipeService.SaveOrUpdateRecipe(newRecipe);
                AppendToLog($"'{pi.ProcessName}' için yeni çeviri yolu kaydedildi! Çeviri başlatılıyor...");
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

            if (_isContinuousTranslationRunning || _manualTranslationTimer.IsEnabled) { btnTranslate.Content = "RAM Çevirisini Durdur"; btnTranslate.IsEnabled = true; }
            else if (_isSetupMode && processSelected) { btnTranslate.Content = "Yeni Çeviri Yolu Kur..."; btnTranslate.IsEnabled = !anyTranslationRunning; }
            else { btnTranslate.Content = "RAM Çevirisini Başlat"; btnTranslate.IsEnabled = processSelected && !anyTranslationRunning; }

            if (_isContinuousOcrRunning) { btnContinuousOcr.Content = "Ekran Çevirisini Durdur"; btnContinuousOcr.IsEnabled = true; }
            else { btnContinuousOcr.Content = "Ekran Çevirisini Başlat"; btnContinuousOcr.IsEnabled = processSelected && !anyTranslationRunning; }



            if (!processSelected) { txtAddress.Text = "Lütfen bir uygulama seçin."; }
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
                AppendToLog("Çalışan işlemler listeleniyor...");
                var selectedBefore = cmbProcesses.SelectedItem as ProcessInfo;
                _processService.RefreshProcesses();
                var processes = _processService.GetProcesses().Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle)).Select(p => new ProcessInfo(p)).OrderBy(p => p.ProcessName).ToList();
                cmbProcesses.ItemsSource = processes;
                var processToSelect = processes.FirstOrDefault(p => selectedBefore != null && p.Process.Id == selectedBefore.Process.Id) ?? processes.FirstOrDefault(p => !string.IsNullOrEmpty(_appSettings.LastProcessName) && p.ProcessName == _appSettings.LastProcessName);
                if (processToSelect != null) { cmbProcesses.SelectedItem = processToSelect; }
                AppendToLog($"{processes.Count} adet pencereli uygulama bulundu.");
            }
            catch (Exception ex)
            {
                AppendToLog($"İşlem listesi yüklenirken hata: {ex.Message}", true);
            }
        }

        private void AppendToLog(string message, bool isError = false)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => AppendToLog(message, isError)); return; }
            string logType = isError ? "[HATA]" : "[BİLGİ]";
            string timestampedMessage = $"{DateTime.Now:HH:mm:ss} {logType} - {message}";
            txtOutput.Items.Add(timestampedMessage);
            if (txtOutput.Items.Count > 0) { txtOutput.ScrollIntoView(txtOutput.Items[txtOutput.Items.Count - 1]); }
            if (txtOutput.Items.Count > 500) { txtOutput.Items.RemoveAt(0); }
        }

        protected virtual void OnTranslatedTextChanged(string newText) => TranslatedTextChanged?.Invoke(newText);

        #region Theme Management

        private void InitializeThemeUI()
        {
            try
            {
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

                    // Temayı değiştir
                    ThemeManager.ChangeTheme(selectedTheme);

                    // Ayarlara kaydet
                    _appSettings.Theme = themeString;
                    _settingsManager.SaveSettings(_appSettings);

                    // Log kaydet
                    AppendToLog($"Tema değiştirildi: {selectedItem.Content}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Tema değiştirme sırasında hata oluştu.", ex);
                AppendToLog("Tema değiştirme sırasında hata oluştu.", true);
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
                    AppendToLog($"OCR motoru değiştirildi: {engineName}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("OCR motoru değiştirme sırasında hata oluştu.", ex);
                AppendToLog("OCR motoru değiştirme sırasında hata oluştu.", true);
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

        #endregion
        #endregion
    }
}