using System;
using System.Text;
using System.Windows.Input;

namespace P5S_ceviri
{
    public class Hotkey : IEquatable<Hotkey>
    {
        public ModifierKeys Modifiers { get; set; }
        public Key Key { get; set; }

        public bool IsValid => Key != Key.None;

        public Hotkey() { }

        public Hotkey(ModifierKeys modifiers, Key key)
        {
            Modifiers = modifiers;
            Key = key;
        }

        public override string ToString()
        {
            if (Key == Key.None)
                return "Tanımsız";

            var sb = new StringBuilder();
            
            if (Modifiers.HasFlag(ModifierKeys.Control))
                sb.Append("Ctrl + ");
            if (Modifiers.HasFlag(ModifierKeys.Shift))
                sb.Append("Shift + ");
            if (Modifiers.HasFlag(ModifierKeys.Alt))
                sb.Append("Alt + ");
            if (Modifiers.HasFlag(ModifierKeys.Windows))
                sb.Append("Win + ");

            sb.Append(Key);

            return sb.ToString();
        }

        public bool Equals(Hotkey other)
        {
            if (other == null)
                return false;

            return Modifiers == other.Modifiers && Key == other.Key;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as Hotkey);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + Modifiers.GetHashCode();
                hash = hash * 23 + Key.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(Hotkey left, Hotkey right)
        {
            if (ReferenceEquals(left, right))
                return true;

            if (left is null || right is null)
                return false;

            return left.Equals(right);
        }
        //
        public static bool operator !=(Hotkey left, Hotkey right)
        {
            return !(left == right);
        }
    }
}