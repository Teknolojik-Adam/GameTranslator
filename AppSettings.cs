using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
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
        OpenCV,
        East,
        None
    }

    public enum DnnModelType
    {
        EAST,
        CRNN,
        PaddleOCR,
        Custom
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
            get
            {
                return _ocrEngine;
            }

            set
            {
                if (_ocrEngine != value)
                {
                    _ocrEngine = value;
                    OnPropertyChanged();
                }
            }
        }

        private TesseractPageSegMode _selectedTesseractPageSegMode =
            TesseractPageSegMode.SingleLine;

        public TesseractPageSegMode SelectedTesseractPageSegMode
        {
            get
            {
                return _selectedTesseractPageSegMode;
            }

            set
            {
                if (_selectedTesseractPageSegMode != value)
                {
                    _selectedTesseractPageSegMode = value;
                    OnPropertyChanged();
                }
            }
        }

        private TextDetectionMethod _textDetectionMethod =
            TextDetectionMethod.East;

        public TextDetectionMethod TextDetectionMethod
        {
            get
            {
                return _textDetectionMethod;
            }

            set
            {
                if (_textDetectionMethod != value)
                {
                    _textDetectionMethod = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableAutoColorDetection = true;

        public bool EnableAutoColorDetection
        {
            get
            {
                return _enableAutoColorDetection;
            }

            set
            {
                if (_enableAutoColorDetection != value)
                {
                    _enableAutoColorDetection = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableDynamicThresholding = true;

        public bool EnableDynamicThresholding
        {
            get
            {
                return _enableDynamicThresholding;
            }

            set
            {
                if (_enableDynamicThresholding != value)
                {
                    _enableDynamicThresholding = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _adaptiveThresholdBlockSize = 11;

        public int AdaptiveThresholdBlockSize
        {
            get
            {
                return _adaptiveThresholdBlockSize;
            }

            set
            {
                if (_adaptiveThresholdBlockSize != value)
                {
                    ValidateAdaptiveThresholdBlockSize(value);

                    _adaptiveThresholdBlockSize = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _adaptiveThresholdC = 2;

        public int AdaptiveThresholdC
        {
            get
            {
                return _adaptiveThresholdC;
            }

            set
            {
                if (_adaptiveThresholdC != value)
                {
                    _adaptiveThresholdC = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _lastProcessName = string.Empty;

        public string LastProcessName
        {
            get
            {
                return _lastProcessName;
            }

            set
            {
                string normalized =
                    value ?? string.Empty;

                if (!string.Equals(
                    _lastProcessName,
                    normalized,
                    StringComparison.Ordinal))
                {
                    _lastProcessName = normalized;
                    OnPropertyChanged();
                }
            }
        }

        private string _theme = "Dark";

        public string Theme
        {
            get
            {
                return _theme;
            }

            set
            {
                string normalized =
                    NormalizeTheme(value);

                if (!string.Equals(
                    _theme,
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                {
                    _theme = normalized;
                    OnPropertyChanged();
                }
            }
        }

        private string _language = "tr";

        public string Language
        {
            get
            {
                return _language;
            }

            set
            {
                string normalized =
                    NormalizeLanguageCode(
                        value,
                        "tr");

                if (!string.Equals(
                    _language,
                    normalized,
                    StringComparison.Ordinal))
                {
                    _language = normalized;
                    OnPropertyChanged();
                }
            }
        }

        private string _targetLanguage = "tr";

        public string TargetLanguage
        {
            get
            {
                return _targetLanguage;
            }

            set
            {
                string normalized =
                    NormalizeLanguageCode(
                        value,
                        "tr");

                if (!string.Equals(
                    _targetLanguage,
                    normalized,
                    StringComparison.Ordinal))
                {
                    _targetLanguage = normalized;
                    OnPropertyChanged();
                }
            }
        }

        private string _ocrLanguage = "eng";

        public string OcrLanguage
        {
            get
            {
                return _ocrLanguage;
            }

            set
            {
                string normalized =
                    NormalizeOcrLanguage(
                        value);

                if (!string.Equals(
                    _ocrLanguage,
                    normalized,
                    StringComparison.Ordinal))
                {
                    _ocrLanguage = normalized;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableOcrColorFilter = true;

        public bool EnableOcrColorFilter
        {
            get
            {
                return _enableOcrColorFilter;
            }

            set
            {
                if (_enableOcrColorFilter != value)
                {
                    _enableOcrColorFilter = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableSkewCorrection = true;

        public bool EnableSkewCorrection
        {
            get
            {
                return _enableSkewCorrection;
            }

            set
            {
                if (_enableSkewCorrection != value)
                {
                    _enableSkewCorrection = value;
                    OnPropertyChanged();
                }
            }
        }

        private float _skewCorrectionThreshold = 0.5f;

        public float SkewCorrectionThreshold
        {
            get
            {
                return _skewCorrectionThreshold;
            }

            set
            {
                if (_skewCorrectionThreshold != value)
                {
                    ValidateSkewCorrectionThreshold(value);

                    _skewCorrectionThreshold = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableHandwritingMode;

        public bool EnableHandwritingMode
        {
            get
            {
                return _enableHandwritingMode;
            }

            set
            {
                if (_enableHandwritingMode != value)
                {
                    _enableHandwritingMode = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableSuperResolution;

        public bool EnableSuperResolution
        {
            get
            {
                return _enableSuperResolution;
            }

            set
            {
                if (_enableSuperResolution != value)
                {
                    _enableSuperResolution = value;
                    OnPropertyChanged();
                }
            }
        }

        private float _superResolutionScale = 2.0f;

        public float SuperResolutionScale
        {
            get
            {
                return _superResolutionScale;
            }

            set
            {
                if (_superResolutionScale != value)
                {
                    ValidatePositiveFloat(value);

                    _superResolutionScale = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _minImageSizeForSuperResolution = 50;

        public int MinImageSizeForSuperResolution
        {
            get
            {
                return _minImageSizeForSuperResolution;
            }

            set
            {
                if (_minImageSizeForSuperResolution != value)
                {
                    ValidatePositiveInteger(value);

                    _minImageSizeForSuperResolution = value;
                    OnPropertyChanged();
                }
            }
        }

        private Hotkey _toggleOcrHotkey =
            new Hotkey(
                ModifierKeys.Control |
                ModifierKeys.Shift,
                Key.O);

        public Hotkey ToggleOcrHotkey
        {
            get
            {
                return _toggleOcrHotkey;
            }

            set
            {
                if (_toggleOcrHotkey != value)
                {
                    _toggleOcrHotkey = value;
                    OnPropertyChanged();
                }
            }
        }

        private Hotkey _toggleTranslateWindowHotkey =
            new Hotkey(
                ModifierKeys.Control |
                ModifierKeys.Shift,
                Key.T);

        public Hotkey ToggleTranslateWindowHotkey
        {
            get
            {
                return _toggleTranslateWindowHotkey;
            }

            set
            {
                if (_toggleTranslateWindowHotkey != value)
                {
                    _toggleTranslateWindowHotkey = value;
                    OnPropertyChanged();
                }
            }
        }

        private Hotkey _switchTranslationServiceHotkey =
            new Hotkey(
                ModifierKeys.Control |
                ModifierKeys.Shift,
                Key.S);

        public Hotkey SwitchTranslationServiceHotkey
        {
            get
            {
                return _switchTranslationServiceHotkey;
            }

            set
            {
                if (_switchTranslationServiceHotkey != value)
                {
                    _switchTranslationServiceHotkey = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _pointerSearchMaxDepth = 4;

        public int PointerSearchMaxDepth
        {
            get
            {
                return _pointerSearchMaxDepth;
            }

            set
            {
                if (_pointerSearchMaxDepth != value)
                {
                    ValidatePositiveInteger(value);

                    _pointerSearchMaxDepth = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _stringReadLength = 256;

        public int StringReadLength
        {
            get
            {
                return _stringReadLength;
            }

            set
            {
                if (_stringReadLength != value)
                {
                    ValidatePositiveInteger(value);

                    _stringReadLength = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _showPreviousTranslationsLabel = true;

        public bool ShowPreviousTranslationsLabel
        {
            get
            {
                return _showPreviousTranslationsLabel;
            }

            set
            {
                if (_showPreviousTranslationsLabel != value)
                {
                    _showPreviousTranslationsLabel = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _showPreviousTranslations;

        public bool ShowPreviousTranslations
        {
            get
            {
                return _showPreviousTranslations;
            }

            set
            {
                if (_showPreviousTranslations != value)
                {
                    _showPreviousTranslations = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _ocrTickIntervalMs = 500;

        public int OcrTickIntervalMs
        {
            get
            {
                return _ocrTickIntervalMs;
            }

            set
            {
                if (_ocrTickIntervalMs != value)
                {
                    ValidatePositiveInteger(value);

                    _ocrTickIntervalMs = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _ramTickIntervalMs = 300;

        public int RamTickIntervalMs
        {
            get
            {
                return _ramTickIntervalMs;
            }

            set
            {
                if (_ramTickIntervalMs != value)
                {
                    ValidatePositiveInteger(value);

                    _ramTickIntervalMs = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _requireStableOcr;

        public bool RequireStableOcr
        {
            get
            {
                return _requireStableOcr;
            }

            set
            {
                if (_requireStableOcr != value)
                {
                    _requireStableOcr = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _requireStableRam;

        public bool RequireStableRam
        {
            get
            {
                return _requireStableRam;
            }

            set
            {
                if (_requireStableRam != value)
                {
                    _requireStableRam = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _maxConcurrentTranslations = 10;

        public int MaxConcurrentTranslations
        {
            get
            {
                return _maxConcurrentTranslations;
            }

            set
            {
                if (_maxConcurrentTranslations != value)
                {
                    ValidatePositiveInteger(value);

                    _maxConcurrentTranslations = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _batchSize = 20;

        public int BatchSize
        {
            get
            {
                return _batchSize;
            }

            set
            {
                if (_batchSize != value)
                {
                    ValidatePositiveInteger(value);

                    _batchSize = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _batchCollectionWindowMs = 100;

        public int BatchCollectionWindowMs
        {
            get
            {
                return _batchCollectionWindowMs;
            }

            set
            {
                if (_batchCollectionWindowMs != value)
                {
                    ValidatePositiveInteger(value);

                    _batchCollectionWindowMs = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableBatchProcessing = true;

        public bool EnableBatchProcessing
        {
            get
            {
                return _enableBatchProcessing;
            }

            set
            {
                if (_enableBatchProcessing != value)
                {
                    _enableBatchProcessing = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableRealtimeBatchProcessing = true;

        public bool EnableRealtimeBatchProcessing
        {
            get
            {
                return _enableRealtimeBatchProcessing;
            }

            set
            {
                if (_enableRealtimeBatchProcessing != value)
                {
                    _enableRealtimeBatchProcessing = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _realtimeBatchThresholdMs = 200;

        public int RealtimeBatchThresholdMs
        {
            get
            {
                return _realtimeBatchThresholdMs;
            }

            set
            {
                if (_realtimeBatchThresholdMs != value)
                {
                    ValidatePositiveInteger(value);

                    _realtimeBatchThresholdMs = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _cacheSizeLimit = 10000;

        public int CacheSizeLimit
        {
            get
            {
                return _cacheSizeLimit;
            }

            set
            {
                if (_cacheSizeLimit != value)
                {
                    ValidatePositiveInteger(value);

                    _cacheSizeLimit = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _cacheCleanupIntervalMinutes = 30;

        public int CacheCleanupIntervalMinutes
        {
            get
            {
                return _cacheCleanupIntervalMinutes;
            }

            set
            {
                if (_cacheCleanupIntervalMinutes != value)
                {
                    ValidatePositiveInteger(value);

                    _cacheCleanupIntervalMinutes = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableSmartCache = true;

        public bool EnableSmartCache
        {
            get
            {
                return _enableSmartCache;
            }

            set
            {
                if (_enableSmartCache != value)
                {
                    _enableSmartCache = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _cacheCleanupThreshold = 0.8;

        public double CacheCleanupThreshold
        {
            get
            {
                return _cacheCleanupThreshold;
            }

            set
            {
                if (_cacheCleanupThreshold != value)
                {
                    ValidateCacheCleanupThreshold(value);

                    _cacheCleanupThreshold = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _translationBatchSize = 20;

        public int TranslationBatchSize
        {
            get
            {
                return _translationBatchSize;
            }

            set
            {
                if (_translationBatchSize != value)
                {
                    ValidatePositiveInteger(value);

                    _translationBatchSize = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _lastUsedTranslationService = string.Empty;

        public string LastUsedTranslationService
        {
            get
            {
                return _lastUsedTranslationService;
            }

            set
            {
                string normalized =
                    value ?? string.Empty;

                if (!string.Equals(
                    _lastUsedTranslationService,
                    normalized,
                    StringComparison.Ordinal))
                {
                    _lastUsedTranslationService = normalized;
                    OnPropertyChanged();
                }
            }
        }

        private bool _lastOcrState;

        public bool LastOcrState
        {
            get
            {
                return _lastOcrState;
            }

            set
            {
                if (_lastOcrState != value)
                {
                    _lastOcrState = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _lastRamState;

        public bool LastRamState
        {
            get
            {
                return _lastRamState;
            }

            set
            {
                if (_lastRamState != value)
                {
                    _lastRamState = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableAnomalyDetection = true;

        public bool EnableAnomalyDetection
        {
            get
            {
                return _enableAnomalyDetection;
            }

            set
            {
                if (_enableAnomalyDetection != value)
                {
                    _enableAnomalyDetection = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _anomalyDetectionThreshold = 0.7;

        public double AnomalyDetectionThreshold
        {
            get
            {
                return _anomalyDetectionThreshold;
            }

            set
            {
                if (_anomalyDetectionThreshold != value)
                {
                    ValidateAnomalyDetectionThreshold(value);

                    _anomalyDetectionThreshold = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _logAnomalies = true;

        public bool LogAnomalies
        {
            get
            {
                return _logAnomalies;
            }

            set
            {
                if (_logAnomalies != value)
                {
                    _logAnomalies = value;
                    OnPropertyChanged();
                }
            }
        }

        private DnnModelType _selectedDnnModel =
            DnnModelType.EAST;

        public DnnModelType SelectedDnnModel
        {
            get
            {
                return _selectedDnnModel;
            }

            set
            {
                if (_selectedDnnModel != value)
                {
                    _selectedDnnModel = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableMachineLearning = true;

        public bool EnableMachineLearning
        {
            get
            {
                return _enableMachineLearning;
            }

            set
            {
                if (_enableMachineLearning != value)
                {
                    _enableMachineLearning = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableTextCorrection = true;

        public bool EnableTextCorrection
        {
            get
            {
                return _enableTextCorrection;
            }

            set
            {
                if (_enableTextCorrection != value)
                {
                    _enableTextCorrection = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableContextAnalysis = true;

        public bool EnableContextAnalysis
        {
            get
            {
                return _enableContextAnalysis;
            }

            set
            {
                if (_enableContextAnalysis != value)
                {
                    _enableContextAnalysis = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _mlConfidenceThreshold = 0.8;

        public double MlConfidenceThreshold
        {
            get
            {
                return _mlConfidenceThreshold;
            }

            set
            {
                if (_mlConfidenceThreshold != value)
                {
                    ValidateMlConfidenceThreshold(value);

                    _mlConfidenceThreshold = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _customDnnModelPath = string.Empty;

        public string CustomDnnModelPath
        {
            get
            {
                return _customDnnModelPath;
            }

            set
            {
                string normalized =
                    value ?? string.Empty;

                if (!string.Equals(
                    _customDnnModelPath,
                    normalized,
                    StringComparison.Ordinal))
                {
                    _customDnnModelPath = normalized;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableOllamaTranslation;

        public bool EnableOllamaTranslation
        {
            get
            {
                return _enableOllamaTranslation;
            }

            set
            {
                if (_enableOllamaTranslation != value)
                {
                    _enableOllamaTranslation = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _ollamaApiUrl =
            "http://localhost:11434";

        public string OllamaApiUrl
        {
            get
            {
                return _ollamaApiUrl;
            }

            set
            {
                string normalized =
                    string.IsNullOrWhiteSpace(value)
                        ? "http://localhost:11434"
                        : value.Trim();

                if (!string.Equals(
                    _ollamaApiUrl,
                    normalized,
                    StringComparison.Ordinal))
                {
                    _ollamaApiUrl = normalized;
                    OnPropertyChanged();
                }
            }
        }

        private string _ollamaModelName =
            "llama3:8b";

        public string OllamaModelName
        {
            get
            {
                return _ollamaModelName;
            }

            set
            {
                string normalized =
                    string.IsNullOrWhiteSpace(value)
                        ? "llama3:8b"
                        : value.Trim();

                if (!string.Equals(
                    _ollamaModelName,
                    normalized,
                    StringComparison.Ordinal))
                {
                    _ollamaModelName = normalized;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableVideoOcr;

        public bool EnableVideoOcr
        {
            get
            {
                return _enableVideoOcr;
            }

            set
            {
                if (_enableVideoOcr != value)
                {
                    _enableVideoOcr = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _videoOcrFrameRate = 30;

        public int VideoOcrFrameRate
        {
            get
            {
                return _videoOcrFrameRate;
            }

            set
            {
                if (_videoOcrFrameRate != value)
                {
                    ValidatePositiveInteger(value);

                    _videoOcrFrameRate = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _videoOcrWidth = 640;

        public int VideoOcrWidth
        {
            get
            {
                return _videoOcrWidth;
            }

            set
            {
                if (_videoOcrWidth != value)
                {
                    ValidatePositiveInteger(value);

                    _videoOcrWidth = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _videoOcrHeight = 480;

        public int VideoOcrHeight
        {
            get
            {
                return _videoOcrHeight;
            }

            set
            {
                if (_videoOcrHeight != value)
                {
                    ValidatePositiveInteger(value);

                    _videoOcrHeight = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableOcrComparison = true;

        public bool EnableOcrComparison
        {
            get
            {
                return _enableOcrComparison;
            }

            set
            {
                if (_enableOcrComparison != value)
                {
                    _enableOcrComparison = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableOcrAccuracyScoring;

        public bool EnableOcrAccuracyScoring
        {
            get
            {
                return _enableOcrAccuracyScoring;
            }

            set
            {
                if (_enableOcrAccuracyScoring != value)
                {
                    _enableOcrAccuracyScoring = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _videoOcrDeviceIndex;

        public int VideoOcrDeviceIndex
        {
            get
            {
                return _videoOcrDeviceIndex;
            }

            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        "Video OCR cihaz indeksi negatif olamaz.");
                }

                if (_videoOcrDeviceIndex != value)
                {
                    _videoOcrDeviceIndex = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableOcrRegionDetection = true;

        public bool EnableOcrRegionDetection
        {
            get
            {
                return _enableOcrRegionDetection;
            }

            set
            {
                if (_enableOcrRegionDetection != value)
                {
                    _enableOcrRegionDetection = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _ocrConfidenceThreshold = 0.7;

        public double OcrConfidenceThreshold
        {
            get
            {
                return _ocrConfidenceThreshold;
            }

            set
            {
                if (_ocrConfidenceThreshold != value)
                {
                    ValidateOcrConfidenceThreshold(value);

                    _ocrConfidenceThreshold = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _ocrResultHistorySize = 100;

        public int OcrResultHistorySize
        {
            get
            {
                return _ocrResultHistorySize;
            }

            set
            {
                if (_ocrResultHistorySize != value)
                {
                    ValidatePositiveInteger(value);

                    _ocrResultHistorySize = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _hueMin;

        public double HueMin
        {
            get
            {
                return _hueMin;
            }

            set
            {
                if (_hueMin != value)
                {
                    ValidateHue(value);

                    _hueMin = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _hueMax = 180;

        public double HueMax
        {
            get
            {
                return _hueMax;
            }

            set
            {
                if (_hueMax != value)
                {
                    ValidateHue(value);

                    _hueMax = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _saturationMin;

        public double SaturationMin
        {
            get
            {
                return _saturationMin;
            }

            set
            {
                if (_saturationMin != value)
                {
                    ValidateSaturation(value);

                    _saturationMin = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _saturationMax = 255;

        public double SaturationMax
        {
            get
            {
                return _saturationMax;
            }

            set
            {
                if (_saturationMax != value)
                {
                    ValidateSaturation(value);

                    _saturationMax = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _valueMin = 200;

        public double ValueMin
        {
            get
            {
                return _valueMin;
            }

            set
            {
                if (_valueMin != value)
                {
                    ValidateValue(value);

                    _valueMin = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _valueMax = 255;

        public double ValueMax
        {
            get
            {
                return _valueMax;
            }

            set
            {
                if (_valueMax != value)
                {
                    ValidateValue(value);

                    _valueMax = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public AppSettings()
        {
        }

        internal AppSettings(
            ILogger logger)
        {
            _logger =
                logger ??
                throw new ArgumentNullException(
                    nameof(logger));
        }

        internal void SetLogger(
            ILogger logger)
        {
            _logger =
                logger;
        }

        protected virtual void OnPropertyChanged(
            [CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler handler =
                PropertyChanged;

            if (handler != null)
            {
                handler(
                    this,
                    new PropertyChangedEventArgs(
                        propertyName));
            }
        }

        public void SaveSettingsToDisk()
        {
            try
            {
                string baseDirectory =
                    AppDomain.CurrentDomain.BaseDirectory;

                if (string.IsNullOrWhiteSpace(
                    baseDirectory))
                {
                    baseDirectory =
                        ".";
                }

                string settingsPath =
                    Path.Combine(
                        baseDirectory,
                        SettingsFileName);

                string tempPath =
                    settingsPath +
                    ".tmp";

                string backupPath =
                    settingsPath +
                    ".bak";

                var options =
                    new JsonSerializerOptions
                    {
                        WriteIndented =
                            true
                    };

                string jsonString =
                    JsonSerializer.Serialize(
                        this,
                        options);

                File.WriteAllText(
                    tempPath,
                    jsonString,
                    new UTF8Encoding(false));

                if (File.Exists(
                    settingsPath))
                {
                    try
                    {
                        File.Replace(
                            tempPath,
                            settingsPath,
                            backupPath,
                            true);
                    }
                    catch
                    {
                        File.Copy(
                            tempPath,
                            settingsPath,
                            true);

                        File.Delete(
                            tempPath);
                    }
                }
                else
                {
                    File.Move(
                        tempPath,
                        settingsPath);
                }

                if (_logger != null)
                {
                    _logger.LogInformation(
                        $"Ayarlar '{SettingsFileName}' dosyasına başarıyla kaydedildi.");
                }
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    _logger.LogError(
                        "Ayarlar kaydedilirken hata oluştu.",
                        ex);
                }
            }
        }

        public void ResetToDefaults()
        {
            OcrEngine =
                OcrEngineType.Tesseract;

            SelectedTesseractPageSegMode =
                TesseractPageSegMode.SingleLine;

            TextDetectionMethod =
                TextDetectionMethod.East;

            EnableAutoColorDetection =
                true;

            EnableDynamicThresholding =
                true;

            AdaptiveThresholdBlockSize =
                11;

            AdaptiveThresholdC =
                2;

            LastProcessName =
                string.Empty;

            Theme =
                "Dark";

            Language =
                "tr";

            TargetLanguage =
                "tr";

            OcrLanguage =
                "eng";

            EnableOcrColorFilter =
                true;

            EnableSkewCorrection =
                true;

            SkewCorrectionThreshold =
                0.5f;

            EnableHandwritingMode =
                false;

            EnableSuperResolution =
                false;

            SuperResolutionScale =
                2.0f;

            MinImageSizeForSuperResolution =
                50;

            ToggleOcrHotkey =
                new Hotkey(
                    ModifierKeys.Control |
                    ModifierKeys.Shift,
                    Key.O);

            ToggleTranslateWindowHotkey =
                new Hotkey(
                    ModifierKeys.Control |
                    ModifierKeys.Shift,
                    Key.T);

            SwitchTranslationServiceHotkey =
                new Hotkey(
                    ModifierKeys.Control |
                    ModifierKeys.Shift,
                    Key.S);

            PointerSearchMaxDepth =
                4;

            StringReadLength =
                256;

            ShowPreviousTranslationsLabel =
                true;

            ShowPreviousTranslations =
                false;

            OcrTickIntervalMs =
                500;

            RamTickIntervalMs =
                300;

            RequireStableOcr =
                false;

            RequireStableRam =
                false;

            MaxConcurrentTranslations =
                10;

            BatchSize =
                20;

            BatchCollectionWindowMs =
                100;

            EnableBatchProcessing =
                true;

            EnableRealtimeBatchProcessing =
                true;

            RealtimeBatchThresholdMs =
                200;

            CacheSizeLimit =
                10000;

            CacheCleanupIntervalMinutes =
                30;

            EnableSmartCache =
                true;

            CacheCleanupThreshold =
                0.8;

            TranslationBatchSize =
                20;

            LastUsedTranslationService =
                string.Empty;

            LastOcrState =
                false;

            LastRamState =
                false;

            EnableAnomalyDetection =
                true;

            AnomalyDetectionThreshold =
                0.7;

            LogAnomalies =
                true;

            SelectedDnnModel =
                DnnModelType.EAST;

            EnableMachineLearning =
                true;

            EnableTextCorrection =
                true;

            EnableContextAnalysis =
                true;

            MlConfidenceThreshold =
                0.8;

            CustomDnnModelPath =
                string.Empty;

            EnableOllamaTranslation =
                false;

            OllamaApiUrl =
                "http://localhost:11434";

            OllamaModelName =
                "llama3:8b";

            EnableVideoOcr =
                false;

            VideoOcrFrameRate =
                30;

            VideoOcrWidth =
                640;

            VideoOcrHeight =
                480;

            EnableOcrComparison =
                true;

            EnableOcrAccuracyScoring =
                false;

            VideoOcrDeviceIndex =
                0;

            EnableOcrRegionDetection =
                true;

            OcrConfidenceThreshold =
                0.7;

            OcrResultHistorySize =
                100;

            HueMin =
                0;

            HueMax =
                180;

            SaturationMin =
                0;

            SaturationMax =
                255;

            ValueMin =
                200;

            ValueMax =
                255;

            if (_logger != null)
            {
                _logger.LogInformation(
                    "Varsayılan ayarlar uygulandı.");
            }
        }

        private static string NormalizeLanguageCode(
            string value,
            string defaultValue)
        {
            if (string.IsNullOrWhiteSpace(
                value))
            {
                return defaultValue;
            }

            return value
                .Trim()
                .Replace(
                    '_',
                    '-')
                .ToLowerInvariant();
        }

        private static string NormalizeOcrLanguage(
            string value)
        {
            if (string.IsNullOrWhiteSpace(
                value))
            {
                return "eng";
            }

            string normalized =
                value
                    .Trim()
                    .Replace(
                        '-',
                        '_')
                    .ToLowerInvariant();

            switch (normalized)
            {
                case "en":
                case "en_us":
                case "en_gb":
                case "english":
                    return "eng";

                case "tr":
                case "tr_tr":
                case "turkish":
                    return "tur";

                case "ja":
                case "ja_jp":
                case "japanese":
                    return "jpn";

                case "zh":
                case "zh_cn":
                case "chinese":
                    return "chi_sim";

                case "ko":
                case "ko_kr":
                case "korean":
                    return "kor";

                case "ru":
                case "ru_ru":
                case "russian":
                    return "rus";

                default:
                    return normalized;
            }
        }

        private static string NormalizeTheme(
            string value)
        {
            if (string.IsNullOrWhiteSpace(
                value))
            {
                return "Dark";
            }

            string normalized =
                value.Trim();

            if (string.Equals(
                normalized,
                "Light",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Light";
            }

            return "Dark";
        }

        private static void ValidatePositiveInteger(
            int value)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Değer pozitif bir tamsayı olmalıdır.");
            }
        }

        private static void ValidateAdaptiveThresholdBlockSize(
            int value)
        {
            if (value <= 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Adaptive Threshold block size 1'den büyük olmalıdır.");
            }

            if (value % 2 == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Adaptive Threshold block size tek sayı olmalıdır.");
            }
        }

        private static void ValidatePositiveFloat(
            float value)
        {
            if (float.IsNaN(value) ||
                float.IsInfinity(value) ||
                value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Değer pozitif ve geçerli bir ondalıklı sayı olmalıdır.");
            }
        }

        private static void ValidateSkewCorrectionThreshold(
            float value)
        {
            if (float.IsNaN(value) ||
                float.IsInfinity(value) ||
                value < 0 ||
                value > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Değer 0 ile 1 arasında olmalıdır.");
            }
        }

        private static void ValidateCacheCleanupThreshold(
            double value)
        {
            ValidateZeroToOne(
                value);
        }

        private static void ValidateAnomalyDetectionThreshold(
            double value)
        {
            ValidateZeroToOne(
                value);
        }

        private static void ValidateMlConfidenceThreshold(
            double value)
        {
            ValidateZeroToOne(
                value);
        }

        private static void ValidateOcrConfidenceThreshold(
            double value)
        {
            ValidateZeroToOne(
                value);
        }

        private static void ValidateZeroToOne(
            double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value < 0 ||
                value > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Değer 0 ile 1 arasında olmalıdır.");
            }
        }

        private static void ValidateHue(
            double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value < 0 ||
                value > 180)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Değer 0 ile 180 arasında olmalıdır.");
            }
        }

        private static void ValidateSaturation(
            double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value < 0 ||
                value > 255)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Değer 0 ile 255 arasında olmalıdır.");
            }
        }

        private static void ValidateValue(
            double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value < 0 ||
                value > 255)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Değer 0 ile 255 arasında olmalıdır.");
            }
        }
    }
}