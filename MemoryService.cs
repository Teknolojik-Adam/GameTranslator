using P5S_ceviri;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Interop;

namespace P5S_ceviri
{
    public class MemoryService : IMemoryService, IDisposable
    {
        #region Windows API Imports (P/Invoke)
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            [Out] byte[] lpBuffer,
            int dwSize,
            out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint dwDesiredAccess,
            bool bInheritHandle,
            int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
        #endregion

        protected readonly ILogger _logger;
        protected readonly AppSettings _appSettings;
        private IntPtr _processHandle = IntPtr.Zero;
        private Process _attachedProcess; 
        private bool _disposed = false;
        private readonly object _lockObject = new object(); 
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const int MAX_CACHE_ENTRIES = 1000;
        private const int MAX_READ_SIZE = 1024 * 1024; // varsayilan 1MB maksimum okuma boyutu
        private const int MIN_PROCESS_ID = 4; 
        private const long MAX_VALID_ADDRESS = 0x7FFFFFFFFFFF; 
        private const long MIN_VALID_ADDRESS = 0x10000;

        #region Cache Classes
        private class CacheEntry<T>
        {
            public T Value { get; set; }
            public int AccessCount { get; set; }
        }
        #endregion

        #region Cache Fields
        private readonly Dictionary<PathInfo, CacheEntry<IntPtr>> _addressCache = new Dictionary<PathInfo, CacheEntry<IntPtr>>();
        private readonly Dictionary<(IntPtr, int), CacheEntry<byte[]>> _memoryCache = new Dictionary<(IntPtr, int), CacheEntry<byte[]>>();
        #endregion

        public MemoryService(ILogger logger, AppSettings appSettings)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        }


        public string TryReadStringDeep(IntPtr address)
        {
           
            if (_disposed)
            {
                _logger?.LogWarning("MemoryService dispose edilmiş durumda. İşlem reddedildi.");
                return null;
            }

            
            if (!IsValidAddress(address))
            {
                _logger?.LogWarning($"Geçersiz adres: 0x{address.ToInt64():X}");
                return null;
            }

            lock (_lockObject)
            {
                var queue = new Queue<(IntPtr address, int depth)>();
                queue.Enqueue((address, 0));
                var visited = new HashSet<long>();
                int maxIterations = 1000; // Sonsuz döngü koruması
                int iterationCount = 0;

                while (queue.Any() && iterationCount < maxIterations)
                {
                    iterationCount++;
                    var (currentAddress, currentDepth) = queue.Dequeue();

                    
                    if (currentAddress == IntPtr.Zero || 
                        currentDepth > _appSettings.PointerSearchMaxDepth || 
                        !visited.Add(currentAddress.ToInt64()) ||
                        !IsValidAddress(currentAddress))
                    {
                        continue;
                    }

                    
                    int readLength = Math.Min(_appSettings.StringReadLength, MAX_READ_SIZE);
                    var buffer = ReadBytes(currentAddress, readLength);
                    if (buffer.Length == 0)
                    {
                        continue;
                    }

                    var (found, text) = ParseBufferAsString(buffer);
                    if (found)
                    {
                        return text;
                    }

                    if (buffer.Length >= IntPtr.Size)
                    {
                        try
                        {
                            long pointerValue = IntPtr.Size == 8 ? BitConverter.ToInt64(buffer, 0) : BitConverter.ToInt32(buffer, 0);
                            
                            if (IsValidAddress(new IntPtr(pointerValue)))
                            {
                                queue.Enqueue((new IntPtr(pointerValue), currentDepth + 1));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning($"Pointer değeri okuma hatası: {ex.Message}");
                        }
                    }
                }

                if (iterationCount >= maxIterations)
                {
                    _logger?.LogWarning("Maksimum iterasyon sayısına ulaşıldı. Güvenlik nedeniyle işlem durduruldu.");
                }
            }
            return null;
        }

        private (bool, string) ParseBufferAsString(byte[] buffer)
        {
            int nullIndex = Array.IndexOf(buffer, (byte)0);
            if (nullIndex >= 0)
            {
                var segment = new ArraySegment<byte>(buffer, 0, nullIndex);
                buffer = segment.ToArray();
            }

            foreach (var encodingName in new[] { "Unicode", "UTF-8", "ASCII" })
            {
                try
                {
                    Encoding encoding = Encoding.GetEncoding(encodingName);
                    string result = encoding.GetString(buffer).Trim('\0');
                    if (IsValidGameText(result))
                    {
                        return (true, result);
                    }
                }
                catch { continue; }
            }
            return (false, null);
        }

        public bool AttachToProcess(int processId)
        {
           
            if (_disposed)
            {
                _logger?.LogWarning("MemoryService dispose edilmiş durumda. Process bağlantısı reddedildi.");
                return false;
            }

            
            if (!IsValidProcessId(processId))
            {
                _logger?.LogError($"Geçersiz Process ID: {processId}. Sistem process'lerine erişim reddedildi.");
                return false;
            }

            lock (_lockObject)
            {
                // Önbellekleri temizle
                ClearAllCaches();
                
                Dispose(); 

                try
                {
                   
                    var process = Process.GetProcessById(processId);
                    if (process == null)
                    {
                        _logger?.LogError($"Process bulunamadı (ID: {processId}).");
                        return false;
                    }

                    
                    if (process.HasExited)
                    {
                        _logger?.LogError($"Process zaten kapanmış (ID: {processId}).");
                        return false;
                    }

                    _processHandle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, processId);
                    if (_processHandle != IntPtr.Zero)
                    {
                        
                        _attachedProcess = process;
                        _logger?.LogInformation($"Process'e başarıyla bağlanıldı (ID: {processId}, Adı: {process.ProcessName}).");
                        return true;
                    }
                    else
                    {
                        int errorCode = Marshal.GetLastWin32Error();
                        _logger?.LogError($"Process'e bağlanılamadı (ID: {processId}). Hata Kodu: {errorCode}");
                        return false;
                    }
                }
                catch (ArgumentException)
                {
                    _logger?.LogError($"Process bulunamadı (ID: {processId}). Muhtemelen kapanmış.");
                    return false;
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Process bağlantısı sırasında beklenmeyen hata (ID: {processId}): {ex.Message}", ex);
                    return false;
                }
            }
        }

