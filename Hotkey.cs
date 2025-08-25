using System.Text;
using System.Windows.Input;

namespace P5S_ceviri
{
    public class Hotkey
    {
        public ModifierKeys Modifiers { get; set; }
        public Key Key { get; set; }

        public Hotkey() { }

        public Hotkey(ModifierKeys modifiers, Key key)
        {
            Modifiers = modifiers;
            Key = key;
        }

        public override string ToString()
        {
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
    }
}