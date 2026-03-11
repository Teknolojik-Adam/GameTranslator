using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;

namespace GameTranslatorUltimate
{
    public enum OcrEngineType
    {
        Tesseract,
        WindowsOcr
    }

    public enum TextDetectionMethod
    {
        OpenCV,  // OpenCV ile genel metin algÄ±lama
        East,     // EAST modeli ile metin algÄ±lama
        None     // Metin algÄ±lama yok (tam ekran)
    }

    public enum DnnModelType
    {
        EAST,           // EAST text detection
        CRNN,           // CRNN text recognition
        PaddleOCR,      // PaddleOCR model
        Custom          // Custom DNN model
    }

    public enum TesseractPageSegMode
    {
        Auto = 3,
        SingleLine = 7,
        SparseText = 11
    }

    public class AppSettings : INotifyPropertyChanged
    {
        private const string SettingsFileName = "app_settings.json";
        private ILogger _logger;
        private OcrEngineType _ocrEngine = OcrEngineType.Tesseract;
        public OcrEngineType OcrEngine
        {
            get => _ocrEngine;
            set { if (_ocrEngine != value) { _ocrEngine = value; OnPropertyChanged(); } }
        }

        private TesseractPageSegMode _selectedTesseractPageSegMode = TesseractPageSegMode.SingleLine;
        public TesseractPageSegMode SelectedTesseractPageSegMode
        {
            get => _selectedTesseractPageSegMode;
            set { if (_selectedTesseractPageSegMode != value) { _selectedTesseractPageSegMode = value; OnPropertyChanged(); } }
        }

        private TextDetectionMethod _textDetectionMethod = TextDetectionMethod.East;
        public TextDetectionMethod TextDetectionMethod
        {
            get => _textDetectionMethod;
            set { if (_textDetectionMethod != value) { _textDetectionMethod = value; OnPropertyChanged(); } }
        }

        private bool _enableAutoColorDetection = true;
        public bool EnableAutoColorDetection
        {
            get => _enableAutoColorDetection;
            set { if (_enableAutoColorDetection != value) { _enableAutoColorDetection = value; OnPropertyChanged(); } }
        }

        private bool _enableDynamicThresholding = true;
        public bool EnableDynamicThresholding
        {
            get => _enableDynamicThresholding;
            set { if (_enableDynamicThresholding != value) { _enableDynamicThresholding = value; OnPropertyChanged(); } }
        }

        private int _adaptiveThresholdBlockSize = 11;
        public int AdaptiveThresholdBlockSize
        {
            get => _adaptiveThresholdBlockSize;
            set { if (_adaptiveThresholdBlockSize != value) { ValidatePositiveInteger(value); _adaptiveThresholdBlockSize = value; OnPropertyChanged(); } }
        }

        private int _adaptiveThresholdC = 2;
        public int AdaptiveThresholdC
        {
            get => _adaptiveThresholdC;
            set { if (_adaptiveThresholdC != value) { ValidatePositiveInteger(value); _adaptiveThresholdC = value; OnPropertyChanged(); } }
        }

        private string _lastProcessName = "";
        public string LastProcessName
        {
            get => _lastProcessName;
            set { if (_lastProcessName != value) { _lastProcessName = value; OnPropertyChanged(); } }
        }

        private string _theme = "Light";
        public string Theme
        {
            get => _theme;
            set { if (_theme != value) { _theme = value; OnPropertyChanged(); } }
        }

        private string _language = "tr";
        public string Language
        {
            get => _language;
            set { if (_language != value) { _language = value; OnPropertyChanged(); } }
        }

        private string _targetLanguage = "tr";
        public string TargetLanguage
        {
            get => _targetLanguage;
            set { if (_targetLanguage != value) { _targetLanguage = value; OnPropertyChanged(); } }
        }

