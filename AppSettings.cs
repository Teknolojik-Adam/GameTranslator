using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace P5S_ceviri
{
    public enum OcrEngineType
    {
        Tesseract,
        WindowsOcr
    }

    public enum TextDetectionMethod
    {
        OpenCV,  // OpenCV ile genel metin algılama
        East,     // EAST modeli ile metin algılama
        None     // Metin algılama yok (tam ekran)
    }

    public enum DnnModelType
    {
        EAST,           // EAST text detection
        CRNN,           // CRNN text recognition
        PaddleOCR,      // PaddleOCR model
        Custom          // Custom DNN model
    }

    public class AppSettings : INotifyPropertyChanged
    {
        private OcrEngineType _ocrEngine = OcrEngineType.Tesseract;
        public OcrEngineType OcrEngine
        {
            get => _ocrEngine;
            set { if (_ocrEngine != value) { _ocrEngine = value; OnPropertyChanged(); } }
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
            set { if (_adaptiveThresholdBlockSize != value) { _adaptiveThresholdBlockSize = value; OnPropertyChanged(); } }
        }

        private int _adaptiveThresholdC = 2;
        public int AdaptiveThresholdC
        {
            get => _adaptiveThresholdC;
            set { if (_adaptiveThresholdC != value) { _adaptiveThresholdC = value; OnPropertyChanged(); } }
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
            set { if (_skewCorrectionThreshold != value) { _skewCorrectionThreshold = value; OnPropertyChanged(); } }
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
            set { if (_superResolutionScale != value) { _superResolutionScale = value; OnPropertyChanged(); } }
        }

        private int _minImageSizeForSuperResolution = 50;
        public int MinImageSizeForSuperResolution
        {
            get => _minImageSizeForSuperResolution;
            set { if (_minImageSizeForSuperResolution != value) { _minImageSizeForSuperResolution = value; OnPropertyChanged(); } }
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
            set { if (_pointerSearchMaxDepth != value) { _pointerSearchMaxDepth = value; OnPropertyChanged(); } }
        }

        private int _stringReadLength = 256;
        public int StringReadLength
        {
            get => _stringReadLength;
            set { if (_stringReadLength != value) { _stringReadLength = value; OnPropertyChanged(); } }
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
            set { if (_ocrTickIntervalMs != value) { _ocrTickIntervalMs = value; OnPropertyChanged(); } }
        }

        private int _ramTickIntervalMs = 300;
        public int RamTickIntervalMs
        {
            get => _ramTickIntervalMs;
            set { if (_ramTickIntervalMs != value) { _ramTickIntervalMs = value; OnPropertyChanged(); } }
        }

        private bool _requireStableOcr = false;
        public bool RequireStableOcr
        {
            get => _requireStableOcr;
            set { if (_requireStableOcr != value) { _requireStableOcr = value; OnPropertyChanged(); } }
        }

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
            set { if (_maxConcurrentTranslations != value) { _maxConcurrentTranslations = value; OnPropertyChanged(); } }
        }

        private int _batchSize = 20;
        public int BatchSize
        {
            get => _batchSize;
            set { if (_batchSize != value) { _batchSize = value; OnPropertyChanged(); } }
        }

        private int _batchCollectionWindowMs = 100;
        public int BatchCollectionWindowMs
        {
            get => _batchCollectionWindowMs;
            set { if (_batchCollectionWindowMs != value) { _batchCollectionWindowMs = value; OnPropertyChanged(); } }
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
            set { if (_realtimeBatchThresholdMs != value) { _realtimeBatchThresholdMs = value; OnPropertyChanged(); } }
        }

        private int _cacheSizeLimit = 10000;
        public int CacheSizeLimit
        {
            get => _cacheSizeLimit;
            set { if (_cacheSizeLimit != value) { _cacheSizeLimit = value; OnPropertyChanged(); } }
        }

        private int _cacheCleanupIntervalMinutes = 30;
        public int CacheCleanupIntervalMinutes
        {
            get => _cacheCleanupIntervalMinutes;
            set { if (_cacheCleanupIntervalMinutes != value) { _cacheCleanupIntervalMinutes = value; OnPropertyChanged(); } }
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
            set { if (_cacheCleanupThreshold != value) { _cacheCleanupThreshold = value; OnPropertyChanged(); } }
        }

        private int _translationBatchSize = 20;
        public int TranslationBatchSize
        {
            get => _translationBatchSize;
            set { if (_translationBatchSize != value) { _translationBatchSize = value; OnPropertyChanged(); } }
        }

        // Son kullanılan ayarlar
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
            set { if (_anomalyDetectionThreshold != value) { _anomalyDetectionThreshold = value; OnPropertyChanged(); } }
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
            set { if (_mlConfidenceThreshold != value) { _mlConfidenceThreshold = value; OnPropertyChanged(); } }
        }

        private string _customDnnModelPath = "";
        public string CustomDnnModelPath
        {
            get => _customDnnModelPath;
            set { if (_customDnnModelPath != value) { _customDnnModelPath = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}