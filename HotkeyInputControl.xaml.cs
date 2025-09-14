using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace P5S_ceviri
{
    public partial class HotkeyInputControl : UserControl
    {
        public static readonly DependencyProperty HotkeyProperty =
            DependencyProperty.Register("Hotkey", typeof(Hotkey), typeof(HotkeyInputControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHotkeyChanged));

        private static void OnHotkeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is HotkeyInputControl control)
            {
                control.UpdateText();
            }
        }

        public Hotkey Hotkey
        {
            get { return (Hotkey)GetValue(HotkeyProperty); }
            set { SetValue(HotkeyProperty, value); }
        }

        public HotkeyInputControl()
        {
            InitializeComponent();
            UpdateText();
        }

        private void UpdateText()
        {
            if (Hotkey != null)
            {
                HotkeyTextBox.Text = Hotkey.ToString();
            }
        }

        private void HotkeyTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            HotkeyTextBox.Text = "Yeni kısayolu bekliyor...";
        }

        private void HotkeyTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdateText();
        }

        private void HotkeyTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            Key key = e.Key;

            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LWin || key == Key.RWin)
            {
                return;
            }

            ModifierKeys modifiers = Keyboard.Modifiers;

            if (modifiers == ModifierKeys.None && key == Key.None)
            {
                return;
            }

            Hotkey = new Hotkey(modifiers, key);
            UpdateText();
            Keyboard.ClearFocus();
            (sender as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }
}