        private string _ocrLanguage = "eng";
        public string OcrLanguage
        {
            get => _ocrLanguage;
            set { if (_ocrLanguage != value) { _ocrLanguage = value; OnPropertyChanged(); } }
        }

        private bool _enableOcrColorFilter = true;
        public bool EnableOcrColorFilter
        {
            get => _enableOcrColorFilter;
            set { if (_enableOcrColorFilter != value) { _enableOcrColorFilter = value; OnPropertyChanged(); } }
        }

        private bool _enableSkewCorrection = true;
        public bool EnableSkewCorrection
        {
            get => _enableSkewCorrection;
            set { if (_enableSkewCorrection != value) { _enableSkewCorrection = value; OnPropertyChanged(); } }
        }

        private float _skewCorrectionThreshold = 0.5f;
        public float SkewCorrectionThreshold
        {
            get => _skewCorrectionThreshold;
            set { if (_skewCorrectionThreshold != value) { ValidateSkewCorrectionThreshold(value); _skewCorrectionThreshold = value; OnPropertyChanged(); } }
        }

        private bool _enableHandwritingMode = false;
        public bool EnableHandwritingMode
        {
            get => _enableHandwritingMode;
            set { if (_enableHandwritingMode != value) { _enableHandwritingMode = value; OnPropertyChanged(); } }
        }

        private bool _enableSuperResolution = false;
        public bool EnableSuperResolution
        {
            get => _enableSuperResolution;
            set { if (_enableSuperResolution != value) { _enableSuperResolution = value; OnPropertyChanged(); } }
        }

        private float _superResolutionScale = 2.0f;
        public float SuperResolutionScale
        {
            get => _superResolutionScale;
            set { if (_superResolutionScale != value) { ValidatePositiveFloat(value); _superResolutionScale = value; OnPropertyChanged(); } }
        }

        private int _minImageSizeForSuperResolution = 50;
        public int MinImageSizeForSuperResolution
        {
            get => _minImageSizeForSuperResolution;
            set { if (_minImageSizeForSuperResolution != value) { ValidatePositiveInteger(value); _minImageSizeForSuperResolution = value; OnPropertyChanged(); } }
        }

        private Hotkey _toggleOcrHotkey = new Hotkey(ModifierKeys.Control | ModifierKeys.Shift, Key.O);
        public Hotkey ToggleOcrHotkey
        {
            get => _toggleOcrHotkey;
            set { if (_toggleOcrHotkey != value) { _toggleOcrHotkey = value; OnPropertyChanged(); } }
        }

        private Hotkey _toggleTranslateWindowHotkey = new Hotkey(ModifierKeys.Control | ModifierKeys.Shift, Key.T);
        public Hotkey ToggleTranslateWindowHotkey
        {
            get => _toggleTranslateWindowHotkey;
            set { if (_toggleTranslateWindowHotkey != value) { _toggleTranslateWindowHotkey = value; OnPropertyChanged(); } }
        }

        private Hotkey _switchTranslationServiceHotkey = new Hotkey(ModifierKeys.Control | ModifierKeys.Shift, Key.S);
        public Hotkey SwitchTranslationServiceHotkey
        {
            get => _switchTranslationServiceHotkey;
            set { if (_switchTranslationServiceHotkey != value) { _switchTranslationServiceHotkey = value; OnPropertyChanged(); } }
        }

        private int _pointerSearchMaxDepth = 4;
        public int PointerSearchMaxDepth
        {
            get => _pointerSearchMaxDepth;
            set { if (_pointerSearchMaxDepth != value) { ValidatePositiveInteger(value); _pointerSearchMaxDepth = value; OnPropertyChanged(); } }
        }

        private int _stringReadLength = 256;
        public int StringReadLength
        {
            get => _stringReadLength;
            set { if (_stringReadLength != value) { ValidatePositiveInteger(value); _stringReadLength = value; OnPropertyChanged(); } }
        }

