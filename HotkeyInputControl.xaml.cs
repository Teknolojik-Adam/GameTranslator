using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GameTranslatorUltimate
{
    public partial class HotkeyInputControl : UserControl
    {
        public static readonly DependencyProperty HotkeyProperty =
            DependencyProperty.Register(
                "Hotkey",
                typeof(Hotkey),
                typeof(HotkeyInputControl),
                new FrameworkPropertyMetadata(
                    null,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnHotkeyChanged));

        public HotkeyInputControl()
        {
            InitializeComponent();
            UpdateText();
        }

        public Hotkey Hotkey
        {
            get
            {
                return (Hotkey)GetValue(
                    HotkeyProperty);
            }

            set
            {
                SetValue(
                    HotkeyProperty,
                    value);
            }
        }

        private static void OnHotkeyChanged(
            DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            HotkeyInputControl control =
                d as HotkeyInputControl;

            if (control != null)
            {
                control.UpdateText();
            }
        }

        private void UpdateText()
        {
            if (Hotkey != null &&
                Hotkey.IsValid)
            {
                HotkeyTextBox.Text =
                    Hotkey.ToString();
            }
            else
            {
                HotkeyTextBox.Text =
                    string.Empty;
            }
        }

        private void HotkeyTextBox_GotFocus(
            object sender,
            RoutedEventArgs e)
        {
            HotkeyTextBox.Text =
                "Yeni kısayolu bekliyor...";
        }

        private void HotkeyTextBox_LostFocus(
            object sender,
            RoutedEventArgs e)
        {
            UpdateText();
        }

        private void HotkeyTextBox_KeyDown(
            object sender,
            KeyEventArgs e)
        {
            e.Handled =
                true;

            Key key =
                GetActualKey(e);

            if (key == Key.Escape)
            {
                UpdateText();
                MoveFocusNext(sender);
                return;
            }

            if (key == Key.Delete ||
                key == Key.Back)
            {
                Hotkey =
                    null;

                UpdateText();
                MoveFocusNext(sender);
                return;
            }

            if (IsModifierKey(key))
            {
                return;
            }

            if (key == Key.None)
            {
                return;
            }

            ModifierKeys modifiers =
                Keyboard.Modifiers;

            Hotkey newHotkey =
                new Hotkey(
                    modifiers,
                    key);

            if (!newHotkey.IsValid)
            {
                UpdateText();
                return;
            }

            Hotkey =
                newHotkey;

            UpdateText();

            MoveFocusNext(
                sender);
        }

        private static Key GetActualKey(
            KeyEventArgs e)
        {
            if (e == null)
            {
                return Key.None;
            }

            if (e.Key == Key.System)
            {
                return e.SystemKey;
            }

            if (e.Key == Key.ImeProcessed)
            {
                return e.ImeProcessedKey;
            }

            if (e.Key == Key.DeadCharProcessed)
            {
                return e.DeadCharProcessedKey;
            }

            return e.Key;
        }

        private static bool IsModifierKey(
            Key key)
        {
            return
                key == Key.LeftCtrl ||
                key == Key.RightCtrl ||
                key == Key.LeftShift ||
                key == Key.RightShift ||
                key == Key.LeftAlt ||
                key == Key.RightAlt ||
                key == Key.LWin ||
                key == Key.RWin;
        }

        private static void MoveFocusNext(
            object sender)
        {
            UIElement element =
                sender as UIElement;

            if (element == null)
            {
                Keyboard.ClearFocus();
                return;
            }

            element.MoveFocus(
                new TraversalRequest(
                    FocusNavigationDirection.Next));
        }
    }
}