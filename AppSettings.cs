using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace P5S_ceviri 
{
    public class AppSettings : INotifyPropertyChanged
    {

        // Son seçilen iþlem (uygulama) adý
        private string _lastProcessName = "";
        public string LastProcessName
        {
            get => _lastProcessName;
            set
            {
                if (_lastProcessName != value)
                {
                    _lastProcessName = value;
                    OnPropertyChanged();
                }
            }
        }

        // Uygulama temasý ("Light" veya "Dark")
        private string _theme = "Light"; // Varsayýlan tema
        public string Theme
        {
            get => _theme;
            set
            {
                if (_theme != value)
                {
                    _theme = value;
                    OnPropertyChanged();
                }
            }
        }
        // INotifyPropertyChanged implementasyonu
        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}