        private bool _showPreviousTranslationsLabel = true;
        public bool ShowPreviousTranslationsLabel
        {
            get => _showPreviousTranslationsLabel;
            set { if (_showPreviousTranslationsLabel != value) { _showPreviousTranslationsLabel = value; OnPropertyChanged(); } }
        }

        private bool _showPreviousTranslations = false;
        public bool ShowPreviousTranslations
        {
            get => _showPreviousTranslations;
            set { if (_showPreviousTranslations != value) { _showPreviousTranslations = value; OnPropertyChanged(); } }
        }

        private int _ocrTickIntervalMs = 500;
        public int OcrTickIntervalMs
        {
            get => _ocrTickIntervalMs;
            set { if (_ocrTickIntervalMs != value) { ValidatePositiveInteger(value); _ocrTickIntervalMs = value; OnPropertyChanged(); } }
        }

        private int _ramTickIntervalMs = 300;
        public int RamTickIntervalMs
        {
            get => _ramTickIntervalMs;
            set { if (_ramTickIntervalMs != value) { ValidatePositiveInteger(value); _ramTickIntervalMs = value; OnPropertyChanged(); } }
        }

        private bool _requireStableOcr = false;
        public bool RequireStableOcr
        {
            get => _requireStableOcr;
            set { if (_requireStableOcr != value) { _requireStableOcr = value; OnPropertyChanged(); } }
        }
        //
        private bool _requireStableRam = false;
        public bool RequireStableRam
        {
            get => _requireStableRam;
            set { if (_requireStableRam != value) { _requireStableRam = value; OnPropertyChanged(); } }
        }

        private int _maxConcurrentTranslations = 10;
        public int MaxConcurrentTranslations
        {
            get => _maxConcurrentTranslations;
            set { if (_maxConcurrentTranslations != value) { ValidatePositiveInteger(value); _maxConcurrentTranslations = value; OnPropertyChanged(); } }
        }

        private int _batchSize = 20;
        public int BatchSize
        {
            get => _batchSize;
            set { if (_batchSize != value) { ValidatePositiveInteger(value); _batchSize = value; OnPropertyChanged(); } }
        }

        private int _batchCollectionWindowMs = 100;
        public int BatchCollectionWindowMs
        {
            get => _batchCollectionWindowMs;
            set { if (_batchCollectionWindowMs != value) { ValidatePositiveInteger(value); _batchCollectionWindowMs = value; OnPropertyChanged(); } }
        }

        private bool _enableBatchProcessing = true;
        public bool EnableBatchProcessing
        {
            get => _enableBatchProcessing;
            set { if (_enableBatchProcessing != value) { _enableBatchProcessing = value; OnPropertyChanged(); } }
        }

        private bool _enableRealtimeBatchProcessing = true;
        public bool EnableRealtimeBatchProcessing
        {
            get => _enableRealtimeBatchProcessing;
            set { if (_enableRealtimeBatchProcessing != value) { _enableRealtimeBatchProcessing = value; OnPropertyChanged(); } }
        }

        private int _realtimeBatchThresholdMs = 200;
        public int RealtimeBatchThresholdMs
        {
            get => _realtimeBatchThresholdMs;
            set { if (_realtimeBatchThresholdMs != value) { ValidatePositiveInteger(value); _realtimeBatchThresholdMs = value; OnPropertyChanged(); } }
        }

        private int _cacheSizeLimit = 10000;
        public int CacheSizeLimit
        {
            get => _cacheSizeLimit;
            set { if (_cacheSizeLimit != value) { ValidatePositiveInteger(value); _cacheSizeLimit = value; OnPropertyChanged(); } }
        }

        private int _cacheCleanupIntervalMinutes = 30;
        public int CacheCleanupIntervalMinutes
        {
            get => _cacheCleanupIntervalMinutes;
            set { if (_cacheCleanupIntervalMinutes != value) { ValidatePositiveInteger(value); _cacheCleanupIntervalMinutes = value; OnPropertyChanged(); } }
        }

