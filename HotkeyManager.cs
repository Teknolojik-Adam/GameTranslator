using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace P5S_ceviri
{
    public class HotkeyManager
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private HwndSource _hwndSource;
        private const int WM_HOTKEY = 0x0312;
        private Dictionary<int, Action> _hotkeyActions = new Dictionary<int, Action>();
        private int _nextHotkeyId = 1;

        public HotkeyManager(HwndSource hwndSource)
        {
            _hwndSource = hwndSource;
            _hwndSource.AddHook(WndProc);
        }

        public int RegisterHotkey(ModifierKeys modifiers, Key key, Action action)
        {
            uint m = (uint)modifiers;
            uint k = (uint)KeyInterop.VirtualKeyFromKey(key);
            int hotkeyId = _nextHotkeyId++;

            if (!RegisterHotKey(_hwndSource.Handle, hotkeyId, m, k))
            {
                // Kısayol kaydedilemedi
                return 0;
            }

            _hotkeyActions[hotkeyId] = action;
            return hotkeyId; // Başarılıysa hotkey ID'si döndürülür
        }

        public void UnregisterHotkey(int hotkeyId)
        {
            if (hotkeyId == 0) return; // Geçersiz hotkey ID'si

            UnregisterHotKey(_hwndSource.Handle, hotkeyId);
            _hotkeyActions.Remove(hotkeyId);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                if (_hotkeyActions.ContainsKey(hotkeyId))
                {
                    _hotkeyActions[hotkeyId].Invoke();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }
    }
}