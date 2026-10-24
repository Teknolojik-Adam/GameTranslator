using System;
using System.Text;
using System.Windows.Input;

namespace GameTranslatorUltimate
{
    public sealed class Hotkey : IEquatable<Hotkey>
    {
        public ModifierKeys Modifiers { get; set; }

        public Key Key { get; set; }

        public bool IsValid
        {
            get
            {
                return
                    Key != Key.None &&
                    !IsModifierKey(Key);
            }
        }

        public Hotkey()
        {
        }

        public Hotkey(
            ModifierKeys modifiers,
            Key key)
        {
            Modifiers =
                modifiers;

            Key =
                key;
        }

        public override string ToString()
        {
            if (!IsValid)
            {
                return "Tanımsız";
            }

            var sb =
                new StringBuilder();

            if ((Modifiers & ModifierKeys.Control) != 0)
            {
                sb.Append("Ctrl + ");
            }

            if ((Modifiers & ModifierKeys.Shift) != 0)
            {
                sb.Append("Shift + ");
            }

            if ((Modifiers & ModifierKeys.Alt) != 0)
            {
                sb.Append("Alt + ");
            }

            if ((Modifiers & ModifierKeys.Windows) != 0)
            {
                sb.Append("Win + ");
            }

            sb.Append(
                GetKeyDisplayName(Key));

            return sb.ToString();
        }

        public bool Equals(
            Hotkey other)
        {
            if (ReferenceEquals(
                other,
                null))
            {
                return false;
            }

            if (ReferenceEquals(
                this,
                other))
            {
                return true;
            }

            return
                Modifiers == other.Modifiers &&
                Key == other.Key;
        }

        public override bool Equals(
            object obj)
        {
            return Equals(
                obj as Hotkey);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash =
                    17;

                hash =
                    hash * 23 +
                    Modifiers.GetHashCode();

                hash =
                    hash * 23 +
                    Key.GetHashCode();

                return hash;
            }
        }

        public static bool operator ==(
            Hotkey left,
            Hotkey right)
        {
            if (ReferenceEquals(
                left,
                right))
            {
                return true;
            }

            if (ReferenceEquals(
                    left,
                    null) ||
                ReferenceEquals(
                    right,
                    null))
            {
                return false;
            }

            return left.Equals(
                right);
        }

        public static bool operator !=(
            Hotkey left,
            Hotkey right)
        {
            return !(left == right);
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

        private static string GetKeyDisplayName(
            Key key)
        {
            switch (key)
            {
                case Key.Return:
                    return "Enter";

                case Key.Back:
                    return "Backspace";

                case Key.Space:
                    return "Space";

                case Key.Escape:
                    return "Esc";

                case Key.Prior:
                    return "PageUp";

                case Key.Next:
                    return "PageDown";

                case Key.Capital:
                    return "CapsLock";

                case Key.Snapshot:
                    return "PrintScreen";

                default:
                    return key.ToString();
            }
        }
    }
}