        private bool _enableSmartCache = true;
        public bool EnableSmartCache
        {
            get => _enableSmartCache;
            set { if (_enableSmartCache != value) { _enableSmartCache = value; OnPropertyChanged(); } }
        }

        private double _cacheCleanupThreshold = 0.8;
        public double CacheCleanupThreshold
        {
            get => _cacheCleanupThreshold;
            set { if (_cacheCleanupThreshold != value) { ValidateCacheCleanupThreshold(value); _cacheCleanupThreshold = value; OnPropertyChanged(); } }
        }

        private int _translationBatchSize = 20;
        public int TranslationBatchSize
        {
            get => _translationBatchSize;
            set { if (_translationBatchSize != value) { ValidatePositiveInteger(value); _translationBatchSize = value; OnPropertyChanged(); } }
        }

        // Son kullanÄ±lan ayarlar
        private string _lastUsedTranslationService = "";
        public string LastUsedTranslationService
        {
            get => _lastUsedTranslationService;
            set { if (_lastUsedTranslationService != value) { _lastUsedTranslationService = value; OnPropertyChanged(); } }
        }

        private bool _lastOcrState = false;
        public bool LastOcrState
        {
            get => _lastOcrState;
            set { if (_lastOcrState != value) { _lastOcrState = value; OnPropertyChanged(); } }
        }

        private bool _lastRamState = false;
        public bool LastRamState
        {
            get => _lastRamState;
            set { if (_lastRamState != value) { _lastRamState = value; OnPropertyChanged(); } }
        }

        private bool _enableAnomalyDetection = true;
        public bool EnableAnomalyDetection
        {
            get => _enableAnomalyDetection;
            set { if (_enableAnomalyDetection != value) { _enableAnomalyDetection = value; OnPropertyChanged(); } }
        }

        private double _anomalyDetectionThreshold = 0.7;
        public double AnomalyDetectionThreshold
        {
            get => _anomalyDetectionThreshold;
            set { if (_anomalyDetectionThreshold != value) { ValidateAnomalyDetectionThreshold(value); _anomalyDetectionThreshold = value; OnPropertyChanged(); } }
        }

        private bool _logAnomalies = true;
        public bool LogAnomalies
        {
            get => _logAnomalies;
            set { if (_logAnomalies != value) { _logAnomalies = value; OnPropertyChanged(); } }
        }

        private DnnModelType _selectedDnnModel = DnnModelType.EAST;
        public DnnModelType SelectedDnnModel
        {
            get => _selectedDnnModel;
            set { if (_selectedDnnModel != value) { _selectedDnnModel = value; OnPropertyChanged(); } }
        }

        private bool _enableMachineLearning = true;
        public bool EnableMachineLearning
        {
            get => _enableMachineLearning;
            set { if (_enableMachineLearning != value) { _enableMachineLearning = value; OnPropertyChanged(); } }
        }

        private bool _enableTextCorrection = true;
        public bool EnableTextCorrection
        {
            get => _enableTextCorrection;
            set { if (_enableTextCorrection != value) { _enableTextCorrection = value; OnPropertyChanged(); } }
        }

        private bool _enableContextAnalysis = true;
        public bool EnableContextAnalysis
        {
            get => _enableContextAnalysis;
            set { if (_enableContextAnalysis != value) { _enableContextAnalysis = value; OnPropertyChanged(); } }
        }

        private double _mlConfidenceThreshold = 0.8;
        public double MlConfidenceThreshold
        {
            get => _mlConfidenceThreshold;
            set { if (_mlConfidenceThreshold != value) { ValidateMlConfidenceThreshold(value); _mlConfidenceThreshold = value; OnPropertyChanged(); } }
        }

        private string _customDnnModelPath = "";
        public string CustomDnnModelPath
        {
            get => _customDnnModelPath;
            set { if (_customDnnModelPath != value) { _customDnnModelPath = value; OnPropertyChanged(); } }
        }

