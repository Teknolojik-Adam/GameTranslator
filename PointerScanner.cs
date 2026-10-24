using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public class PointerPath
    {
        public string ModuleName { get; set; } = string.Empty;
        public long BaseOffset { get; set; }
        public List<int> Offsets { get; set; } = new List<int>();

        public override string ToString()
        {
            IEnumerable<int> offsets =
                Offsets ?? Enumerable.Empty<int>();

            string result =
                $"\"{ModuleName}\"+0x{BaseOffset:X}";

            string offsetText =
                string.Join(
                    ", ",
                    offsets.Select(FormatOffset));

            if (!string.IsNullOrWhiteSpace(offsetText))
            {
                result += ", " + offsetText;
            }

            return result;
        }

        private static string FormatOffset(int offset)
        {
            if (offset < 0)
            {
                return "-0x" +
                       Math.Abs((long)offset).ToString("X");
            }

            return "0x" +
                   offset.ToString("X");
        }
    }

    public class PointerScanner : IDisposable
    {
        private sealed class AddressCacheEntry
        {
            public IntPtr Address { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
        }

        private const int MaxScanOffset = 0x1000;
        private const int MaxPointerDepth = 8;
        private const int MaxFoundPaths = 2000;
        private const int MaxSearchRegionSize = 512 * 1024 * 1024;

        private static readonly TimeSpan AddressCacheLifetime =
            TimeSpan.FromMilliseconds(250);

        private readonly Process _process;
        private readonly ProcessModule _mainModule;
        private readonly ILogger _logger;
        private readonly IMemoryService _memoryService;

        private readonly object _cacheLock =
            new object();

        private readonly object _memoryLock =
            new object();

        private readonly Dictionary<string, AddressCacheEntry> _addressCache =
            new Dictionary<string, AddressCacheEntry>(
                StringComparer.OrdinalIgnoreCase);

        private int _disposed;

        public PointerScanner(
            Process process,
            IMemoryService memoryService,
            ILogger logger = null)
        {
            _process =
                process ?? throw new ArgumentNullException(nameof(process));

            _memoryService =
                memoryService ?? throw new ArgumentNullException(nameof(memoryService));

            _logger = logger;

            try
            {
                _mainModule =
                    process.MainModule;

                if (_mainModule == null)
                {
                    throw new ArgumentException(
                        "Sürecin bir ana modülü olmalıdır.",
                        nameof(process));
                }
            }
            catch (Exception ex)
            {
                throw new ArgumentException(
                    "Sürecin ana modülüne erişilemedi.",
                    nameof(process),
                    ex);
            }
        }

        public async Task<List<PointerPath>> FindPointers(
            IntPtr targetAddress,
            int maxDepth = 3,
            IntPtr? searchRegionStart = null,
            int? searchRegionSize = null)
        {
            ThrowIfDisposed();

            if (targetAddress == IntPtr.Zero)
            {
                return new List<PointerPath>();
            }

            if (!IsProcessAlive())
            {
                return new List<PointerPath>();
            }

            if (maxDepth < 1)
                maxDepth = 1;

            if (maxDepth > MaxPointerDepth)
                maxDepth = MaxPointerDepth;

            return await Task.Run(() =>
            {
                IntPtr regionStart =
                    searchRegionStart ??
                    _mainModule.BaseAddress;

                int regionSize =
                    searchRegionSize ??
                    _mainModule.ModuleMemorySize;

                if (regionStart == IntPtr.Zero ||
                    regionSize <= 0)
                {
                    return new List<PointerPath>();
                }

                if (regionSize > MaxSearchRegionSize)
                {
                    _logger?.LogWarning(
                        $"Pointer tarama bölgesi çok büyük: {regionSize:N0} byte.");

                    regionSize =
                        MaxSearchRegionSize;
                }

                byte[] memoryDump =
                    new byte[regionSize];

                bool readSuccess;

                try
                {
                    readSuccess =
                        MemoryService.ReadProcessMemory(
                            _process.Handle,
                            regionStart,
                            memoryDump,
                            memoryDump.Length,
                            out _);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(
                        "Pointer taraması için bellek okunamadı.",
                        ex);

                    return new List<PointerPath>();
                }

                if (!readSuccess)
                {
                    int errorCode =
                        Marshal.GetLastWin32Error();

                    _logger?.LogError(
                        $"Bellek okunamadı: 0x{regionStart.ToInt64():X}. Hata kodu: {errorCode}");

                    return new List<PointerPath>();
                }

                int pointerSize =
                    GetTargetPointerSize();

                var comparer =
                    new PointerPathComparer();

                var foundPaths =
                    new HashSet<PointerPath>(
                        comparer);

                long mainModuleStart =
                    _mainModule.BaseAddress.ToInt64();

                long mainModuleEnd =
                    mainModuleStart +
                    _mainModule.ModuleMemorySize;

                long targetValue =
                    targetAddress.ToInt64();

                if (targetValue >= mainModuleStart &&
                    targetValue < mainModuleEnd)
                {
                    foundPaths.Add(
                        new PointerPath
                        {
                            ModuleName =
                                _mainModule.ModuleName,

                            BaseOffset =
                                targetValue -
                                mainModuleStart,

                            Offsets =
                                new List<int>()
                        });
                }

                SearchPointersRecursive(
                    targetAddress,
                    new List<int>(),
                    maxDepth,
                    memoryDump,
                    regionStart,
                    pointerSize,
                    new HashSet<long>(),
                    foundPaths);

                List<PointerPath> result =
                    foundPaths
                        .Take(MaxFoundPaths)
                        .OrderBy(x =>
                            x.Offsets != null
                                ? x.Offsets.Count
                                : 0)
                        .ThenBy(x => x.BaseOffset)
                        .ToList();

                _logger?.LogInformation(
                    $"Pointer taraması tamamlandı. {result.Count} yol bulundu.");

                return result;
            }).ConfigureAwait(false);
        }

        private void SearchPointersRecursive(
            IntPtr targetAddress,
            List<int> currentOffsets,
            int depth,
            byte[] memoryDump,
            IntPtr memoryBase,
            int pointerSize,
            HashSet<long> currentBranch,
            HashSet<PointerPath> foundPaths)
        {
            if (depth <= 0 ||
                targetAddress == IntPtr.Zero ||
                foundPaths.Count >= MaxFoundPaths)
            {
                return;
            }

            long targetValue =
                targetAddress.ToInt64();

            if (!currentBranch.Add(targetValue))
                return;

            try
            {
                int scanStep =
                    pointerSize == 8
                        ? 4
                        : pointerSize;

                for (int i = 0;
                     i <= memoryDump.Length - pointerSize;
                     i += scanStep)
                {
                    if (foundPaths.Count >= MaxFoundPaths)
                        break;

                    long pointerValue =
                        ReadPointerValue(
                            memoryDump,
                            i,
                            pointerSize);

                    if (pointerValue == 0)
                        continue;

                    long difference =
                        targetValue -
                        pointerValue;

                    if (difference < -MaxScanOffset ||
                        difference > MaxScanOffset)
                    {
                        continue;
                    }

                    if (difference % 4 != 0)
                        continue;

                    int offset =
                        (int)difference;

                    long pointerStorageAddress =
                        memoryBase.ToInt64() +
                        i;

                    IntPtr addressOfPointer =
                        new IntPtr(
                            pointerStorageAddress);

                    var newOffsets =
                        new List<int>(
                            currentOffsets.Count + 1);

                    newOffsets.Add(offset);
                    newOffsets.AddRange(
                        currentOffsets);

                    long moduleStart =
                        _mainModule.BaseAddress.ToInt64();

                    long moduleEnd =
                        moduleStart +
                        _mainModule.ModuleMemorySize;

                    if (pointerStorageAddress >= moduleStart &&
                        pointerStorageAddress < moduleEnd)
                    {
                        foundPaths.Add(
                            new PointerPath
                            {
                                ModuleName =
                                    _mainModule.ModuleName,

                                BaseOffset =
                                    pointerStorageAddress -
                                    moduleStart,

                                Offsets =
                                    newOffsets
                            });
                    }

                    if (depth > 1)
                    {
                        SearchPointersRecursive(
                            addressOfPointer,
                            newOffsets,
                            depth - 1,
                            memoryDump,
                            memoryBase,
                            pointerSize,
                            currentBranch,
                            foundPaths);
                    }
                }
            }
            finally
            {
                currentBranch.Remove(
                    targetValue);
            }
        }

        public async Task<PointerStabilityResult> CheckPointerStability(
            PointerPath path,
            int checkCount = 10,
            int intervalMs = 100)
        {
            ThrowIfDisposed();

            var result =
                new PointerStabilityResult
                {
                    Path = path
                };

            if (path == null)
            {
                result.Message =
                    "Pointer yolu geçersiz.";

                return result;
            }

            if (!IsProcessAlive())
            {
                result.Message =
                    "Process kapalı veya geçersiz.";

                return result;
            }

            if (checkCount < 2)
                checkCount = 2;

            if (checkCount > 100)
                checkCount = 100;

            if (intervalMs < 10)
                intervalMs = 10;

            if (intervalMs > 10000)
                intervalMs = 10000;

            var addresses =
                new List<IntPtr>();

            var values =
                new List<string>();

            int successfulSamples = 0;

            for (int i = 0;
                 i < checkCount;
                 i++)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    break;

                IntPtr address =
                    ResolveAddressFromPath(
                        path,
                        false);

                if (address != IntPtr.Zero)
                {
                    string value =
                        null;

                    try
                    {
                        lock (_memoryLock)
                        {
                            value =
                                _memoryService.TryReadStringDeep(
                                    address);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(
                            $"Pointer değeri okunamadı: {ex.Message}");
                    }

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        addresses.Add(
                            address);

                        values.Add(
                            value);

                        successfulSamples++;
                    }
                }

                if (i < checkCount - 1)
                {
                    await Task.Delay(
                            intervalMs)
                        .ConfigureAwait(false);
                }
            }

            double successRate =
                (double)successfulSamples /
                checkCount *
                100.0;

            double addressConsistency =
                CalculateConsistency(
                    addresses.Count,
                    addresses.Distinct().Count());

            double valueConsistency =
                CalculateConsistency(
                    values.Count,
                    values.Distinct(
                        StringComparer.Ordinal).Count());

            double stabilityScore =
                successRate * 0.50 +
                addressConsistency * 0.35 +
                valueConsistency * 0.15;

            stabilityScore =
                Math.Max(
                    0,
                    Math.Min(
                        100,
                        stabilityScore));

            bool isStable =
                successRate >= 80 &&
                addressConsistency >= 80 &&
                stabilityScore >= 80;

            IntPtr lastAddress =
                addresses.LastOrDefault();

            result.IsStable =
                isStable;

            result.Message =
                $"Kararlılık: {stabilityScore:F1}% | Başarı: {successRate:F1}% ({successfulSamples}/{checkCount})";

            result.LastKnownAddress =
                lastAddress;

            result.SuccessRate =
                successRate;

            result.AddressConsistency =
                addressConsistency;

            result.ValueConsistency =
                valueConsistency;

            result.StabilityScore =
                stabilityScore;

            if (lastAddress != IntPtr.Zero)
            {
                SetCachedAddress(
                    path,
                    lastAddress);
            }

            _logger?.LogInformation(
                $"Pointer testi tamamlandı: {path} | " +
                $"Başarı %{successRate:F1} | " +
                $"Adres %{addressConsistency:F1} | " +
                $"Değer %{valueConsistency:F1} | " +
                $"Skor %{stabilityScore:F1} | " +
                $"{(isStable ? "KARARLI" : "KARARSIZ")}");

            return result;
        }

        private IntPtr ResolveAddressFromPath(
            PointerPath path,
            bool allowCache = true)
        {
            if (path == null ||
                !IsProcessAlive())
            {
                return IntPtr.Zero;
            }

            if (allowCache)
            {
                IntPtr cached =
                    GetCachedAddress(path);

                if (cached != IntPtr.Zero)
                    return cached;
            }

            lock (_memoryLock)
            {
                try
                {
                    ProcessModule module =
                        _process.Modules
                            .Cast<ProcessModule>()
                            .FirstOrDefault(x =>
                                string.Equals(
                                    x.ModuleName,
                                    path.ModuleName,
                                    StringComparison.OrdinalIgnoreCase));

                    if (module == null)
                    {
                        _logger?.LogWarning(
                            $"Modül bulunamadı: {path.ModuleName}");

                        return IntPtr.Zero;
                    }

                    long current =
                        module.BaseAddress.ToInt64() +
                        path.BaseOffset;

                    int pointerSize =
                        GetTargetPointerSize();

                    IEnumerable<int> offsets =
                        path.Offsets ??
                        Enumerable.Empty<int>();

                    foreach (int offset in offsets)
                    {
                        IntPtr currentAddress =
                            new IntPtr(current);

                        byte[] pointerBytes =
                            _memoryService.ReadBytes(
                                currentAddress,
                                pointerSize);

                        if (pointerBytes == null ||
                            pointerBytes.Length < pointerSize)
                        {
                            return IntPtr.Zero;
                        }

                        long pointerValue =
                            ReadPointerValue(
                                pointerBytes,
                                0,
                                pointerSize);

                        if (pointerValue == 0)
                            return IntPtr.Zero;

                        current =
                            pointerValue +
                            offset;
                    }

                    IntPtr resolvedAddress =
                        new IntPtr(current);

                    if (allowCache)
                    {
                        SetCachedAddress(
                            path,
                            resolvedAddress);
                    }

                    return resolvedAddress;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(
                        "Pointer adresi çözümlenirken hata oluştu.",
                        ex);

                    return IntPtr.Zero;
                }
            }
        }

        private IntPtr GetCachedAddress(
            PointerPath path)
        {
            string key =
                BuildPathKey(path);

            lock (_cacheLock)
            {
                AddressCacheEntry entry;

                if (!_addressCache.TryGetValue(
                    key,
                    out entry))
                {
                    return IntPtr.Zero;
                }

                if (DateTime.UtcNow >
                    entry.ExpiresAtUtc)
                {
                    _addressCache.Remove(key);

                    return IntPtr.Zero;
                }

                return entry.Address;
            }
        }

        private void SetCachedAddress(
            PointerPath path,
            IntPtr address)
        {
            if (path == null ||
                address == IntPtr.Zero)
            {
                return;
            }

            string key =
                BuildPathKey(path);

            lock (_cacheLock)
            {
                RemoveExpiredCacheEntries();

                _addressCache[key] =
                    new AddressCacheEntry
                    {
                        Address =
                            address,

                        ExpiresAtUtc =
                            DateTime.UtcNow +
                            AddressCacheLifetime
                    };
            }
        }

        private void RemoveExpiredCacheEntries()
        {
            DateTime now =
                DateTime.UtcNow;

            string[] keys =
                _addressCache
                    .Where(x =>
                        now >
                        x.Value.ExpiresAtUtc)
                    .Select(x => x.Key)
                    .ToArray();

            foreach (string key in keys)
            {
                _addressCache.Remove(key);
            }
        }

        private static double CalculateConsistency(
            int sampleCount,
            int uniqueCount)
        {
            if (sampleCount <= 0)
                return 0;

            if (sampleCount == 1 ||
                uniqueCount <= 1)
            {
                return 100;
            }

            double variation =
                (double)(uniqueCount - 1) /
                (sampleCount - 1);

            double consistency =
                (1.0 - variation) *
                100.0;

            return Math.Max(
                0,
                Math.Min(
                    100,
                    consistency));
        }

        private long ReadPointerValue(
            byte[] data,
            int index,
            int pointerSize)
        {
            if (pointerSize == 8)
            {
                return BitConverter.ToInt64(
                    data,
                    index);
            }

            return BitConverter.ToUInt32(
                data,
                index);
        }

        private int GetTargetPointerSize()
        {
            if (!Environment.Is64BitOperatingSystem)
                return 4;

            try
            {
                bool isWow64;

                if (IsWow64Process(
                    _process.Handle,
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

        private bool IsProcessAlive()
        {
            try
            {
                return _process != null &&
                       !_process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static string BuildPathKey(
            PointerPath path)
        {
            if (path == null)
                return string.Empty;

            IEnumerable<int> offsets =
                path.Offsets ??
                Enumerable.Empty<int>();

            return
                (path.ModuleName ?? string.Empty)
                    .ToUpperInvariant() +
                "|" +
                path.BaseOffset +
                "|" +
                string.Join(
                    ",",
                    offsets);
        }

        public void ClearCache()
        {
            ThrowIfDisposed();

            lock (_cacheLock)
            {
                _addressCache.Clear();
            }
        }

        public int CachedPathCount
        {
            get
            {
                ThrowIfDisposed();

                lock (_cacheLock)
                {
                    RemoveExpiredCacheEntries();

                    return _addressCache.Count;
                }
            }
        }

        public class PointerPathComparer :
            IEqualityComparer<PointerPath>
        {
            public bool Equals(
                PointerPath x,
                PointerPath y)
            {
                if (ReferenceEquals(x, y))
                    return true;

                if (x == null ||
                    y == null)
                {
                    return false;
                }

                if (!string.Equals(
                        x.ModuleName,
                        y.ModuleName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (x.BaseOffset !=
                    y.BaseOffset)
                {
                    return false;
                }

                IEnumerable<int> xOffsets =
                    x.Offsets ??
                    Enumerable.Empty<int>();

                IEnumerable<int> yOffsets =
                    y.Offsets ??
                    Enumerable.Empty<int>();

                return xOffsets.SequenceEqual(
                    yOffsets);
            }

            public int GetHashCode(
                PointerPath obj)
            {
                if (obj == null)
                    return 0;

                unchecked
                {
                    int hash =
                        17;

                    hash =
                        hash * 23 +
                        StringComparer.OrdinalIgnoreCase.GetHashCode(
                            obj.ModuleName ??
                            string.Empty);

                    hash =
                        hash * 23 +
                        obj.BaseOffset.GetHashCode();

                    if (obj.Offsets != null)
                    {
                        foreach (int offset in
                                 obj.Offsets)
                        {
                            hash =
                                hash * 23 +
                                offset.GetHashCode();
                        }
                    }

                    return hash;
                }
            }
        }

        public class ByteArrayComparer :
            IEqualityComparer<byte[]>
        {
            public bool Equals(
                byte[] x,
                byte[] y)
            {
                if (ReferenceEquals(x, y))
                    return true;

                if (x == null ||
                    y == null)
                {
                    return false;
                }

                if (x.Length != y.Length)
                    return false;

                for (int i = 0;
                     i < x.Length;
                     i++)
                {
                    if (x[i] != y[i])
                        return false;
                }

                return true;
            }

            public int GetHashCode(
                byte[] obj)
            {
                if (obj == null)
                    return 0;

                unchecked
                {
                    int hash =
                        17;

                    for (int i = 0;
                         i < obj.Length;
                         i++)
                    {
                        hash =
                            hash * 23 +
                            obj[i];
                    }

                    return hash;
                }
            }

            public double CalculateSimilarity(
                byte[] x,
                byte[] y)
            {
                if (x == null ||
                    y == null)
                {
                    return 0;
                }

                int maxLength =
                    Math.Max(
                        x.Length,
                        y.Length);

                if (maxLength == 0)
                    return 1;

                int minLength =
                    Math.Min(
                        x.Length,
                        y.Length);

                int matchingBytes =
                    0;

                for (int i = 0;
                     i < minLength;
                     i++)
                {
                    if (x[i] == y[i])
                    {
                        matchingBytes++;
                    }
                }

                return
                    (double)matchingBytes /
                    maxLength;
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(
                    ref _disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(PointerScanner));
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

            lock (_cacheLock)
            {
                _addressCache.Clear();
            }
        }

        [DllImport(
            "kernel32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process(
            IntPtr processHandle,
            out bool wow64Process);
    }
}