        public byte[] ReadBytes(IntPtr address, int length)
        {
            
            if (_disposed)
            {
                _logger?.LogWarning("MemoryService dispose edilmiş durumda. Bellek okuma reddedildi.");
                return new byte[0];
            }

            if (_processHandle == IntPtr.Zero)
            {
                _logger?.LogWarning("Process handle geçersiz. Bellek okuma reddedildi.");
                return new byte[0];
            }

            if (address == IntPtr.Zero)
            {
                _logger?.LogWarning("Geçersiz adres (null). Bellek okuma reddedildi.");
                return new byte[0];
            }

            
            if (!IsValidAddress(address))
            {
                _logger?.LogWarning($"Geçersiz adres: 0x{address.ToInt64():X}. Bellek okuma reddedildi.");
                return new byte[0];
            }

            
            if (length <= 0 || length > MAX_READ_SIZE)
            {
                _logger?.LogWarning($"Geçersiz okuma boyutu: {length}. Maksimum: {MAX_READ_SIZE}");
                return new byte[0];
            }

            
            if (_attachedProcess != null && _attachedProcess.HasExited)
            {
                _logger?.LogWarning("Bağlı process kapanmış. Bellek okuma reddedildi.");
                return new byte[0];
            }

            lock (_lockObject)
            {
                try
                {
                    byte[] buffer = new byte[length];
                    if (ReadProcessMemory(_processHandle, address, buffer, length, out int bytesRead) && bytesRead == length)
                    {
                        return buffer;
                    }
                    else
                    {
                        int errorCode = Marshal.GetLastWin32Error();
                        _logger?.LogWarning($"Bellek okuma başarısız: Adres 0x{address.ToInt64():X}, Uzunluk {length}, Okunan Byte {bytesRead}, Hata Kodu: {errorCode}");
                        return new byte[0];
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Bellek okuma sırasında beklenmeyen hata: {ex.Message}", ex);
                    return new byte[0];
                }
            }
        }

        private bool IsValidGameText(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.Length < 2 || s.Length > 1000)
                return false;
            if (s.Contains('\uFFFD'))
                return false;
            int printableOrWhitespaceCount = s.Count(c => !char.IsControl(c) || char.IsWhiteSpace(c));
            double printableRatio = (double)printableOrWhitespaceCount / s.Length;
            return printableRatio >= 0.8 && s.Any(char.IsLetterOrDigit);
        }

        
        private bool IsValidAddress(IntPtr address)
        {
            if (address == IntPtr.Zero)
                return false;

            long addressValue = address.ToInt64();
            return addressValue >= MIN_VALID_ADDRESS && addressValue <= MAX_VALID_ADDRESS;
        }

        
        private bool IsValidProcessId(int processId)
        {
            return processId >= MIN_PROCESS_ID && processId <= int.MaxValue;
        }

