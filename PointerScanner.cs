using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace P5S_ceviri
{
  //
    public class PointerPath
    {
        public string ModuleName { get; set; } = string.Empty;
        public long BaseOffset { get; set; }
        public List<int> Offsets { get; set; } = new List<int>();

        public override string ToString()
        {
            return $"\"{ModuleName}\"+0x{BaseOffset:X}" + (Offsets.Any() ? ", " + string.Join(", ", Offsets.Select(o => "0x" + o.ToString("X"))) : "");
        }
    }

    public class PointerScanner : IDisposable
    {
        private readonly Process _process;
        private readonly ProcessModule _mainModule;
        private readonly ILogger _logger;
        private readonly IMemoryService _memoryService;
        private readonly Dictionary<PointerPath, IntPtr> _addressCache = new Dictionary<PointerPath, IntPtr>(new PointerPathComparer());
        private readonly object _cacheLockObject = new object();
        private bool _disposed = false;

        public PointerScanner(Process process, IMemoryService memoryService, ILogger logger = null)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
            _mainModule = process.MainModule ?? throw new ArgumentException("Sürecin bir ana modülü olmalıdır.", nameof(process));
            _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
            _logger = logger;
        }

        public async Task<List<PointerPath>> FindPointers(IntPtr targetAddress, int maxDepth = 3, IntPtr? searchRegionStart = null, int? searchRegionSize = null)
        {
            return await Task.Run(() =>
            {
                var paths = new List<PointerPath>();
                var visitedAddresses = new HashSet<IntPtr>();

                IntPtr regionStart = searchRegionStart ?? _mainModule.BaseAddress;
                int regionSize = searchRegionSize ?? _mainModule.ModuleMemorySize;
                byte[] memoryDump = new byte[regionSize];

                if (!MemoryService.ReadProcessMemory(_process.Handle, regionStart, memoryDump, memoryDump.Length, out _))
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    _logger?.LogError($"Bellek okunamadı: 0x{regionStart.ToInt64():X} - {_process.ProcessName}. Hata Kodu: {errorCode}");
                    return paths;
                }

                _logger?.LogInformation($"Pointer taraması başlatıldı. Hedef: 0x{targetAddress.ToInt64():X}, Derinlik: {maxDepth}");

                // Doğrudan offset (Pointer olmayan)
                long relativeTargetAddress = targetAddress.ToInt64() - _mainModule.BaseAddress.ToInt64();
                if (relativeTargetAddress >= 0 && relativeTargetAddress < regionSize)
                {
                    var directPath = new PointerPath
                    {
                        ModuleName = _mainModule.ModuleName,
                        BaseOffset = relativeTargetAddress,
                        Offsets = new List<int>()
                    };
                    paths.Add(directPath);
                    _logger?.LogInformation($"Doğrudan adres yolu bulundu: {directPath}");
                }

                // Pointer araması
                SearchPointersRecursive(targetAddress, new List<int>(), maxDepth, memoryDump, regionStart, visitedAddresses, paths);

                var distinctPaths = paths.Distinct(new PointerPathComparer()).ToList();

                _logger?.LogInformation($"Tarama tamamlandı. {distinctPaths.Count} benzersiz pointer yolu bulundu.");
                return distinctPaths;
            });
        }

        private void SearchPointersRecursive(IntPtr currentTarget, List<int> currentOffsets, int depth, byte[] memoryDump, IntPtr memoryBase, HashSet<IntPtr> visited, List<PointerPath> foundPaths)
        {
            if (depth <= 0 || visited.Contains(currentTarget) || currentTarget == IntPtr.Zero) return;
            visited.Add(currentTarget);

            int pointerSize = IntPtr.Size;
            long targetValue = currentTarget.ToInt64();

            for (int i = 0; i <= memoryDump.Length - pointerSize; i += pointerSize)
            {
                try
                {
                    long potentialPointerValue = (pointerSize == 8) ? BitConverter.ToInt64(memoryDump, i) : BitConverter.ToInt32(memoryDump, i);

                    if (potentialPointerValue == targetValue)
                    {
                        IntPtr pointerAddress = IntPtr.Add(memoryBase, i);
                        long relativePointerAddress = pointerAddress.ToInt64() - _mainModule.BaseAddress.ToInt64();

                        if (relativePointerAddress >= 0 && relativePointerAddress < _mainModule.ModuleMemorySize)
                        {
                            int calculatedOffset = (int)(targetValue - potentialPointerValue);
                            var newOffsets = new List<int> { calculatedOffset };
                            newOffsets.AddRange(currentOffsets);

                            var path = new PointerPath
                            {
                                ModuleName = _mainModule.ModuleName,
                                BaseOffset = relativePointerAddress,
                                Offsets = newOffsets
                            };
                            foundPaths.Add(path);
                            _logger?.LogInformation($"Pointer yolu bulundu: {path}");

                            if (depth > 1)
                            {
                                SearchPointersRecursive(pointerAddress, newOffsets, depth - 1, memoryDump, memoryBase, visited, foundPaths);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Dizinde bellek işleme hatası 0x{i:X}: {ex.Message}");
                }
            }
        }

        public async Task<PointerStabilityResult> CheckPointerStability(PointerPath path, int checkCount = 10, int intervalMs = 100)
        {
            return await Task.Run(async () =>
            {
                var stabilityResult = new PointerStabilityResult
                {
                    Path = path,
                    IsStable = true,
                    SuccessRate = 100.0,
                    AddressConsistency = 100.0,
                    ValueConsistency = 100.0,
                    StabilityScore = 100.0
                };

                if (_process == null || _process.HasExited)
                {
                    stabilityResult.IsStable = false;
                    stabilityResult.Message = "Process kapalı veya geçersiz.";
                    stabilityResult.SuccessRate = 0.0;
                    stabilityResult.AddressConsistency = 0.0;
                    stabilityResult.ValueConsistency = 0.0;
                    stabilityResult.StabilityScore = 0.0;
                    return stabilityResult;
                }

                var addresses = new List<IntPtr>();
                var values = new List<string>();

                for (int i = 0; i < checkCount; i++)
                {
                    IntPtr resolvedAddress = ResolveAddressFromPath(path);
                    if (resolvedAddress == IntPtr.Zero)
                    {
                        stabilityResult.IsStable = false;
                        stabilityResult.Message = $"Adres çözümlenemedi (Deneme {i + 1}/{checkCount})";
                        stabilityResult.SuccessRate = (double)i / checkCount * 100;
                        stabilityResult.AddressConsistency = 0.0;
                        stabilityResult.ValueConsistency = 0.0;
                        stabilityResult.StabilityScore = 0.0;
                        return stabilityResult;
                    }

                    addresses.Add(resolvedAddress);

                    string value = _memoryService.TryReadStringDeep(resolvedAddress);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        stabilityResult.IsStable = false;
                        stabilityResult.Message = $"Değer okunamadı (Deneme {i + 1}/{checkCount})";
                        stabilityResult.SuccessRate = (double)i / checkCount * 100;
                        stabilityResult.AddressConsistency = 0.0;
                        stabilityResult.ValueConsistency = 0.0;
                        stabilityResult.StabilityScore = 0.0;
                        return stabilityResult;
                    }

                    values.Add(value);

                    await Task.Delay(intervalMs);
                }

                // Sonuçları analiz et
                int successfulSamples = checkCount;
                double successRate = (double)successfulSamples / checkCount * 100;

                var distinctAddresses = addresses.Distinct().ToList();
                double addressConsistency = distinctAddresses.Count == 1 ? 100.0 : (double)distinctAddresses.Count / checkCount * 100;

                var distinctValues = values.Distinct().ToList();
                double valueConsistency = distinctValues.Count == 1 ? 100.0 : (double)distinctValues.Count / checkCount * 100;

                double stabilityScore = (successRate * 0.5) + (addressConsistency * 0.3) + (valueConsistency * 0.2);
                bool isStable = stabilityScore >= 80;

                stabilityResult.IsStable = isStable;
                stabilityResult.Message = $"Kararlılık: {successRate:F1}% ({successfulSamples}/{checkCount} başarılı)";
                stabilityResult.LastKnownAddress = addresses.LastOrDefault();
                stabilityResult.SuccessRate = successRate;
                stabilityResult.AddressConsistency = addressConsistency;
                stabilityResult.ValueConsistency = valueConsistency;
                stabilityResult.StabilityScore = stabilityScore;

                // Detaylı log
                _logger?.LogInformation($"=== Pointer Stability Test Sonuçları ===");
                _logger?.LogInformation($"Path: {path}");
                _logger?.LogInformation($"Başarı Oranı: {successRate:F1}%");
                _logger?.LogInformation($"Adres Tutarlılığı: {addressConsistency:F1}% ({distinctAddresses.Count} farklı adres)");
                _logger?.LogInformation($"Değer Tutarlılığı: {valueConsistency:F1}% ({distinctValues.Count} farklı değer)");
                _logger?.LogInformation($"Stabilite Skoru: {stabilityScore:F1}/100");
                _logger?.LogInformation($"Sonuç: {(isStable ? "KARLI ✅" : "KARARSIZ ⚠️")}");
                
                return stabilityResult;
            });
        }

        private IntPtr ResolveAddressFromPath(PointerPath path)
        {
            if (path == null) return IntPtr.Zero;

            lock (_cacheLockObject)
            {
                // Önbellekten kontrol et
                if (_addressCache.TryGetValue(path, out IntPtr cachedAddress))
                {
                    _logger?.LogInformation($"Pointer yolu önbellekten alındı: {path}");
                    return cachedAddress;
                }

                try
                {
                    ProcessModule module = _process.Modules.Cast<ProcessModule>().FirstOrDefault(m => m.ModuleName.Equals(path.ModuleName, StringComparison.OrdinalIgnoreCase));
                    if (module == null)
                    {
                        _logger?.LogWarning($"Modül bulunamadı: {path.ModuleName}");
                        return IntPtr.Zero;
                    }

                    IntPtr currentAddress = IntPtr.Add(module.BaseAddress, (int)path.BaseOffset);
                    _logger?.LogInformation($"Başlangıç adresi: 0x{currentAddress.ToInt64():X} ({module.ModuleName} + 0x{path.BaseOffset:X})");

                    foreach (var offset in path.Offsets)
                    {
                        var pointerBytes = _memoryService.ReadBytes(currentAddress, IntPtr.Size);
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

                    // Önbelleğe kaydet
                    _addressCache[path] = currentAddress;
                    _logger?.LogInformation($"Pointer yolu çözümlendi ve önbelleğe eklendi: {path}");
                    
                    return currentAddress;
                }
                catch (Exception ex)
                {
                    _logger?.LogError("Adres yolu çözümlenirken hata oluştu.", ex);
                    return IntPtr.Zero;
                }
            }
        }

        public void ClearCache()
        {
            lock (_cacheLockObject)
            {
                int count = _addressCache.Count;
                _addressCache.Clear();
                _logger?.LogInformation($"PointerScanner önbelleği temizlendi ({count} adet)");
            }
        }

        public int CachedPathCount
        {
            get
            {
                lock (_cacheLockObject)
                {
                    return _addressCache.Count;
                }
            }
        }
        /// PointerPath nesnelerini karşılaştırmak için Comparer
        public class PointerPathComparer : IEqualityComparer<PointerPath>
        {
            public bool Equals(PointerPath x, PointerPath y)
            {
                if (x == null || y == null) return x == y;
                return x.ModuleName == y.ModuleName &&
                       x.BaseOffset == y.BaseOffset &&
                       x.Offsets.SequenceEqual(y.Offsets);
            }

            public int GetHashCode(PointerPath obj)
            {
                if (obj == null) return 0;
                
                unchecked
                {
                    int hash = 17;
                    hash = hash * 23 + (obj.ModuleName?.GetHashCode() ?? 0);
                    hash = hash * 23 + obj.BaseOffset.GetHashCode();
                    foreach (var offset in obj.Offsets)
                    {
                        hash = hash * 23 + offset.GetHashCode();
                    }
                    return hash;
                }
            }
        }

        /// Byte dizilerini karşılaştırmak için Comparer
        /// Kullanım: Dictionary, HashSet, Distinct, vs.
        public class ByteArrayComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] x, byte[] y)
            {
                if (x == null || y == null) 
                    return x == y;
                
                if (x.Length != y.Length) 
                    return false;
                
                for (int i = 0; i < x.Length; i++)
                {
                    if (x[i] != y[i]) 
                        return false;
                }
                
                return true;
            }

            public int GetHashCode(byte[] obj)
            {
                if (obj == null) 
                    return 0;
                
                unchecked
                {
                    int hash = 17;
                    foreach (var b in obj)
                    {
                        hash = hash * 23 + b.GetHashCode();
                    }
                    return hash;
                }
            }
            /// İki byte dizisi arasındaki benzerlik oranını hesaplar (0.0 - 1.0)
            public double CalculateSimilarity(byte[] x, byte[] y)
            {
                if (x == null || y == null) 
                    return 0.0;

                int minLength = Math.Min(x.Length, y.Length);
                int maxLength = Math.Max(x.Length, y.Length);
                
                if (maxLength == 0) 
                    return 0.0;

                int matchingBytes = 0;
                for (int i = 0; i < minLength; i++)
                {
                    if (x[i] == y[i]) 
                        matchingBytes++;
                }

                return (double)matchingBytes / maxLength;
            }
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
                    lock (_cacheLockObject)
                    {
                        _addressCache.Clear();
                    }
                    _logger?.LogInformation("PointerScanner kapatıldı");
                }
                _disposed = true;
            }
        }

        ~PointerScanner()
        {
            Dispose(false);
        }
    }
}