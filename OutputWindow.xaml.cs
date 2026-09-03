using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GameTranslatorUltimate
{
    public partial class OutputWindow : Window
    {
        private readonly MainWindow _mainWindow;

        private bool _isSelectionMode;
        private bool _isSelecting;

        private Point _startPoint;

        private double _savedLeft;
        private double _savedTop;
        private double _savedWidth;
        private double _savedHeight;
        private SizeToContent _savedSizeToContent;
        private ResizeMode _savedResizeMode;

        public event Action<System.Drawing.Rectangle> RegionSelected;

        public OutputWindow(MainWindow mainWindow)
        {
            InitializeComponent();

            _mainWindow =
                mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        }

        public void EnterSelectionMode()
        {
            if (_isSelectionMode)
                return;

            SaveWindowState();

            _isSelectionMode = true;
            _isSelecting = false;

            SizeToContent = SizeToContent.Manual;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;

            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            DisplayBorder.Visibility =
                Visibility.Collapsed;

            SelectionCanvas.Visibility =
                Visibility.Visible;

            SelectionRectangle.Visibility =
                Visibility.Collapsed;

            Cursor =
                Cursors.Cross;

            Activate();
            Focus();
        }

        private void ExitSelectionMode()
        {
            if (!_isSelectionMode)
                return;

            _isSelectionMode = false;
            _isSelecting = false;

            SelectionRectangle.Visibility =
                Visibility.Collapsed;

            SelectionCanvas.Visibility =
                Visibility.Collapsed;

            DisplayBorder.Visibility =
                Visibility.Visible;

            Cursor =
                Cursors.Arrow;

            RestoreWindowState();
        }

        private void SelectionCanvas_MouseDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (!_isSelectionMode)
                return;

            _isSelecting = true;

            _startPoint =
                e.GetPosition(SelectionCanvas);

            Canvas.SetLeft(
                SelectionRectangle,
                _startPoint.X);

            Canvas.SetTop(
                SelectionRectangle,
                _startPoint.Y);

            SelectionRectangle.Width = 0;
            SelectionRectangle.Height = 0;

            SelectionRectangle.Visibility =
                Visibility.Visible;

            SelectionCanvas.CaptureMouse();

            e.Handled = true;
        }

        private void SelectionCanvas_MouseMove(
            object sender,
            MouseEventArgs e)
        {
            if (!_isSelectionMode ||
                !_isSelecting ||
                e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Point currentPoint =
                e.GetPosition(SelectionCanvas);

            double x =
                Math.Min(
                    _startPoint.X,
                    currentPoint.X);

            double y =
                Math.Min(
                    _startPoint.Y,
                    currentPoint.Y);

            double width =
                Math.Abs(
                    currentPoint.X -
                    _startPoint.X);

            double height =
                Math.Abs(
                    currentPoint.Y -
                    _startPoint.Y);

            Canvas.SetLeft(
                SelectionRectangle,
                x);

            Canvas.SetTop(
                SelectionRectangle,
                y);

            SelectionRectangle.Width =
                width;

            SelectionRectangle.Height =
                height;
        }

        private void SelectionCanvas_MouseUp(
            object sender,
            MouseButtonEventArgs e)
        {
            if (!_isSelectionMode ||
                !_isSelecting)
            {
                return;
            }

            _isSelecting = false;

            SelectionCanvas.ReleaseMouseCapture();

            double left =
                Canvas.GetLeft(
                    SelectionRectangle);

            double top =
                Canvas.GetTop(
                    SelectionRectangle);

            double width =
                SelectionRectangle.Width;

            double height =
                SelectionRectangle.Height;

            if (double.IsNaN(left) ||
                double.IsNaN(top) ||
                width < 5 ||
                height < 5)
            {
                ExitSelectionMode();
                return;
            }

            Point screenTopLeft =
                SelectionCanvas.PointToScreen(
                    new Point(left, top));

            Point screenBottomRight =
                SelectionCanvas.PointToScreen(
                    new Point(
                        left + width,
                        top + height));

            int x =
                (int)Math.Round(
                    Math.Min(
                        screenTopLeft.X,
                        screenBottomRight.X));

            int y =
                (int)Math.Round(
                    Math.Min(
                        screenTopLeft.Y,
                        screenBottomRight.Y));

            int pixelWidth =
                (int)Math.Round(
                    Math.Abs(
                        screenBottomRight.X -
                        screenTopLeft.X));

            int pixelHeight =
                (int)Math.Round(
                    Math.Abs(
                        screenBottomRight.Y -
                        screenTopLeft.Y));

            ExitSelectionMode();

            if (pixelWidth <= 5 ||
                pixelHeight <= 5)
            {
                return;
            }

            RegionSelected?.Invoke(
                new System.Drawing.Rectangle(
                    x,
                    y,
                    pixelWidth,
                    pixelHeight));
        }

        private void Window_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            _mainWindow.TranslatedTextChanged +=
                OnMainWindowTranslatedTextChanged;
        }

        private void Window_Closing(
            object sender,
            CancelEventArgs e)
        {
            _mainWindow.TranslatedTextChanged -=
                OnMainWindowTranslatedTextChanged;

            if (SelectionCanvas.IsMouseCaptured)
            {
                SelectionCanvas.ReleaseMouseCapture();
            }
        }

        private void OnMainWindowTranslatedTextChanged(
            string newText)
        {
            if (Dispatcher.HasShutdownStarted ||
                Dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (Dispatcher.CheckAccess())
            {
                txtOutputDisplay.Text =
                    newText ?? string.Empty;

                return;
            }

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    txtOutputDisplay.Text =
                        newText ?? string.Empty;
                }));
        }

        private void Border_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (_isSelectionMode ||
                e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            try
            {
                DragMove();
            }
            catch
            {
            }
        }

        private void Window_KeyDown(
            object sender,
            KeyEventArgs e)
        {
            if (!_isSelectionMode ||
                e.Key != Key.Escape)
            {
                return;
            }

            ExitSelectionMode();

            e.Handled = true;
        }

        private void SaveWindowState()
        {
            _savedLeft = Left;
            _savedTop = Top;
            _savedWidth = ActualWidth;
            _savedHeight = ActualHeight;
            _savedSizeToContent = SizeToContent;
            _savedResizeMode = ResizeMode;
        }

        private void RestoreWindowState()
        {
            WindowState =
                WindowState.Normal;

            SizeToContent =
                _savedSizeToContent;

            ResizeMode =
                _savedResizeMode;

            Left =
                _savedLeft;

            Top =
                _savedTop;

            if (_savedSizeToContent ==
                SizeToContent.Manual)
            {
                if (_savedWidth > 0)
                    Width = _savedWidth;

                if (_savedHeight > 0)
                    Height = _savedHeight;
            }
        }
    }
}