using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;

namespace P5S_ceviri
{
    public partial class OutputWindow : Window
    {
        private readonly MainWindow _mainWindow;
        private bool _isSelectionMode = false;
        private Point _startPoint;

        // Ana pencereye seçilen bölgeyi bildirmek için event
        public event Action<System.Drawing.Rectangle> RegionSelected;

        public OutputWindow(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
            this.KeyDown += Window_KeyDown;
        }

        #region Seçim Modu Mantığı

        public void EnterSelectionMode()
        {
            _isSelectionMode = true;

            // Pencereyi tam ekran, yarı saydam yap
            this.WindowState = WindowState.Maximized;
            this.Background = System.Windows.Media.Brushes.Transparent; //arka planı yarı saydam

            // Normal görünümü gizle, seçim i göster
            DisplayBorder.Visibility = Visibility.Collapsed;
            SelectionCanvas.Visibility = Visibility.Visible;
            this.Cursor = Cursors.Cross;
        }

        private void ExitSelectionMode()
        {
            _isSelectionMode = false;

            // Pencereyi normale döndür
            this.WindowState = WindowState.Normal;
            this.SizeToContent = SizeToContent.WidthAndHeight; // Boyutu içeriğe göre ayarla
            this.Background = System.Windows.Media.Brushes.Transparent;

            // Seçim tuvalini gizle, normal görünümü göster
            SelectionCanvas.Visibility = Visibility.Collapsed;
            DisplayBorder.Visibility = Visibility.Visible;
            this.Cursor = Cursors.Arrow;
        }

        private void SelectionCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition(this);
            SelectionRectangle.SetValue(Canvas.LeftProperty, _startPoint.X);
            SelectionRectangle.SetValue(Canvas.TopProperty, _startPoint.Y);
            SelectionRectangle.Width = 0;
            SelectionRectangle.Height = 0;
            SelectionRectangle.Visibility = Visibility.Visible;
        }

        private void SelectionCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var currentPoint = e.GetPosition(this);
            var x = Math.Min(_startPoint.X, currentPoint.X);
            var y = Math.Min(_startPoint.Y, currentPoint.Y);
            var width = Math.Abs(_startPoint.X - currentPoint.X);
            var height = Math.Abs(_startPoint.Y - currentPoint.Y);
            SelectionRectangle.SetValue(Canvas.LeftProperty, x);
            SelectionRectangle.SetValue(Canvas.TopProperty, y);
            SelectionRectangle.Width = width;
            SelectionRectangle.Height = height;
        }

        private void SelectionCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var dpiScale = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
            var x = (int)(Canvas.GetLeft(SelectionRectangle) * dpiScale.M11);
            var y = (int)(Canvas.GetTop(SelectionRectangle) * dpiScale.M22);
            var width = (int)(SelectionRectangle.Width * dpiScale.M11);
            var height = (int)(SelectionRectangle.Height * dpiScale.M22);

            if (width > 5 && height > 5)
            {
                RegionSelected?.Invoke(new System.Drawing.Rectangle(x, y, width, height));
            }

            ExitSelectionMode();
        }

        #endregion

        #region Normal Pencere İşlevleri

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _mainWindow.TranslatedTextChanged += OnMainWindowTranslatedTextChanged;
            this.Closing += (s, ev) => _mainWindow.TranslatedTextChanged -= OnMainWindowTranslatedTextChanged;
        }

        private void OnMainWindowTranslatedTextChanged(string newText)
        {
            Dispatcher.Invoke(() => txtOutputDisplay.Text = newText);
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isSelectionMode)
            {
                DragMove();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (_isSelectionMode && e.Key == Key.Escape)
            {
                ExitSelectionMode();
            }
        }
        #endregion
    }
}