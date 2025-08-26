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

    public class AppSettings : INotifyPropertyChanged
    {
       
        private OcrEngineType _ocrEngine = OcrEngineType.Tesseract;
        public OcrEngineType OcrEngine
        {
            get => _ocrEngine;
            set { if (_ocrEngine != value) { _ocrEngine = value; OnPropertyChanged(); } }
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

        // OCR Language
        private string _ocrLanguage = "eng";
        public string OcrLanguage
        {
            get => _ocrLanguage;
            set { if (_ocrLanguage != value) { _ocrLanguage = value; OnPropertyChanged(); } }
        }

        // OCR Color Filter
        private bool _enableOcrColorFilter = true;
        public bool EnableOcrColorFilter
        {
            get => _enableOcrColorFilter;
            set { if (_enableOcrColorFilter != value) { _enableOcrColorFilter = value; OnPropertyChanged(); } }
        }

        // Hotkey Settings
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

        // Memory Reading Settings
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

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}