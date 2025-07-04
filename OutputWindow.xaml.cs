using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace P5S_ceviri
{
    public partial class OutputWindow : Window
    {
        private readonly MainWindow _mainWindow;

        public OutputWindow(MainWindow mainWindow)
        {
            InitializeComponent();

          
            _mainWindow = mainWindow;

            // MainWindow'den gelen TranslatedTextChanged olayını dinle
            _mainWindow.TranslatedTextChanged += OnMainWindowTranslatedTextChanged;

            this.Closing += (s, e) =>
            {
                _mainWindow.TranslatedTextChanged -= OnMainWindowTranslatedTextChanged;
            };
        }

     
        private void OnMainWindowTranslatedTextChanged(string newText)
        {
         
            Dispatcher.Invoke(() =>
            {
                // Yalnızca çevrilmiş metni göster
                txtOutputDisplay.Text = newText;
            });
        }

       
        public void UpdateText(string originalText, string translatedText)
        {
            Dispatcher.Invoke(() =>
            {
                // Hem orijinal metni hem de çevrilmiş metni göstermek için
                txtOutputDisplay.Text = $"Orijinal: {originalText}\n\nÇeviri: {translatedText}";
            });
        }

        // Pencereyi sürüklemek için
        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                
                if (WindowState == WindowState.Maximized)
                    WindowState = WindowState.Normal;
                else
                    WindowState = WindowState.Maximized;
            }
            else
            {
             
                DragMove();
            }
        }

        private void txtOutputDisplay_TextChanged(object sender, TextChangedEventArgs e)
        {
            
            (sender as TextBox)?.ScrollToEnd();
        }
    }
}