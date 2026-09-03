using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GameTranslatorUltimate
{
    public class MemoryService : IMemoryService, IDisposable
    {
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
        private static extern bool CloseHandle(
            IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process(
            IntPtr hProcess,
            out bool wow64Process);

        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        private const int MaxCacheEntries = 1000;
        private const int MaxReadSize = 1024 * 1024;
        private const int MinProcessId = 4;

        private const long MinValidAddress = 0x10000;
        private const long MaxValidAddress64 = 0x00007FFFFFFFFFFF;

        private static readonly TimeSpan AddressCacheLifetime =
            TimeSpan.FromMilliseconds(500);

        private static readonly TimeSpan MemoryCacheLifetime =
            TimeSpan.FromMilliseconds(100);

        protected readonly ILogger _logger;
        protected readonly AppSettings _appSettings;

        private readonly object _lockObject =
            new object();

        private readonly Dictionary<string, AddressCacheEntry> _addressCache =
            new Dictionary<string, AddressCacheEntry>(
                StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<MemoryCacheKey, MemoryCacheEntry> _memoryCache =
            new Dictionary<MemoryCacheKey, MemoryCacheEntry>();

        private IntPtr _processHandle =
            IntPtr.Zero;

        private Process _attachedProcess;

        private int _attachedProcessId =
            -1;

        private int _targetPointerSize =
            IntPtr.Size;

        private int _disposed;

        private sealed class AddressCacheEntry
        {
            public PathInfo Path { get; set; }

            public IntPtr Value { get; set; }

            public int AccessCount { get; set; }

            public DateTime ExpiresAtUtc { get; set; }
        }

        private sealed class MemoryCacheEntry
        {
            public byte[] Value { get; set; }

            public int AccessCount { get; set; }

            public DateTime ExpiresAtUtc { get; set; }
        }

        private struct MemoryCacheKey : IEquatable<MemoryCacheKey>
        {
            public IntPtr Address;
            public int Length;

            public bool Equals(
                MemoryCacheKey other)
            {
                return
                    Address == other.Address &&
                    Length == other.Length;
            }

            public override bool Equals(
                object obj)
            {
                if (!(obj is MemoryCacheKey))
                    return false;

                return Equals(
                    (MemoryCacheKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return
                        Address.GetHashCode() * 397 ^
                        Length;
                }
            }
        }

        public MemoryService(
            ILogger logger,
            AppSettings appSettings)
        {
            _logger =
                logger ?? throw new ArgumentNullException(nameof(logger));

            _appSettings =
                appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        }

        public bool AttachToProcess(
            int processId)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _logger.LogWarning(
                    "MemoryService dispose edilmiş durumda.");

                return false;
            }

            if (!IsValidProcessId(processId))
            {
                _logger.LogError(
                    $"Geçersiz process ID: {processId}");

                return false;
            }

            lock (_lockObject)
            {
                if (_attachedProcessId == processId &&
                    _processHandle != IntPtr.Zero &&
                    IsAttachedProcessAlive())
                {
                    return true;
                }

                CloseCurrentProcess();

                Process process =
                    null;

                try
                {
                    process =
                        Process.GetProcessById(
                            processId);

                    if (process.HasExited)
                    {
                        process.Dispose();
                        return false;
                    }

                    IntPtr handle =
                        OpenProcess(
                            PROCESS_VM_READ |
                            PROCESS_QUERY_INFORMATION,
                            false,
                            processId);

                    if (handle == IntPtr.Zero)
                    {
                        int errorCode =
                            Marshal.GetLastWin32Error();

                        _logger.LogError(
                            $"Process'e bağlanılamadı. PID: {processId}, Hata: {errorCode}");

                        process.Dispose();

                        return false;
                    }

                    _processHandle =
                        handle;

                    _attachedProcess =
                        process;

                    _attachedProcessId =
                        processId;

                    _targetPointerSize =
                        DeterminePointerSize(
                            handle);

                    ClearAllCachesInternal();

                    _logger.LogInformation(
                        $"Process'e bağlanıldı. PID: {processId}, Pointer: {_targetPointerSize * 8}-bit");

                    return true;
                }
                catch (ArgumentException)
                {
                    process?.Dispose();

                    _logger.LogWarning(
                        $"Process bulunamadı. PID: {processId}");

                    return false;
                }
                catch (Exception ex)
                {
                    process?.Dispose();

                    CloseCurrentProcess();

                    _logger.LogError(
                        $"Process bağlantısı sırasında hata oluştu. PID: {processId}",
                        ex);

                    return false;
                }
            }
        }

        public byte[] ReadBytes(
            IntPtr address,
            int length)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return new byte[0];

            if (length <= 0 ||
                length > MaxReadSize)
            {
                return new byte[0];
            }

            if (!IsValidAddress(address))
                return new byte[0];

            lock (_lockObject)
            {
                if (_processHandle == IntPtr.Zero ||
                    !IsAttachedProcessAlive())
                {
                    return new byte[0];
                }

                try
                {
                    var buffer =
                        new byte[length];

                    int bytesRead;

                    bool success =
                        ReadProcessMemory(
                            _processHandle,
                            address,
                            buffer,
                            length,
                            out bytesRead);

                    if (!success ||
                        bytesRead <= 0)
                    {
                        return new byte[0];
                    }

                    if (bytesRead == length)
                        return buffer;

                    var partial =
                        new byte[bytesRead];

                    Buffer.BlockCopy(
                        buffer,
                        0,
                        partial,
                        0,
                        bytesRead);

                    return partial;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        $"Bellek okunurken hata oluştu: 0x{address.ToInt64():X}",
                        ex);

                    return new byte[0];
                }
            }
        }

        public byte[] ReadBytesCached(
            IntPtr address,
            int length)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return new byte[0];

            if (!IsValidAddress(address) ||
                length <= 0 ||
                length > MaxReadSize)
            {
                return new byte[0];
            }

            var key =
                new MemoryCacheKey
                {
                    Address = address,
                    Length = length
                };

            lock (_lockObject)
            {
                MemoryCacheEntry entry;

                if (_memoryCache.TryGetValue(
                        key,
                        out entry))
                {
                    if (entry.ExpiresAtUtc >
                        DateTime.UtcNow)
                    {
                        entry.AccessCount++;

                        return CloneBytes(
                            entry.Value);
                    }

                    _memoryCache.Remove(
                        key);
                }
            }

            byte[] value =
                ReadBytes(
                    address,
                    length);

            if (value.Length == 0)
                return value;

            lock (_lockObject)
            {
                EnsureMemoryCacheCapacity();

                _memoryCache[key] =
                    new MemoryCacheEntry
                    {
                        Value =
                            CloneBytes(value),

                        AccessCount =
                            1,

                        ExpiresAtUtc =
                            DateTime.UtcNow +
                            MemoryCacheLifetime
                    };
            }

            return value;
        }

        public string TryReadStringDeep(
            IntPtr address)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return null;

            if (!IsValidAddress(address))
                return null;

            int maxDepth =
                _appSettings.PointerSearchMaxDepth;

            if (maxDepth < 0)
                maxDepth = 0;

            if (maxDepth > 32)
                maxDepth = 32;

            int readLength =
                _appSettings.StringReadLength;

            if (readLength < 4)
                readLength = 4;

            if (readLength > MaxReadSize)
                readLength = MaxReadSize;

            var queue =
                new Queue<AddressDepth>();

            var visited =
                new HashSet<long>();

            queue.Enqueue(
                new AddressDepth
                {
                    Address = address,
                    Depth = 0
                });

            int iterations =
                0;

            const int maxIterations =
                1000;

            while (queue.Count > 0 &&
                   iterations < maxIterations)
            {
                iterations++;

                AddressDepth item =
                    queue.Dequeue();

                if (item.Address == IntPtr.Zero ||
                    item.Depth > maxDepth ||
                    !IsValidAddress(item.Address) ||
                    !visited.Add(item.Address.ToInt64()))
                {
                    continue;
                }

                byte[] buffer =
                    ReadBytes(
                        item.Address,
                        readLength);

                if (buffer.Length == 0)
                    continue;

                string text =
                    ParseBufferAsString(
                        buffer);

                if (!string.IsNullOrWhiteSpace(text))
                    return text;

                if (buffer.Length <
                    _targetPointerSize)
                {
                    continue;
                }

                long pointerValue;

                if (!TryReadPointerValue(
                        buffer,
                        0,
                        out pointerValue))
                {
                    continue;
                }

                IntPtr pointerAddress;

                try
                {
                    pointerAddress =
                        new IntPtr(
                            pointerValue);
                }
                catch
                {
                    continue;
                }

                if (IsValidAddress(pointerAddress))
                {
                    queue.Enqueue(
                        new AddressDepth
                        {
                            Address =
                                pointerAddress,

                            Depth =
                                item.Depth + 1
                        });
                }
            }

            return null;
        }

        private sealed class AddressDepth
        {
            public IntPtr Address { get; set; }
            public int Depth { get; set; }
        }

        private string ParseBufferAsString(
            byte[] buffer)
        {
            if (buffer == null ||
                buffer.Length == 0)
            {
                return null;
            }

            string unicode =
                TryDecodeUtf16(
                    buffer);

            if (IsValidGameText(unicode))
                return unicode;

            string utf8 =
                TryDecodeSingleByte(
                    buffer,
                    Encoding.UTF8);

            if (IsValidGameText(utf8))
                return utf8;

            string ascii =
                TryDecodeSingleByte(
                    buffer,
                    Encoding.ASCII);

            if (IsValidGameText(ascii))
                return ascii;

            return null;
        }

        private static string TryDecodeUtf16(
            byte[] buffer)
        {
            try
            {
                int usableLength =
                    buffer.Length -
                    buffer.Length % 2;

                if (usableLength < 4)
                    return null;

                int end =
                    usableLength;

                for (int i = 0;
                     i + 1 < usableLength;
                     i += 2)
                {
                    if (buffer[i] == 0 &&
                        buffer[i + 1] == 0)
                    {
                        end = i;
                        break;
                    }
                }

                if (end < 2)
                    return null;

                string text =
                    Encoding.Unicode.GetString(
                        buffer,
                        0,
                        end);

                return text
                    .Trim('\0')
                    .Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string TryDecodeSingleByte(
            byte[] buffer,
            Encoding encoding)
        {
            try
            {
                int end =
                    Array.IndexOf(
                        buffer,
                        (byte)0);

                if (end < 0)
                    end = buffer.Length;

                if (end <= 0)
                    return null;

                string text =
                    encoding.GetString(
                        buffer,
                        0,
                        end);

                return text
                    .Trim('\0')
                    .Trim();
            }
            catch
            {
                return null;
            }
        }

        public IntPtr ResolveAddressFromPath(
            Process process,
            PathInfo path)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return IntPtr.Zero;

            if (process == null ||
                path == null)
            {
                return IntPtr.Zero;
            }

            try
            {
                if (process.HasExited)
                    return IntPtr.Zero;

                ProcessModule module =
                    FindModule(
                        process,
                        path.BaseAddressModule);

                if (module == null)
                {
                    _logger.LogWarning(
                        $"Modül bulunamadı: {path.BaseAddressModule}");

                    return IntPtr.Zero;
                }

                long currentValue =
                    module.BaseAddress.ToInt64() +
                    path.BaseAddressOffset;

                IntPtr currentAddress =
                    new IntPtr(
                        currentValue);

                IEnumerable<int> offsets =
                    path.PointerOffsets ??
                    Enumerable.Empty<int>();

                foreach (int offset in offsets)
                {
                    byte[] pointerBytes =
                        ReadBytes(
                            currentAddress,
                            _targetPointerSize);

                    if (pointerBytes.Length <
                        _targetPointerSize)
                    {
                        return IntPtr.Zero;
                    }

                    long pointerValue;

                    if (!TryReadPointerValue(
                            pointerBytes,
                            0,
                            out pointerValue))
                    {
                        return IntPtr.Zero;
                    }

                    if (pointerValue == 0)
                        return IntPtr.Zero;

                    long next =
                        pointerValue +
                        offset;

                    currentAddress =
                        new IntPtr(
                            next);

                    if (!IsValidAddress(
                            currentAddress))
                    {
                        return IntPtr.Zero;
                    }
                }

                return currentAddress;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Pointer yolu çözümlenirken hata oluştu.",
                    ex);

                return IntPtr.Zero;
            }
        }

        public IntPtr ResolveAddressFromPathCached(
            Process process,
            PathInfo path)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return IntPtr.Zero;

            if (process == null ||
                path == null)
            {
                return IntPtr.Zero;
            }

            string key =
                BuildPathKey(
                    process.Id,
                    path);

            lock (_lockObject)
            {
                AddressCacheEntry entry;

                if (_addressCache.TryGetValue(
                        key,
                        out entry))
                {
                    if (entry.ExpiresAtUtc >
                        DateTime.UtcNow)
                    {
                        entry.AccessCount++;

                        return entry.Value;
                    }

                    _addressCache.Remove(
                        key);
                }
            }

            IntPtr resolvedAddress =
                ResolveAddressFromPath(
                    process,
                    path);

            if (resolvedAddress ==
                IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            lock (_lockObject)
            {
                EnsureAddressCacheCapacity();

                _addressCache[key] =
                    new AddressCacheEntry
                    {
                        Path =
                            ClonePath(path),

                        Value =
                            resolvedAddress,

                        AccessCount =
                            1,

                        ExpiresAtUtc =
                            DateTime.UtcNow +
                            AddressCacheLifetime
                    };
            }

            return resolvedAddress;
        }

        public List<KeyValuePair<PathInfo, IntPtr>> GetMostFrequentAddresses(
            int topN = 10)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new List<KeyValuePair<PathInfo, IntPtr>>();
            }

            if (topN <= 0)
            {
                return new List<KeyValuePair<PathInfo, IntPtr>>();
            }

            lock (_lockObject)
            {
                RemoveExpiredAddressCacheEntries();

                return _addressCache
                    .Values
                    .OrderByDescending(
                        entry =>
                            entry.AccessCount)
                    .Take(topN)
                    .Select(
                        entry =>
                            new KeyValuePair<PathInfo, IntPtr>(
                                ClonePath(entry.Path),
                                entry.Value))
                    .ToList();
            }
        }

        public void ClearAddressCache()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            lock (_lockObject)
            {
                _addressCache.Clear();
            }
        }

        public void ClearMemoryCache()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            lock (_lockObject)
            {
                _memoryCache.Clear();
            }
        }

        public void ClearAllCaches()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            lock (_lockObject)
            {
                ClearAllCachesInternal();
            }
        }

        public (
            int AddressCacheCount,
            int MemoryCacheCount)
            GetCacheStatistics()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return (0, 0);

            lock (_lockObject)
            {
                RemoveExpiredAddressCacheEntries();
                RemoveExpiredMemoryCacheEntries();

                return (
                    _addressCache.Count,
                    _memoryCache.Count);
            }
        }

        private bool TryReadPointerValue(
            byte[] buffer,
            int offset,
            out long value)
        {
            value =
                0;

            if (buffer == null ||
                offset < 0)
            {
                return false;
            }

            try
            {
                if (_targetPointerSize == 8)
                {
                    if (buffer.Length <
                        offset + 8)
                    {
                        return false;
                    }

                    value =
                        BitConverter.ToInt64(
                            buffer,
                            offset);

                    return true;
                }

                if (buffer.Length <
                    offset + 4)
                {
                    return false;
                }

                value =
                    BitConverter.ToUInt32(
                        buffer,
                        offset);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private ProcessModule FindModule(
            Process process,
            string moduleName)
        {
            if (process == null)
                return null;

            if (string.IsNullOrWhiteSpace(
                moduleName))
            {
                return process.MainModule;
            }

            try
            {
                foreach (ProcessModule module
                         in process.Modules)
                {
                    if (string.Equals(
                        module.ModuleName,
                        moduleName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return module;
                    }
                }
            }
            catch
            {
            }

            try
            {
                ProcessModule mainModule =
                    process.MainModule;

                if (mainModule != null &&
                    string.Equals(
                        mainModule.ModuleName,
                        moduleName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return mainModule;
                }
            }
            catch
            {
            }

            return null;
        }

        private int DeterminePointerSize(
            IntPtr processHandle)
        {
            if (!Environment.Is64BitOperatingSystem)
                return 4;

            try
            {
                bool isWow64;

                if (IsWow64Process(
                    processHandle,
                    out isWow64))
                {
                    return isWow64
                        ? 4
                        : 8;
                }
            }
            catch
            {
            }

            return IntPtr.Size;
        }

        private bool IsAttachedProcessAlive()
        {
            if (_attachedProcess == null)
                return false;

            try
            {
                return !_attachedProcess.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private bool IsValidGameText(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (text.Length < 2 ||
                text.Length > 4096)
            {
                return false;
            }

            if (text.IndexOf('\uFFFD') >= 0)
                return false;

            int acceptable =
                0;

            int useful =
                0;

            foreach (char character in text)
            {
                if (!char.IsControl(character) ||
                    char.IsWhiteSpace(character))
                {
                    acceptable++;
                }

                if (char.IsLetterOrDigit(character))
                {
                    useful++;
                }
            }

            double printableRatio =
                (double)acceptable /
                text.Length;

            return
                printableRatio >= 0.85 &&
                useful > 0;
        }

        private bool IsValidAddress(
            IntPtr address)
        {
            if (address ==
                IntPtr.Zero)
            {
                return false;
            }

            long value =
                address.ToInt64();

            if (value <
                MinValidAddress)
            {
                return false;
            }

            if (_targetPointerSize == 4)
            {
                return
                    value >= MinValidAddress &&
                    value <= uint.MaxValue;
            }

            return
                value <= MaxValidAddress64;
        }

        private static bool IsValidProcessId(
            int processId)
        {
            return processId >=
                   MinProcessId;
        }

        private void CloseCurrentProcess()
        {
            ClearAllCachesInternal();

            if (_processHandle !=
                IntPtr.Zero)
            {
                try
                {
                    CloseHandle(
                        _processHandle);
                }
                catch
                {
                }

                _processHandle =
                    IntPtr.Zero;
            }

            if (_attachedProcess != null)
            {
                try
                {
                    _attachedProcess.Dispose();
                }
                catch
                {
                }

                _attachedProcess =
                    null;
            }

            _attachedProcessId =
                -1;

            _targetPointerSize =
                IntPtr.Size;
        }

        private void ClearAllCachesInternal()
        {
            _addressCache.Clear();
            _memoryCache.Clear();
        }

        private void EnsureAddressCacheCapacity()
        {
            RemoveExpiredAddressCacheEntries();

            if (_addressCache.Count <
                MaxCacheEntries)
            {
                return;
            }

            KeyValuePair<string, AddressCacheEntry> victim =
                _addressCache
                    .OrderBy(
                        pair =>
                            pair.Value.AccessCount)
                    .ThenBy(
                        pair =>
                            pair.Value.ExpiresAtUtc)
                    .First();

            _addressCache.Remove(
                victim.Key);
        }

        private void EnsureMemoryCacheCapacity()
        {
            RemoveExpiredMemoryCacheEntries();

            if (_memoryCache.Count <
                MaxCacheEntries)
            {
                return;
            }

            KeyValuePair<MemoryCacheKey, MemoryCacheEntry> victim =
                _memoryCache
                    .OrderBy(
                        pair =>
                            pair.Value.AccessCount)
                    .ThenBy(
                        pair =>
                            pair.Value.ExpiresAtUtc)
                    .First();

            _memoryCache.Remove(
                victim.Key);
        }

        private void RemoveExpiredAddressCacheEntries()
        {
            DateTime now =
                DateTime.UtcNow;

            string[] expired =
                _addressCache
                    .Where(
                        pair =>
                            pair.Value.ExpiresAtUtc <= now)
                    .Select(
                        pair =>
                            pair.Key)
                    .ToArray();

            foreach (string key in expired)
            {
                _addressCache.Remove(
                    key);
            }
        }

        private void RemoveExpiredMemoryCacheEntries()
        {
            DateTime now =
                DateTime.UtcNow;

            MemoryCacheKey[] expired =
                _memoryCache
                    .Where(
                        pair =>
                            pair.Value.ExpiresAtUtc <= now)
                    .Select(
                        pair =>
                            pair.Key)
                    .ToArray();

            foreach (MemoryCacheKey key in expired)
            {
                _memoryCache.Remove(
                    key);
            }
        }

        private static string BuildPathKey(
            int processId,
            PathInfo path)
        {
            string offsets =
                path.PointerOffsets == null
                    ? string.Empty
                    : string.Join(
                        ",",
                        path.PointerOffsets);

            return
                processId +
                "|" +
                (path.BaseAddressModule ?? string.Empty)
                    .ToUpperInvariant() +
                "|" +
                path.BaseAddressOffset +
                "|" +
                offsets;
        }

        private static PathInfo ClonePath(
            PathInfo path)
        {
            if (path == null)
                return null;

            return new PathInfo
            {
                BaseAddressModule =
                    path.BaseAddressModule ??
                    string.Empty,

                BaseAddressOffset =
                    path.BaseAddressOffset,

                PointerOffsets =
                    path.PointerOffsets != null
                        ? new List<int>(
                            path.PointerOffsets)
                        : new List<int>()
            };
        }

        private static byte[] CloneBytes(
            byte[] source)
        {
            if (source == null ||
                source.Length == 0)
            {
                return new byte[0];
            }

            var result =
                new byte[source.Length];

            Buffer.BlockCopy(
                source,
                0,
                result,
                0,
                source.Length);

            return result;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(MemoryService));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) != 0)
            {
                return;
            }

            lock (_lockObject)
            {
                CloseCurrentProcess();
            }
        }
    }
}