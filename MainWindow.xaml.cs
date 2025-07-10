using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace P5S_ceviri
{
    public partial class MainWindow : Window
    {
        #region Win32 Imports
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        #endregion

        #region Fields & Properties
        private readonly IProcessService _processService;
        private readonly IMemoryService _memoryService;
        private readonly ITranslationService _translationService;
        private readonly ILogger _logger;
        private readonly IOcrService _ocrService;
        private readonly IGameRecipeService _gameRecipeService;
        private readonly SettingsManager _settingsManager;

        private readonly DispatcherTimer _continuousTranslationTimer;
        private readonly DispatcherTimer _manualTranslationTimer;
        private readonly DispatcherTimer _continuousOcrTimer;

        private readonly AppSettings _appSettings;
        private bool _isSetupMode = false;
        private OutputWindow _outputWindow;
        public event Action<string> TranslatedTextChanged;

        private bool _isContinuousTranslationRunning = false;
        private string _lastReadText = "";
        private IntPtr _dynamicTextAddress = IntPtr.Zero;
        private IntPtr _manualAddress = IntPtr.Zero;
        private string _lastManualText = "";

        private bool _isContinuousOcrRunning = false;
        private bool _isOcrTickBusy = false;

        private System.Drawing.Rectangle? _selectedOcrRegion = null;
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

                InitializeTranslationServices();

                _manualTranslationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _manualTranslationTimer.Tick += ManualTranslationTimer_Tick;

                _continuousTranslationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _continuousTranslationTimer.Tick += ContinuousTranslationTimer_Tick;

                _continuousOcrTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _continuousOcrTimer.Tick += ContinuousOcrTimer_Tick;

                this.Closing += (s, e) =>
                {
                    StopAllTranslations();
                    _memoryService?.Dispose();
                    _outputWindow?.Close();
                    ServiceContainer.Cleanup();
                };

                LoadProcesses();
                UpdateUIState();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Uygulama başlatılırken kritik bir hata oluştu: {ex.Message}", "Kritik Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
            }
        }

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
            if (cmbTranslationService.SelectedItem is StrategyInfo selectedStrategy)
            {
                return selectedStrategy.Type;
            }
            return null;
        }

        #region Timer Ticks
        private async void ContinuousTranslationTimer_Tick(object sender, EventArgs e)
        {
            var pi = cmbProcesses.SelectedItem as ProcessInfo;
            if (pi == null || !_isContinuousTranslationRunning || pi.Process.HasExited)
            {
                StopAllTranslations();
                return;
            }
            if (_dynamicTextAddress == IntPtr.Zero) return;

            string currentText = _memoryService.TryReadStringDeep(_dynamicTextAddress);
            if (!string.IsNullOrEmpty(currentText) && currentText != _lastReadText)
            {
                _lastReadText = currentText;
                string translated = await _translationService.TranslateAsync(currentText, "tr", GetSelectedTranslationStrategy());
                Dispatcher.Invoke(() =>
                {
                    txtOriginal.Text = $"[RAM] {currentText}";
                    txtTranslated.Text = translated;
                    OnTranslatedTextChanged(translated);
                });
            }
        }

        private async void ManualTranslationTimer_Tick(object sender, EventArgs e)
        {
            if (_manualAddress == IntPtr.Zero) return;

            string currentText = _memoryService.TryReadStringDeep(_manualAddress);
            if (!string.IsNullOrWhiteSpace(currentText) && currentText != _lastManualText)
            {
                _lastManualText = currentText;
                string translated = await _translationService.TranslateAsync(currentText, "tr", GetSelectedTranslationStrategy());
                Dispatcher.Invoke(() =>
                {
                    txtOriginal.Text = $"[Manuel] {currentText}";
                    txtTranslated.Text = translated;
                    OnTranslatedTextChanged(translated);
                });
            }
        }
        private async void ContinuousOcrTimer_Tick(object sender, EventArgs e)
        {
            if (_isOcrTickBusy) return;
            _isOcrTickBusy = true;

            try
            {
                if (!_isContinuousOcrRunning) return;
                var pi = cmbProcesses.SelectedItem as ProcessInfo;
                if (pi == null || pi.Process.HasExited)
                {
                    StopContinuousOcr();
                    return;
                }

                var handle = pi.Process.MainWindowHandle;
                if (handle == IntPtr.Zero) return;

                using (var screenshot = _ocrService.CaptureWindow(handle))
                {
                    if (screenshot == null) return;

                    using (Bitmap imageToProcess = _selectedOcrRegion.HasValue ?
                           _ocrService.CropImage(screenshot, _selectedOcrRegion.Value) :
                           (Bitmap)screenshot.Clone())
                    {
                        string currentText = await _ocrService.GetTextAdaptiveAsync(imageToProcess, "eng");

                        if (!string.IsNullOrWhiteSpace(currentText) && currentText != _lastReadText)
                        {
                            _lastReadText = currentText;
                            string translated = await _translationService.TranslateAsync(currentText, "tr", GetSelectedTranslationStrategy());

                            Dispatcher.Invoke(() =>
                            {
                                txtOriginal.Text = $"[OCR] {currentText}";
                                txtTranslated.Text = translated;
                                OnTranslatedTextChanged(translated);
                            });
                        }
                    }
                }
            }
            finally
            {
                _isOcrTickBusy = false;
            }
        }
        #endregion

        #region UI Event Handlers
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
            }
        }

        private void btnTranslate_Click(object sender, RoutedEventArgs e)
        {
            if (_isContinuousTranslationRunning || _manualTranslationTimer.IsEnabled)
            {
                StopAllTranslations();
                return;
            }

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
                _outputWindow.RegionSelected += (region) => {
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
        #endregion

        #region Core Logic
        private void StartManualTranslation(string addressText)
        {
            var pi = cmbProcesses.SelectedItem as ProcessInfo;
            if (pi == null)
            {
                AppendToLog("Lütfen önce listeden bir uygulama seçin.", true);
                return;
            }

            if (!_memoryService.AttachToProcess(pi.Process.Id))
            {
                AppendToLog("Uygulamaya bağlanılamadı. Yönetici olarak çalıştırmayı deneyin.", true);
                return;
            }

            try
            {
                _manualAddress = addressText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? new IntPtr(long.Parse(addressText.Substring(2), NumberStyles.HexNumber))
                    : new IntPtr(long.Parse(addressText, NumberStyles.HexNumber));

                AppendToLog($"Gerçek zamanlı adres okuma başlatılıyor: {_manualAddress.ToInt64():X}");
                _lastManualText = "";
                _manualTranslationTimer.Start();
                UpdateUIState();
            }
            catch (Exception ex)
            {
                AppendToLog($"Adres analiz etme hatası: {ex.Message}", true);
            }
        }

        private async void StartContinuousTranslation()
        {
            StopAllTranslations();
            var pi = cmbProcesses.SelectedItem as ProcessInfo;
            if (pi == null)
            {
                AppendToLog("Lütfen bir uygulama seçin.");
                return;
            }

            if (!_memoryService.AttachToProcess(pi.Process.Id))
            {
                AppendToLog("Uygulamaya bağlanılamadı.", true);
                return;
            }

            var recipe = await _gameRecipeService.GetRecipeForProcessAsync(pi.Process);
            if (recipe == null) return;

            AppendToLog($"'{pi.ProcessName}' için kayıtlı çeviri yolu kullanılıyor...");
            _dynamicTextAddress = _memoryService.ResolveAddressFromPath(pi.Process, recipe);

            if (_dynamicTextAddress == IntPtr.Zero)
            {
                AppendToLog("Adres çözümlenemedi! Yol geçersiz veya oyun güncellenmiş olabilir.", true);
                _isSetupMode = true;
                UpdateUIState();
                return;
            }

            txtAddress.Text = $"0x{_dynamicTextAddress.ToInt64():X}";
            AppendToLog($"Metin adresi başarıyla bulundu: {txtAddress.Text}");
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
            _isContinuousOcrRunning = true;
            _continuousOcrTimer.Start();
            UpdateUIState();
        }

        private void StopAllTranslations()
        {
            if (_isContinuousTranslationRunning)
            {
                _isContinuousTranslationRunning = false;
                _continuousTranslationTimer.Stop();
                AppendToLog("Otomatik RAM çevirisi durduruldu.");
            }
            if (_manualTranslationTimer.IsEnabled)
            {
                _manualTranslationTimer.Stop();
                _manualAddress = IntPtr.Zero;
                AppendToLog("Manuel RAM çevirisi durduruldu.");
            }
            if (_isContinuousOcrRunning)
            {
                StopContinuousOcr();
            }
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

            var prompt = new InputDialog("Lütfen Cheat Engine ile bulduğunuz kalıcı pointer yolunu girin:",
                "\"OyunAdi.exe\"+1A2B3C, 40, 1F8, 10");

            if (prompt.ShowDialog() == true)
            {
                var (baseModule, baseOffset, offsets) = ParsePointerPath(prompt.Answer);
                if (string.IsNullOrWhiteSpace(baseModule) || offsets == null)
                {
                    MessageBox.Show("Girdi formatı geçersiz.", "Hatalı Giriş", MessageBoxButton.OK, MessageBoxImage.Error);
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

                AppendToLog($"'{pi.ProcessName}' için yeni çeviri yolu kaydedildi! Çeviri başlatılıyor...");
                _isSetupMode = false;
                UpdateUIState();
                StartContinuousTranslation();
            }
        }
        #endregion

        #region Helper Methods
        private void UpdateUIState()
        {
            bool processSelected = cmbProcesses.SelectedItem != null;
            bool anyRamTranslationRunning = _isContinuousTranslationRunning || _manualTranslationTimer.IsEnabled;
            bool anyTranslationRunning = anyRamTranslationRunning || _isContinuousOcrRunning;

            cmbProcesses.IsEnabled = !anyTranslationRunning;
            cmbTranslationService.IsEnabled = !anyTranslationRunning;

            if (anyRamTranslationRunning)
            {
                btnTranslate.Content = "RAM Çevirisini Durdur";
                btnTranslate.IsEnabled = true;
            }
            else if (_isSetupMode && processSelected)
            {
                btnTranslate.Content = "Yeni Çeviri Yolu Kur...";
                btnTranslate.IsEnabled = !anyTranslationRunning;
            }
            else
            {
                btnTranslate.Content = "RAM Çevirisini Başlat";
                btnTranslate.IsEnabled = processSelected && !anyTranslationRunning;
            }

            if (_isContinuousOcrRunning)
            {
                btnContinuousOcr.Content = "Ekran Çevirisini Durdur";
                btnContinuousOcr.IsEnabled = true;
            }
            else
            {
                btnContinuousOcr.Content = "Ekran Çevirisini Başlat";
                btnContinuousOcr.IsEnabled = processSelected && !anyTranslationRunning;
            }

            if (!processSelected)
            {
                txtAddress.Text = "Lütfen bir uygulama seçin.";
            }
        }

        private (string Module, long Offset, List<int> Offsets) ParsePointerPath(string input)
        {
            try
            {
                var parts = input.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1) return (null, 0, null);

                var baseMatch = Regex.Match(parts[0].Trim(), @"[""']?(?<module>.+?\.exe)[""']?\s*\+\s*(0x)?(?<offset>[0-9A-Fa-f]+)");
                if (!baseMatch.Success) return (null, 0, null);

                var offsets = parts.Skip(1)
                    .Select(p => int.TryParse(p.Trim().Replace("0x", ""),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out int val) ? val : 0)
                    .ToList();

                return (
                    baseMatch.Groups["module"].Value,
                    long.Parse(baseMatch.Groups["offset"].Value, NumberStyles.HexNumber),
                    offsets
                );
            }
            catch (Exception ex)
            {
                _logger.LogError("Pointer yolu ayrıştırılırken hata oluştu.", ex);
                return (null, 0, null);
            }
        }

        private void LoadProcesses()
        {
            AppendToLog("Çalışan işlemler listeleniyor...");
            var selectedBefore = cmbProcesses.SelectedItem as ProcessInfo;
            _processService.RefreshProcesses();

            var processes = _processService.GetProcesses()
                .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle))
                .Select(p => new ProcessInfo(p))
                .OrderBy(p => p.ProcessName)
                .ToList();

            cmbProcesses.ItemsSource = processes;

            var processToSelect = processes.FirstOrDefault(p =>
                selectedBefore != null && p.Process.Id == selectedBefore.Process.Id) ??
                processes.FirstOrDefault(p =>
                    !string.IsNullOrEmpty(_appSettings.LastProcessName) &&
                    p.ProcessName == _appSettings.LastProcessName);

            if (processToSelect != null)
            {
                cmbProcesses.SelectedItem = processToSelect;
            }

            AppendToLog($"{processes.Count} adet pencereli uygulama bulundu.");
        }

        private void AppendToLog(string message, bool isError = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendToLog(message, isError));
                return;
            }

            string logType = isError ? "[HATA]" : "[BİLGİ]";
            string timestampedMessage = $"{DateTime.Now:HH:mm:ss} {logType} - {message}";
            txtOutput.Items.Add(timestampedMessage);

            if (txtOutput.Items.Count > 0)
            {
                txtOutput.ScrollIntoView(txtOutput.Items[txtOutput.Items.Count - 1]);
            }

            if (txtOutput.Items.Count > 500)
            {
                txtOutput.Items.RemoveAt(0);
            }
        }

        protected virtual void OnTranslatedTextChanged(string newText) =>
            TranslatedTextChanged?.Invoke(newText);
        #endregion
    }
}