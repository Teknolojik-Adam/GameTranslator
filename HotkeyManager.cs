using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace P5S_ceviri
{
    public class HotkeyManager : IDisposable
    {
        #region Win32 API
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        #endregion

        #region Hotkey Info
        private class HotkeyInfo
        {
            public int Id { get; set; }
            public ModifierKeys Modifiers { get; set; }
            public Key Key { get; set; }
            public Action Action { get; set; }
            public string Description { get; set; }
        }
        #endregion

        private readonly HwndSource _hwndSource;
        private readonly ILogger _logger;
        private readonly Dictionary<int, HotkeyInfo> _registeredHotkeys = new Dictionary<int, HotkeyInfo>();
        private readonly object _lockObject = new object();
        private int _nextHotkeyId = 1;
        private bool _disposed = false;

        public int RegisteredHotkeyCount
        {
            get
            {
                lock (_lockObject)
                {
                    return _registeredHotkeys.Count;
                }
            }
        }

        public HotkeyManager(HwndSource hwndSource, ILogger logger)
        {
            _hwndSource = hwndSource ?? throw new ArgumentNullException(nameof(hwndSource));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _hwndSource.AddHook(WndProc);
            _logger.LogInformation("HotkeyManager başlatıldı");
        }

        public int RegisterHotkey(ModifierKeys modifiers, Key key, Action action, string description = null)
        {
            if (_disposed)
            {
                _logger.LogWarning("HotkeyManager dispose edilmiş durumda. İşlem reddedildi.");
                return 0;
            }

            if (action == null)
            {
                _logger.LogWarning("Hotkey action null olamaz.");
                return 0;
            }

            lock (_lockObject)
            {
                // Aynı kombinasyon zaten kayıtlı mı kontrol etmek için
                var existing = _registeredHotkeys.Values.FirstOrDefault(h => h.Modifiers == modifiers && h.Key == key);
                if (existing != null)
                {
                    _logger.LogWarning($"Bu kısayol kombinasyonu zaten kayıtlı: {modifiers} + {key}, ID: {existing.Id}");
                    return 0;
                }

                uint m = (uint)modifiers;
                uint k = (uint)KeyInterop.VirtualKeyFromKey(key);
                int hotkeyId = _nextHotkeyId++;

                if (!RegisterHotKey(_hwndSource.Handle, hotkeyId, m, k))
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    _logger.LogError($"Kısayol kaydedilemedi: {modifiers} + {key}. Hata Kodu: {errorCode}");
                    return 0;
                }

                var hotkeyInfo = new HotkeyInfo
                {
                    Id = hotkeyId,
                    Modifiers = modifiers,
                    Key = key,
                    Action = action,
                    Description = description ?? $"{modifiers} + {key}"
                };

                _registeredHotkeys[hotkeyId] = hotkeyInfo;
                _logger.LogInformation($"Kısayol kaydedildi: {hotkeyInfo.Description}, ID: {hotkeyId}");
                return hotkeyId;
            }
        }

        public void UnregisterHotkey(int hotkeyId)
        {
            if (_disposed)
            {
                _logger.LogWarning("HotkeyManager dispose edilmiş durumda. İşlem reddedildi.");
                return;
            }

            if (hotkeyId == 0) return;

            lock (_lockObject)
            {
                if (!_registeredHotkeys.TryGetValue(hotkeyId, out var hotkeyInfo))
                {
                    _logger.LogWarning($"Kayıtlı olmayan hotkey ID'si: {hotkeyId}");
                    return;
                }

                if (!UnregisterHotKey(_hwndSource.Handle, hotkeyId))
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    _logger.LogError($"Kısayol kaldırılamadı: {hotkeyInfo.Description}. Hata Kodu: {errorCode}");
                    return;
                }

                _registeredHotkeys.Remove(hotkeyId);
                _logger.LogInformation($"Kısayol kaldırıldı: {hotkeyInfo.Description}, ID: {hotkeyId}");
            }
        }

        public void UnregisterAllHotkeys()
        {
            lock (_lockObject)
            {
                var hotkeyIds = _registeredHotkeys.Keys.ToList();
                foreach (var id in hotkeyIds)
                {
                    UnregisterHotkey(id);
                }
                _logger.LogInformation("Tüm kısayollar kaldırıldı");
            }
        }

        public bool UpdateHotkey(int hotkeyId, ModifierKeys newModifiers, Key newKey)
        {
            if (_disposed)
            {
                _logger.LogWarning("HotkeyManager dispose edilmiş durumda. İşlem reddedildi.");
                return false;
            }

            lock (_lockObject)
            {
                if (!_registeredHotkeys.TryGetValue(hotkeyId, out var hotkeyInfo))
                {
                    _logger.LogWarning($"Kayıtlı olmayan hotkey ID'si: {hotkeyId}");
                    return false;
                }

                var oldDescription = hotkeyInfo.Description;
                var action = hotkeyInfo.Action;
                var description = hotkeyInfo.Description;

                // Önce eski kaydı kaldır
                UnregisterHotkey(hotkeyId);

                // Yeni kaydı oluştur (aynı ID kullanmak için _nextHotkeyId'yi ayarla)
                _nextHotkeyId = hotkeyId;
                var newId = RegisterHotkey(newModifiers, newKey, action, description);

                if (newId == hotkeyId)
                {
                    _logger.LogInformation($"Kısayol güncellendi: {oldDescription} -> {newModifiers} + {newKey}");
                    return true;
                }

                _logger.LogError($"Kısayol güncellenemedi: {oldDescription}");
                return false;
            }
        }

        public List<(int Id, ModifierKeys Modifiers, Key Key, string Description)> GetRegisteredHotkeys()
        {
            if (_disposed)
            {
                _logger.LogWarning("HotkeyManager dispose edilmiş durumda. İşlem reddedildi.");
                return new List<(int Id, ModifierKeys Modifiers, Key Key, string Description)>();
            }

            lock (_lockObject)
            {
                var hotkeys = _registeredHotkeys.Values
                    .Select(h => (h.Id, h.Modifiers, h.Key, h.Description))
                    .ToList();

                _logger.LogInformation($"Kayıtlı kısayollar listelendi. Toplam: {hotkeys.Count}");
                return hotkeys;
            }
        }

        public bool IsHotkeyRegistered(int hotkeyId)
        {
            lock (_lockObject)
            {
                return _registeredHotkeys.ContainsKey(hotkeyId);
            }
        }

        public bool IsHotkeyRegistered(ModifierKeys modifiers, Key key)
        {
            lock (_lockObject)
            {
                return _registeredHotkeys.Values.Any(h => h.Modifiers == modifiers && h.Key == key);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                
                lock (_lockObject)
                {
                    if (_registeredHotkeys.TryGetValue(hotkeyId, out var hotkeyInfo))
                    {
                        try
                        {
                            _logger.LogInformation($"Kısayol tetiklendi: {hotkeyInfo.Description}");
                            hotkeyInfo.Action?.Invoke();
                            handled = true;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Kısayol action hatası: {hotkeyInfo.Description}", ex);
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Kayıtlı olmayan hotkey ID'si tetiklendi: {hotkeyId}");
                    }
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    lock (_lockObject)
                    {
                        _logger.LogInformation("HotkeyManager kapatılıyor...");
                        
                        // Tüm hotkey'leri kaldır
                        foreach (var hotkeyId in _registeredHotkeys.Keys.ToList())
                        {
                            if (UnregisterHotKey(_hwndSource.Handle, hotkeyId))
                            {
                                _logger.LogInformation($"Kısayol kaldırıldı: ID {hotkeyId}");
                            }
                        }
                        _registeredHotkeys.Clear();

                        // Hook'u kaldır
                        if (_hwndSource != null)
                        {
                            _hwndSource.RemoveHook(WndProc);
                        }
                        
                        _logger.LogInformation("HotkeyManager kapatıldı");
                    }
                }
                _disposed = true;
            }
        }

        ~HotkeyManager()
        {
            Dispose(false);
        }
    }
}