        public IntPtr ResolveAddressFromPath(Process process, PathInfo path)
        {
            if (path == null || process == null) return IntPtr.Zero;
            try
            {
                ProcessModule mainModule = process.MainModule;
                if (mainModule == null || !mainModule.ModuleName.Equals(path.BaseAddressModule, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning($"Modül eşleşmedi: Beklenen '{path.BaseAddressModule}', Bulunan '{mainModule?.ModuleName}'");
                    return IntPtr.Zero;
                }
                IntPtr currentAddress = IntPtr.Add(mainModule.BaseAddress, (int)path.BaseAddressOffset);
                _logger?.LogInformation($"Başlangıç adresi: 0x{currentAddress.ToInt64():X} ({mainModule.ModuleName} + 0x{path.BaseAddressOffset:X})");
                foreach (var offset in path.PointerOffsets)
                {
                    var pointerBytes = ReadBytes(currentAddress, IntPtr.Size);
                    if (pointerBytes.Length == 0)
                    {
                        _logger?.LogWarning($"Pointer okuma başarısız: 0x{currentAddress.ToInt64():X}");
                        return IntPtr.Zero;
                    }
                    long pointerValue = IntPtr.Size == 8 ? BitConverter.ToInt64(pointerBytes, 0) : BitConverter.ToInt32(pointerBytes, 0);
                    currentAddress = new IntPtr(pointerValue);
                    if (currentAddress == IntPtr.Zero)
                    {
                        _logger?.LogWarning("Pointer değeri sıfır.");
                        return IntPtr.Zero;
                    }
                    currentAddress = IntPtr.Add(currentAddress, offset);
                    _logger?.LogInformation($" -> Offset 0x{offset:X} uygulandı. Yeni adres: 0x{currentAddress.ToInt64():X}");
                }
                _logger?.LogInformation($"Pointer yolu başarıyla çözümlendi: 0x{currentAddress.ToInt64():X}");
                return currentAddress;
            }
            catch (Exception ex)
            {
                _logger?.LogError("Adres yolu çözümlenirken hata oluştu.", ex);
                return IntPtr.Zero;
            }
        }

        #region Cache Methods
        public IntPtr ResolveAddressFromPathCached(Process process, PathInfo path)
        {
            if (path == null) return IntPtr.Zero;

            lock (_lockObject)
            {
                if (_addressCache.TryGetValue(path, out var cachedEntry))
                {
                    cachedEntry.AccessCount++;
                    _logger?.LogInformation($"Önbellekten adres alındı: 0x{cachedEntry.Value.ToInt64():X} (Erişim sayısı: {cachedEntry.AccessCount})");
                    return cachedEntry.Value;
                }

                IntPtr resolvedAddress = ResolveAddressFromPath(process, path);
                if (resolvedAddress != IntPtr.Zero)
                {
                    if (_addressCache.Count >= MAX_CACHE_ENTRIES)
                    {
                        var lru = _addressCache.OrderBy(kvp => kvp.Value.AccessCount).First();
                        _addressCache.Remove(lru.Key);
                        _logger?.LogInformation($"Adres önbelleği dolu. En az kullanılan giriş kaldırıldı (Erişim sayısı: {lru.Value.AccessCount})");
                    }
                    _addressCache[path] = new CacheEntry<IntPtr> { Value = resolvedAddress, AccessCount = 1 };
                    _logger?.LogInformation($"Adres önbelleğe eklendi: 0x{resolvedAddress.ToInt64():X}");
                }
                return resolvedAddress;
            }
        }

        public byte[] ReadBytesCached(IntPtr address, int length)
        {
            var key = (address, length);
            lock (_lockObject)
            {
                if (_memoryCache.TryGetValue(key, out var cachedEntry))
                {
                    cachedEntry.AccessCount++;
                    _logger?.LogInformation($"Önbellekten bellek verisi alındı: 0x{address.ToInt64():X} ({length} byte, Erişim sayısı: {cachedEntry.AccessCount})");
                    return cachedEntry.Value;
                }
            }

            byte[] buffer = ReadBytes(address, length);

            if (buffer.Length > 0)
            {
                lock (_lockObject)
                {
                    if (_memoryCache.Count >= MAX_CACHE_ENTRIES)
                    {
                        var lru = _memoryCache.OrderBy(kvp => kvp.Value.AccessCount).First();
                        _memoryCache.Remove(lru.Key);
                        _logger?.LogInformation($"Bellek önbelleği dolu. En az kullanılan giriş kaldırıldı (Erişim sayısı: {lru.Value.AccessCount})");
                    }
                    _memoryCache[key] = new CacheEntry<byte[]> { Value = buffer, AccessCount = 1 };
                    _logger?.LogInformation($"Bellek verisi önbelleğe eklendi: 0x{address.ToInt64():X} ({length} byte)");
                }
            }
            return buffer;
        }

        public List<KeyValuePair<PathInfo, IntPtr>> GetMostFrequentAddresses(int topN = 10)
        {
            lock (_lockObject)
            {
                var result = _addressCache
                    .OrderByDescending(kvp => kvp.Value.AccessCount)
                    .Take(topN)
                    .Select(kvp => new KeyValuePair<PathInfo, IntPtr>(kvp.Key, kvp.Value.Value))
                    .ToList();
                
                _logger?.LogInformation($"En sık kullanılan {result.Count} adres listelendi");
                return result;
            }
        }

        public void ClearAddressCache()
        {
            lock (_lockObject)
            {
                int count = _addressCache.Count;
                _addressCache.Clear();
                _logger?.LogInformation($"Adres önbelleği temizlendi ({count} giriş)");
            }
        }

        public void ClearMemoryCache()
        {
            lock (_lockObject)
            {
                int count = _memoryCache.Count;
                _memoryCache.Clear();
                _logger?.LogInformation($"Bellek önbelleği temizlendi ({count} giriş)");
            }
        }

        public void ClearAllCaches()
        {
            lock (_lockObject)
            {
                int addressCount = _addressCache.Count;
                int memoryCount = _memoryCache.Count;
                _addressCache.Clear();
                _memoryCache.Clear();
                _logger?.LogInformation($"Tüm önbellekler temizlendi (Adres: {addressCount}, Bellek: {memoryCount})");
            }
        }

        public (int AddressCacheCount, int MemoryCacheCount) GetCacheStatistics()
        {
            lock (_lockObject)
            {
                return (_addressCache.Count, _memoryCache.Count);
            }
        }
        #endregion

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
                        // Önbellekleri temizle
                        ClearAllCaches();

                        if (_processHandle != IntPtr.Zero)
                        {
                            try
                            {
                                CloseHandle(_processHandle);
                                _logger?.LogInformation("Process handle kapatıldı.");
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError($"Process handle kapatılırken hata: {ex.Message}", ex);
                            }
                            finally
                            {
                                _processHandle = IntPtr.Zero;
                            }
                        }

                        if (_attachedProcess != null)
                        {
                            try
                            {
                                _attachedProcess.Dispose();
                                _logger?.LogInformation("Process nesnesi temizlendi.");
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError($"Process nesnesi temizlenirken hata: {ex.Message}", ex);
                            }
                            finally
                            {
                                _attachedProcess = null;
                            }
                        }
                    }
                }
                _disposed = true;
            }
        }

        ~MemoryService()
        {
            Dispose(false);
        }
    }
}