        // --- Yapay Zeka (Ollama) AyarlarÄ± ---
        private bool _enableOllamaTranslation = false;
        public bool EnableOllamaTranslation
        {
            get => _enableOllamaTranslation;
            set { if (_enableOllamaTranslation != value) { _enableOllamaTranslation = value; OnPropertyChanged(); } }
        }

        private string _ollamaApiUrl = "http://localhost:11434";
        public string OllamaApiUrl
        {
            get => _ollamaApiUrl;
            set { if (_ollamaApiUrl != value) { _ollamaApiUrl = value; OnPropertyChanged(); } }
        }

        private string _ollamaModelName = "llama3:8b"; // VarsayÄ±lan gÃ¼Ã§lÃ¼ bir model
        public string OllamaModelName
        {
            get => _ollamaModelName;
            set { if (_ollamaModelName != value) { _ollamaModelName = value; OnPropertyChanged(); } }
        }

        // Video OCR Settings
        private bool _enableVideoOcr = false;
        public bool EnableVideoOcr
        {
            get => _enableVideoOcr;
            set { if (_enableVideoOcr != value) { _enableVideoOcr = value; OnPropertyChanged(); } }
        }

        private int _videoOcrFrameRate = 30;
        public int VideoOcrFrameRate
        {
            get => _videoOcrFrameRate;
            set { if (_videoOcrFrameRate != value) { ValidatePositiveInteger(value); _videoOcrFrameRate = value; OnPropertyChanged(); } }
        }

        private int _videoOcrWidth = 640;
        public int VideoOcrWidth
        {
            get => _videoOcrWidth;
            set { if (_videoOcrWidth != value) { ValidatePositiveInteger(value); _videoOcrWidth = value; OnPropertyChanged(); } }
        }

        private int _videoOcrHeight = 480;
        public int VideoOcrHeight
        {
            get => _videoOcrHeight;
            set { if (_videoOcrHeight != value) { ValidatePositiveInteger(value); _videoOcrHeight = value; OnPropertyChanged(); } }
        }

        private bool _enableOcrComparison = true;
        public bool EnableOcrComparison
        {
            get => _enableOcrComparison;
            set { if (_enableOcrComparison != value) { _enableOcrComparison = value; OnPropertyChanged(); } }
        }

        private bool _enableOcrAccuracyScoring = false;
        public bool EnableOcrAccuracyScoring
        {
            get => _enableOcrAccuracyScoring;
            set { if (_enableOcrAccuracyScoring != value) { _enableOcrAccuracyScoring = value; OnPropertyChanged(); } }
        }

        private int _videoOcrDeviceIndex = 0;
        public int VideoOcrDeviceIndex
        {
            get => _videoOcrDeviceIndex;
            set { if (_videoOcrDeviceIndex != value) { _videoOcrDeviceIndex = value; OnPropertyChanged(); } }
        }

        private bool _enableOcrRegionDetection = true;
        public bool EnableOcrRegionDetection
        {
            get => _enableOcrRegionDetection;
            set { if (_enableOcrRegionDetection != value) { _enableOcrRegionDetection = value; OnPropertyChanged(); } }
        }

        private double _ocrConfidenceThreshold = 0.7;
        public double OcrConfidenceThreshold
        {
            get => _ocrConfidenceThreshold;
            set { if (_ocrConfidenceThreshold != value) { ValidateOcrConfidenceThreshold(value); _ocrConfidenceThreshold = value; OnPropertyChanged(); } }
        }

        private int _ocrResultHistorySize = 100;
        public int OcrResultHistorySize
        {
            get => _ocrResultHistorySize;
            set { if (_ocrResultHistorySize != value) { ValidatePositiveInteger(value); _ocrResultHistorySize = value; OnPropertyChanged(); } }
        }

