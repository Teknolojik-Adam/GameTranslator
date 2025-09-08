using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace P5S_ceviri
{
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

    public class PointerScanner
    {
        private readonly Process _process;
        private readonly ProcessModule _mainModule;
        private readonly ILogger _logger;

        public PointerScanner(Process process, ILogger logger = null)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
            _mainModule = process.MainModule ?? throw new ArgumentException("Sürecin bir ana modülü olmalıdır.", nameof(process));
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

                //  Doğrudan offset (Pointer olmayan)
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

            for (int i = 0; i <= memoryDump.Length - pointerSize; i += 4)
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
}
