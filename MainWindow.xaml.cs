using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

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
                ServiceContainer.Initialize();
                _processService = ServiceContainer.GetService<IProcessService>();
                _memoryService = ServiceContainer.GetService<IMemoryService>();
                _translationService = ServiceContainer.GetService<ITranslationService>();
                _logger = ServiceContainer.GetService<ILogger>();
                _ocrService = ServiceContainer.GetService<IOcrService>();
                _gameRecipeService = ServiceContainer.GetService<IGameRecipeService>();
                _settingsManager = new SettingsManager(_logger);
                _appSettings = _settingsManager.LoadSettings();
                _enhancedMemoryService = new EnhancedMemoryService(_logger);
                _pointerValidationService = new PointerValidationService(_memoryService, _logger);

                InitializeTranslationServices();

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

            HwndSource hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            _hotkeyManager = new HotkeyManager(hwndSource);

            // Kısayolları kaydet
            RegisterHotkeys();
        }

        protected override void OnClosed(EventArgs e)
        {
            // Kısayolları kaldır
            UnregisterHotkeys();

            base.OnClosed(e);
        }

        private void RegisterHotkeys()
        {
            // OCR başlatma/durdurma kısayolu
            _ocrHotkeyId = _hotkeyManager.RegisterHotkey(ModifierKeys.Control | ModifierKeys.Shift, Key.O, ToggleOcr);

            // Çeviri penceresini açma/kapama kısayolu
            _translateWindowHotkeyId = _hotkeyManager.RegisterHotkey(ModifierKeys.Control | ModifierKeys.Shift, Key.T, ToggleTranslateWindow);

            // Çeviri servisleri arasında geçiş kısayolu
            _switchTranslationServiceHotkeyId = _hotkeyManager.RegisterHotkey(ModifierKeys.Control | ModifierKeys.Shift, Key.S, SwitchTranslationService); // Yeni kısayol
        }

        private void UnregisterHotkeys()
        {
            _hotkeyManager.UnregisterHotkey(_ocrHotkeyId);
            _hotkeyManager.UnregisterHotkey(_translateWindowHotkeyId);
            _hotkeyManager.UnregisterHotkey(_switchTranslationServiceHotkeyId); // Yeni kısayol
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
            // Arama metnini normalize et
            searchText = NormalizeString(searchText).Trim();

            // UI ayarları
            btnScanPointers.IsEnabled = false;
            btnStopScan.IsEnabled = true;
            progressScan.Visibility = Visibility.Visible;
            progressScan.Value = 0;
            lblScanStatus.Text = "Tarama başlatılıyor...";

            // ListBox'ı temizle
            lstAddresses.Items.Clear();

            _scanCancellationTokenSource = new CancellationTokenSource();

            try
            {
                var progress = new Progress<int>(value =>
                {
                    Dispatcher.Invoke(() => progressScan.Value = value);
                });

                _enhancedMemoryService.StatusChanged += OnScanStatusChanged;
                _enhancedMemoryService.ProgressChanged += OnScanProgressChanged;

                AppendToLog($"'{searchText}' pattern'i için pattern taraması başlatılıyor...");

                // Pattern araması
                List<IntPtr> addresses = await _enhancedMemoryService.FindPatternAddressesAsync(
                    pi.Process, searchText, _scanCancellationTokenSource.Token, progress);

                if (addresses == null || addresses.Count == 0)
                {
                    AppendToLog($"'{searchText}' pattern'i bulunamadı.", true);
                    return;
                }

                AppendToLog($"{addresses.Count} adres bulundu.");

                // Adresleri ListBox'a ekle
                foreach (var address in addresses)
                {
                    string addressString = $"0x{address.ToInt64():X}";
                    lstAddresses.Items.Add(addressString);
                    AppendToLog($"Bulunan adres: {addressString}");
                }
            }
            catch (OperationCanceledException)
            {
                AppendToLog("Pattern taraması kullanıcı tarafından durduruldu.");
            }
            catch (Exception ex)
            {
                AppendToLog($"Tarama sırasında hata: {ex.Message}", true);
            }
            finally
            {
                _enhancedMemoryService.StatusChanged -= OnScanStatusChanged;
                _enhancedMemoryService.ProgressChanged -= OnScanProgressChanged;
                btnScanPointers.IsEnabled = true;
                btnStopScan.IsEnabled = false;
                //progressScan.Visibility.Collapsed = Visibility.Collapsed;
                lblScanStatus.Text = "";
                _scanCancellationTokenSource?.Dispose();
                _scanCancellationTokenSource = null;
            }
        }

        private string NormalizeString(string input)
        {
            string normalized = input.Normalize(NormalizationForm.FormKD);
            return normalized;
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

                        // Loaded pointer'ları göster
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
            Dispatcher.Invoke(() => lblScanStatus.Text = status);
        }

        private void OnScanProgressChanged(int progress)
        {
            Dispatcher.Invoke(() => progressScan.Value = progress);
        }

        private void DisplayPointerResults(List<PointerValidationResult> results)
        {
            var validResults = results.Where(r => r.IsValid).OrderByDescending(r => r.Score).Take(15);
            var invalidResults = results.Where(r => !r.IsValid).OrderByDescending(r => r.Score).Take(5);

            AppendToLog("=== GEÇERLİ POINTER'LAR (En İyiden Kötüye) ===");
            foreach (var result in validResults)
            {
                string preview = result.CurrentValue != null ?
                    result.CurrentValue.Substring(0, Math.Min(50, result.CurrentValue.Length)) :
                    "[Boş]";
                AppendToLog($"[Skor: {result.Score}] {result.Path} -> \"{preview}...\"");
            }

            if (invalidResults.Any())
            {
                AppendToLog("=== GEÇERSİZ POINTER'LAR ===");
                foreach (var result in invalidResults.Take(3))
                {
                    AppendToLog($"[Hata] {result.Path} -> {result.ErrorMessage}");
                }
            }

            AppendToLog("İpucu: Yüksek skorlu pointer'lar daha güvenilirdir.");
        }
        #endregion

        #region Existing Methods
        private void InitializeTranslationServices()
        {
            if (_translationService is AdvancedTranslationService advancedService)
            {
                cmbTranslationService.ItemsSource = advancedService.AvailableStrategies;
                cmbTranslationService.SelectedIndex = 0;
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
                    string translated = await _translationService.TranslateAsync(currentText, "tr", GetSelectedTranslationStrategy());
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
                    string translated = await _translationService.TranslateAsync(currentText, "tr", GetSelectedTranslationStrategy());
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
            // Aynı anda birden fazla OCR tick'i çalışmasını engelle
            if (_isOcrTickBusy) return;
            _isOcrTickBusy = true;

            try
            {
                // OCR devre dışıysa veya kullanıcı durdurduysa
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

                // Ekran görüntüsü al (arka planda)
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
                            // Clone ile yeni bir bitmap oluştur (Dispose güvenliği için)
                            imageToProcess = new Bitmap(cropped);
                        }
                    }
                    else
                    {
                        // Tüm pencereyi kullan, güvenli kopya al
                        imageToProcess = new Bitmap(screenshot);
                    }


                    string currentText = await _ocrService.GetTextAdaptiveAsync(imageToProcess, "eng");


                    imageToProcess.Dispose();

                    // Geçerli metin yoksa veya değişmediyse işlem yapma
                    if (string.IsNullOrWhiteSpace(currentText) || currentText == _lastReadText)
                        return;

                    _lastReadText = currentText;

                    // Çeviri isteği
                    string translated = await _translationService.TranslateAsync(
                        currentText,
                        "tr",
                        GetSelectedTranslationStrategy());


                    Dispatcher.Invoke(() =>
                    {
                        txtOriginal.Text = $"[OCR] {currentText}";
                        txtTranslated.Text = translated;
                        OnTranslatedTextChanged(translated);
                    });
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

                // Baz adresi (hex) parse et
                if (!long.TryParse(offsetHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long baseOffset))
                {
                    _logger?.LogError($"ParsePointerPath: Baz adres geçersiz. Girdi: {offsetHex}");
                    return (null, 0, null);
                }

                // Kalan offset'leri parse et
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
            AppendToLog("Çalışan işlemler listeleniyor...");
            var selectedBefore = cmbProcesses.SelectedItem as ProcessInfo;
            _processService.RefreshProcesses();
            var processes = _processService.GetProcesses().Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle)).Select(p => new ProcessInfo(p)).OrderBy(p => p.ProcessName).ToList();
            cmbProcesses.ItemsSource = processes;
            var processToSelect = processes.FirstOrDefault(p => selectedBefore != null && p.Process.Id == selectedBefore.Process.Id) ?? processes.FirstOrDefault(p => !string.IsNullOrEmpty(_appSettings.LastProcessName) && p.ProcessName == _appSettings.LastProcessName);
            if (processToSelect != null) { cmbProcesses.SelectedItem = processToSelect; }
            AppendToLog($"{processes.Count} adet pencereli uygulama bulundu.");
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
                // Mevcut tema tercihini al
                var currentTheme = ThemeManager.GetThemeFromString(_appSettings.Theme);

                // ComboBox'ta doğru seçimi yap
                foreach (ComboBoxItem item in cmbTheme.Items)
                {
                    if (item.Tag.ToString() == ThemeManager.GetStringFromTheme(currentTheme))
                    {
                        cmbTheme.SelectedItem = item;
                        break;
                    }
                }

                // Eğer hiçbiri seçili değilse, varsayılan olarak Light'ı seç
                if (cmbTheme.SelectedItem == null)
                {
                    cmbTheme.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Tema UI başlatılırken hata oluştu.", ex);
                cmbTheme.SelectedIndex = 0;
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
        #endregion
        #endregion
    }
}