        // Manual Color Isolation Settings (HSV values)
        private double _hueMin = 0;
        public double HueMin
        {
            get => _hueMin;
            set { if (_hueMin != value) { ValidateHue(value); _hueMin = value; OnPropertyChanged(); } }
        }

        private double _hueMax = 180;
        public double HueMax
        {
            get => _hueMax;
            set { if (_hueMax != value) { ValidateHue(value); _hueMax = value; OnPropertyChanged(); } }
        }

        private double _saturationMin = 0;
        public double SaturationMin
        {
            get => _saturationMin;
            set { if (_saturationMin != value) { ValidateSaturation(value); _saturationMin = value; OnPropertyChanged(); } }
        }

        private double _saturationMax = 255;
        public double SaturationMax
        {
            get => _saturationMax;
            set { if (_saturationMax != value) { ValidateSaturation(value); _saturationMax = value; OnPropertyChanged(); } }
        }

        private double _valueMin = 200;
        public double ValueMin
        {
            get => _valueMin;
            set { if (_valueMin != value) { ValidateValue(value); _valueMin = value; OnPropertyChanged(); } }
        }

        private double _valueMax = 255;
        public double ValueMax
        {
            get => _valueMax;
            set { if (_valueMax != value) { ValidateValue(value); _valueMax = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // Parameterless ctor used by JSON deserializer. Logger will be assigned by SettingsManager after deserialization.
        public AppSettings()
        {
        }

        // Keep logger-assignment constructor non-public so JSON deserializer uses the parameterless ctor
        internal AppSettings(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // NOT: Ayarlar SettingsManager tarafÄ±ndan yÃ¼kleniyor
            // LoadSettingsFromDisk() burada Ã§aÄŸrÄ±lmÄ±yor (Ã§ift yÃ¼kleme Ã¶nleme)
        }

        // Called by SettingsManager after deserialization to provide a logger instance
        internal void SetLogger(ILogger logger)
        {
            _logger = logger;
        }

        public void SaveSettingsToDisk()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(this, options);
                File.WriteAllText(SettingsFileName, jsonString);
                _logger.LogInformation($"Ayarlar '{SettingsFileName}' dosyasÄ±na baÅŸarÄ±yla kaydedildi.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Ayarlar kaydedilirken hata oluÅŸtu", ex);
            }
        }

        public void ResetToDefaults()
        {
            _ocrEngine = OcrEngineType.Tesseract;
            _textDetectionMethod = TextDetectionMethod.East;
            _enableAutoColorDetection = true;
            _enableDynamicThresholding = true;
            _adaptiveThresholdBlockSize = 11;
            _adaptiveThresholdC = 2;
            _lastProcessName = "";
            _theme = "Light";
            _language = "tr";
            _targetLanguage = "tr";
            _ocrLanguage = "eng";
            _enableOcrColorFilter = true;
            _enableSkewCorrection = true;
            _skewCorrectionThreshold = 0.5f;
            _enableHandwritingMode = false;
            _enableSuperResolution = false;
            _superResolutionScale = 2.0f;
            _minImageSizeForSuperResolution = 50;
            _toggleOcrHotkey = new Hotkey(ModifierKeys.Control | ModifierKeys.Shift, Key.O);
            _toggleTranslateWindowHotkey = new Hotkey(ModifierKeys.Control | ModifierKeys.Shift, Key.T);
            _switchTranslationServiceHotkey = new Hotkey(ModifierKeys.Control | ModifierKeys.Shift, Key.S);
            _pointerSearchMaxDepth = 4;
            _stringReadLength = 256;
            _showPreviousTranslationsLabel = true;
            _showPreviousTranslations = false;
            _ocrTickIntervalMs = 500;
            _ramTickIntervalMs = 300;
            _requireStableOcr = false;
            _requireStableRam = false;
            _maxConcurrentTranslations = 10;
            _batchSize = 20;
            _batchCollectionWindowMs = 100;
            _enableBatchProcessing = true;
            _enableRealtimeBatchProcessing = true;
            _realtimeBatchThresholdMs = 200;
            _cacheSizeLimit = 10000;
            _cacheCleanupIntervalMinutes = 30;
            _enableSmartCache = true;
            _cacheCleanupThreshold = 0.8;
            _translationBatchSize = 20;
            _lastUsedTranslationService = "";
            _lastOcrState = false;
            _lastRamState = false;
            _enableAnomalyDetection = true;
            _anomalyDetectionThreshold = 0.7;
            _logAnomalies = true;
            _selectedDnnModel = DnnModelType.EAST;
            _selectedTesseractPageSegMode = TesseractPageSegMode.SingleLine;
            _enableMachineLearning = true;
            _enableTextCorrection = true;
            _enableContextAnalysis = true;
            _mlConfidenceThreshold = 0.8;
            _customDnnModelPath = "";
            _enableOllamaTranslation = false;
            _ollamaApiUrl = "http://localhost:11434";
            _ollamaModelName = "llama3:8b";
            _enableVideoOcr = false;
            _videoOcrFrameRate = 30;
            _videoOcrWidth = 640;
            _videoOcrHeight = 480;
            _enableOcrComparison = true;
            _enableOcrAccuracyScoring = false;
            _videoOcrDeviceIndex = 0;
            _enableOcrRegionDetection = true;
            _ocrConfidenceThreshold = 0.7;
            _ocrResultHistorySize = 100;
            _hueMin = 0;
            _hueMax = 180;
            _saturationMin = 0;
            _saturationMax = 255;
            _valueMin = 200;
            _valueMax = 255;

            SaveSettingsToDisk();
            _logger.LogInformation("VarsayÄ±lan ayarlar yÃ¼klendi ve kaydedildi.");
        }

        private void ValidatePositiveInteger(int value)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "DeÄŸer pozitif bir tamsayÄ± olmalÄ±dÄ±r.");
            }
        }

        private void ValidatePositiveFloat(float value)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "DeÄŸer pozitif bir ondalÄ±klÄ± sayÄ± olmalÄ±dÄ±r.");
            }
        }

        private void ValidateSkewCorrectionThreshold(float value)
        {
            if (value < 0 || value > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "DeÄŸer 0 ile 1 arasÄ±nda olmalÄ±dÄ±r.");
            }
        }

        private void ValidateCacheCleanupThreshold(double value)
        {
            if (value < 0 || value > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "DeÄŸer 0 ile 1 arasÄ±nda olmalÄ±dÄ±r.");
            }
        }

        private void ValidateAnomalyDetectionThreshold(double value)
        {
            if (value < 0 || value > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "DeÄŸer 0 ile 1 arasÄ±nda olmalÄ±dÄ±r.");
            }
        }

        private void ValidateMlConfidenceThreshold(double value)
        {
            if (value < 0 || value > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "DeÄŸer 0 ile 1 arasÄ±nda olmalÄ±dÄ±r.");
            }
        }

        private void ValidateOcrConfidenceThreshold(double value)
        {
            if (value < 0 || value > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "DeÄŸer 0 ile 1 arasÄ±nda olmalÄ±dÄ±r.");
            }
        }

        private void ValidateHue(double value)
        {
            if (value < 0 || value > 180)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "DeÄŸer 0 ile 180 arasÄ±nda olmalÄ±dÄ±r.");
            }
        }

        private void ValidateSaturation(double value)
        {
            if (value < 0 || value > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "DeÄŸer 0 ile 255 arasÄ±nda olmalÄ±dÄ±r.");
            }
        }

        private void ValidateValue(double value)
        {
            if (value < 0 || value > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "DeÄŸer 0 ile 255 arasÄ±nda olmalÄ±dÄ±r.");
            }
        }
    }
}
