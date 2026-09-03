using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace GameTranslatorUltimate
{
    public sealed class HotkeyManager : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;

        private readonly HwndSource _hwndSource;
        private readonly ILogger _logger;
        private readonly Dictionary<int, HotkeyInfo> _registeredHotkeys;
        private readonly object _lockObject;

        private int _nextHotkeyId;
        private bool _disposed;

        private sealed class HotkeyInfo
        {
            public int Id { get; set; }

            public ModifierKeys Modifiers { get; set; }

            public Key Key { get; set; }

            public Action Action { get; set; }

            public string Description { get; set; }
        }

        public HotkeyManager(
            HwndSource hwndSource,
            ILogger logger)
        {
            if (hwndSource == null)
            {
                throw new ArgumentNullException(
                    nameof(hwndSource));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(
                    nameof(logger));
            }

            _hwndSource = hwndSource;
            _logger = logger;

            _registeredHotkeys =
                new Dictionary<int, HotkeyInfo>();

            _lockObject =
                new object();

            _nextHotkeyId =
                1;

            _hwndSource.AddHook(
                WndProc);

            _logger.LogInformation(
                "HotkeyManager başlatıldı.");
        }

        public int RegisteredHotkeyCount
        {
            get
            {
                lock (_lockObject)
                {
                    if (_disposed)
                        return 0;

                    return _registeredHotkeys.Count;
                }
            }
        }

        public int RegisterHotkey(
            ModifierKeys modifiers,
            Key key,
            Action action,
            string description = null)
        {
            if (action == null)
            {
                _logger.LogWarning(
                    "Hotkey action null olamaz.");

                return 0;
            }

            if (key == Key.None)
            {
                _logger.LogWarning(
                    "Geçersiz hotkey tuşu: Key.None.");

                return 0;
            }

            lock (_lockObject)
            {
                if (_disposed)
                {
                    _logger.LogWarning(
                        "HotkeyManager dispose edilmiş durumda.");

                    return 0;
                }

                HotkeyInfo existing =
                    _registeredHotkeys.Values
                        .FirstOrDefault(
                            h =>
                                h.Modifiers == modifiers &&
                                h.Key == key);

                if (existing != null)
                {
                    _logger.LogWarning(
                        $"Bu kısayol kombinasyonu zaten kayıtlı: {modifiers} + {key}, ID: {existing.Id}");

                    return 0;
                }

                int virtualKey =
                    KeyInterop.VirtualKeyFromKey(
                        key);

                if (virtualKey == 0)
                {
                    _logger.LogWarning(
                        $"Hotkey için sanal tuş kodu alınamadı: {key}");

                    return 0;
                }

                int hotkeyId =
                    GetNextAvailableId();

                if (hotkeyId <= 0)
                {
                    _logger.LogError(
                        "Yeni hotkey ID'si oluşturulamadı.");

                    return 0;
                }

                bool registered =
                    NativeMethods.RegisterHotKey(
                        _hwndSource.Handle,
                        hotkeyId,
                        ConvertModifiers(modifiers),
                        (uint)virtualKey);

                if (!registered)
                {
                    int errorCode =
                        Marshal.GetLastWin32Error();

                    _logger.LogError(
                        $"Kısayol kaydedilemedi: {modifiers} + {key}. Hata kodu: {errorCode}");

                    return 0;
                }

                var hotkeyInfo =
                    new HotkeyInfo
                    {
                        Id = hotkeyId,
                        Modifiers = modifiers,
                        Key = key,
                        Action = action,
                        Description =
                            !string.IsNullOrWhiteSpace(description)
                                ? description.Trim()
                                : $"{modifiers} + {key}"
                    };

                _registeredHotkeys[hotkeyId] =
                    hotkeyInfo;

                _logger.LogInformation(
                    $"Kısayol kaydedildi: {hotkeyInfo.Description}, ID: {hotkeyId}");

                return hotkeyId;
            }
        }

        public void UnregisterHotkey(
            int hotkeyId)
        {
            if (hotkeyId <= 0)
                return;

            lock (_lockObject)
            {
                if (_disposed)
                {
                    _logger.LogWarning(
                        "HotkeyManager dispose edilmiş durumda.");

                    return;
                }

                UnregisterHotkeyInternal(
                    hotkeyId,
                    true);
            }
        }

        public void UnregisterAllHotkeys()
        {
            lock (_lockObject)
            {
                if (_disposed)
                    return;

                int[] hotkeyIds =
                    _registeredHotkeys.Keys
                        .ToArray();

                for (int i = 0;
                     i < hotkeyIds.Length;
                     i++)
                {
                    UnregisterHotkeyInternal(
                        hotkeyIds[i],
                        false);
                }

                _logger.LogInformation(
                    "Tüm kısayollar kaldırıldı.");
            }
        }

        public bool UpdateHotkey(
            int hotkeyId,
            ModifierKeys newModifiers,
            Key newKey)
        {
            lock (_lockObject)
            {
                if (_disposed)
                {
                    _logger.LogWarning(
                        "HotkeyManager dispose edilmiş durumda.");

                    return false;
                }

                HotkeyInfo current;

                if (!_registeredHotkeys.TryGetValue(
                    hotkeyId,
                    out current))
                {
                    _logger.LogWarning(
                        $"Kayıtlı olmayan hotkey ID'si: {hotkeyId}");

                    return false;
                }

                if (newKey == Key.None)
                {
                    _logger.LogWarning(
                        "Yeni hotkey tuşu geçersiz.");

                    return false;
                }

                HotkeyInfo conflict =
                    _registeredHotkeys.Values
                        .FirstOrDefault(
                            h =>
                                h.Id != hotkeyId &&
                                h.Modifiers == newModifiers &&
                                h.Key == newKey);

                if (conflict != null)
                {
                    _logger.LogWarning(
                        $"Yeni kısayol kombinasyonu zaten kullanımda: {newModifiers} + {newKey}");

                    return false;
                }

                int virtualKey =
                    KeyInterop.VirtualKeyFromKey(
                        newKey);

                if (virtualKey == 0)
                {
                    _logger.LogWarning(
                        $"Yeni tuş için sanal tuş kodu alınamadı: {newKey}");

                    return false;
                }

                ModifierKeys oldModifiers =
                    current.Modifiers;

                Key oldKey =
                    current.Key;

                string description =
                    current.Description;

                if (!NativeMethods.UnregisterHotKey(
                    _hwndSource.Handle,
                    hotkeyId))
                {
                    int errorCode =
                        Marshal.GetLastWin32Error();

                    _logger.LogError(
                        $"Eski kısayol kaldırılamadı: {description}. Hata kodu: {errorCode}");

                    return false;
                }

                bool newRegistered =
                    NativeMethods.RegisterHotKey(
                        _hwndSource.Handle,
                        hotkeyId,
                        ConvertModifiers(newModifiers),
                        (uint)virtualKey);

                if (newRegistered)
                {
                    current.Modifiers =
                        newModifiers;

                    current.Key =
                        newKey;

                    _logger.LogInformation(
                        $"Kısayol güncellendi: {description} -> {newModifiers} + {newKey}");

                    return true;
                }

                int newError =
                    Marshal.GetLastWin32Error();

                int oldVirtualKey =
                    KeyInterop.VirtualKeyFromKey(
                        oldKey);

                bool restored =
                    oldVirtualKey != 0 &&
                    NativeMethods.RegisterHotKey(
                        _hwndSource.Handle,
                        hotkeyId,
                        ConvertModifiers(oldModifiers),
                        (uint)oldVirtualKey);

                if (!restored)
                {
                    _registeredHotkeys.Remove(
                        hotkeyId);

                    _logger.LogError(
                        $"Kısayol güncellenemedi ve eski kayıt geri yüklenemedi: {description}. Yeni kayıt hata kodu: {newError}");

                    return false;
                }

                _logger.LogError(
                    $"Kısayol güncellenemedi: {description}. Eski kayıt geri yüklendi. Hata kodu: {newError}");

                return false;
            }
        }

        public List<(
            int Id,
            ModifierKeys Modifiers,
            Key Key,
            string Description)> GetRegisteredHotkeys()
        {
            lock (_lockObject)
            {
                if (_disposed)
                {
                    return new List<(
                        int Id,
                        ModifierKeys Modifiers,
                        Key Key,
                        string Description)>();
                }

                return _registeredHotkeys.Values
                    .OrderBy(h => h.Id)
                    .Select(
                        h => (
                            h.Id,
                            h.Modifiers,
                            h.Key,
                            h.Description))
                    .ToList();
            }
        }

        public bool IsHotkeyRegistered(
            int hotkeyId)
        {
            lock (_lockObject)
            {
                if (_disposed)
                    return false;

                return _registeredHotkeys.ContainsKey(
                    hotkeyId);
            }
        }

        public bool IsHotkeyRegistered(
            ModifierKeys modifiers,
            Key key)
        {
            lock (_lockObject)
            {
                if (_disposed)
                    return false;

                return _registeredHotkeys.Values.Any(
                    h =>
                        h.Modifiers == modifiers &&
                        h.Key == key);
            }
        }

        private IntPtr WndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (msg != WM_HOTKEY)
                return IntPtr.Zero;

            int hotkeyId =
                wParam.ToInt32();

            Action action =
                null;

            string description =
                null;

            lock (_lockObject)
            {
                if (_disposed)
                    return IntPtr.Zero;

                HotkeyInfo hotkeyInfo;

                if (!_registeredHotkeys.TryGetValue(
                    hotkeyId,
                    out hotkeyInfo))
                {
                    _logger.LogWarning(
                        $"Kayıtlı olmayan hotkey ID'si tetiklendi: {hotkeyId}");

                    return IntPtr.Zero;
                }

                action =
                    hotkeyInfo.Action;

                description =
                    hotkeyInfo.Description;
            }

            try
            {
                _logger.LogInformation(
                    $"Kısayol tetiklendi: {description}");

                if (action != null)
                {
                    action();
                }

                handled =
                    true;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    $"Kısayol action hatası: {description}",
                    ex);
            }

            return IntPtr.Zero;
        }

        private bool UnregisterHotkeyInternal(
            int hotkeyId,
            bool logUnknown)
        {
            HotkeyInfo hotkeyInfo;

            if (!_registeredHotkeys.TryGetValue(
                hotkeyId,
                out hotkeyInfo))
            {
                if (logUnknown)
                {
                    _logger.LogWarning(
                        $"Kayıtlı olmayan hotkey ID'si: {hotkeyId}");
                }

                return false;
            }

            bool result =
                NativeMethods.UnregisterHotKey(
                    _hwndSource.Handle,
                    hotkeyId);

            if (!result)
            {
                int errorCode =
                    Marshal.GetLastWin32Error();

                _logger.LogError(
                    $"Kısayol kaldırılamadı: {hotkeyInfo.Description}. Hata kodu: {errorCode}");

                return false;
            }

            _registeredHotkeys.Remove(
                hotkeyId);

            _logger.LogInformation(
                $"Kısayol kaldırıldı: {hotkeyInfo.Description}, ID: {hotkeyId}");

            return true;
        }

        private int GetNextAvailableId()
        {
            const int maxId =
                0xBFFF;

            int attempts =
                maxId;

            while (attempts-- > 0)
            {
                if (_nextHotkeyId <= 0 ||
                    _nextHotkeyId > maxId)
                {
                    _nextHotkeyId =
                        1;
                }

                int candidate =
                    _nextHotkeyId++;

                if (!_registeredHotkeys.ContainsKey(
                    candidate))
                {
                    return candidate;
                }
            }

            return 0;
        }

        private static uint ConvertModifiers(
            ModifierKeys modifiers)
        {
            uint nativeModifiers =
                0;

            if ((modifiers & ModifierKeys.Alt) != 0)
            {
                nativeModifiers |=
                    NativeMethods.MOD_ALT;
            }

            if ((modifiers & ModifierKeys.Control) != 0)
            {
                nativeModifiers |=
                    NativeMethods.MOD_CONTROL;
            }

            if ((modifiers & ModifierKeys.Shift) != 0)
            {
                nativeModifiers |=
                    NativeMethods.MOD_SHIFT;
            }

            if ((modifiers & ModifierKeys.Windows) != 0)
            {
                nativeModifiers |=
                    NativeMethods.MOD_WIN;
            }

            return nativeModifiers;
        }

        public void Dispose()
        {
            lock (_lockObject)
            {
                if (_disposed)
                    return;

                _logger.LogInformation(
                    "HotkeyManager kapatılıyor...");

                int[] hotkeyIds =
                    _registeredHotkeys.Keys
                        .ToArray();

                for (int i = 0;
                     i < hotkeyIds.Length;
                     i++)
                {
                    int hotkeyId =
                        hotkeyIds[i];

                    if (!NativeMethods.UnregisterHotKey(
                        _hwndSource.Handle,
                        hotkeyId))
                    {
                        int errorCode =
                            Marshal.GetLastWin32Error();

                        _logger.LogWarning(
                            $"Kapanış sırasında hotkey kaldırılamadı. ID: {hotkeyId}, Hata kodu: {errorCode}");
                    }
                }

                _registeredHotkeys.Clear();

                try
                {
                    _hwndSource.RemoveHook(
                        WndProc);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "Hotkey hook kaldırılırken hata oluştu.",
                        ex);
                }

                _disposed =
                    true;

                _logger.LogInformation(
                    "HotkeyManager kapatıldı.");
            }

            GC.SuppressFinalize(
                this);
        }

        private static class NativeMethods
        {
            public const uint MOD_ALT =
                0x0001;

            public const uint MOD_CONTROL =
                0x0002;

            public const uint MOD_SHIFT =
                0x0004;

            public const uint MOD_WIN =
                0x0008;

            [DllImport(
                "user32.dll",
                SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool RegisterHotKey(
                IntPtr hWnd,
                int id,
                uint fsModifiers,
                uint vk);

            [DllImport(
                "user32.dll",
                SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool UnregisterHotKey(
                IntPtr hWnd,
                int id);
        }
    }
}