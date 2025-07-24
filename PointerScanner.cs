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
        public string ModuleName { get; set; } // Genellikle .exe dosyasının adı
        public long BaseOffset { get; set; } // Modülün başlangıcından itibaren sabit offset
        public List<int> Offsets { get; set; } = new List<int>(); // Pointer zincirindeki offsetler (sonuncusu hedefe olan offset)

        public override string ToString()
        {
            // Cheat Engine formatında çıktı verir.
            // oradan okunan değere 0x90 ekleyerek hedef adrese ulaşacağınız anlamına gelir.
            return $"\"{ModuleName}\"+0x{BaseOffset:X}" + (Offsets.Any() ? ", " + string.Join(", ", Offsets.Select(o => "0x" + o.ToString("X"))) : "");
        }
    }

    public class PointerScanner
    {
        // P/Invoke: Bellek okuma
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        private readonly Process _process;
        private readonly ProcessModule _mainModule;
        private readonly ILogger _logger; // Logger eklenebilir

        public PointerScanner(Process process, ILogger logger = null)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
            _mainModule = process.MainModule ?? throw new ArgumentException("Process must have a main module.");
            _logger = logger;
        }
        public async Task<List<PointerPath>> FindPointers(IntPtr targetAddress, int maxDepth = 3, IntPtr? searchRegionStart = null, int? searchRegionSize = null)
        {
            return await Task.Run(() =>
            {
                var paths = new List<PointerPath>();
                var visitedAddresses = new HashSet<IntPtr>(); // Sonsuz döngüyü önlemek için

                // Bellek okuma için gerekli değişkenler
                IntPtr regionStart = searchRegionStart ?? _mainModule.BaseAddress;
                int regionSize = searchRegionSize ?? _mainModule.ModuleMemorySize;
                byte[] memoryDump = new byte[regionSize];

                if (!ReadProcessMemory(_process.Handle, regionStart, memoryDump, memoryDump.Length, out _))
                {
                    _logger?.LogError($"Bellek okunamadı: 0x{regionStart.ToInt64():X} - {_process.ProcessName}");
                    return paths; // Boş liste döndür
                }

                // Hedef adresin doğrudan modüldeki offset'ini bulmaya çalış (çok nadir)
                long relativeTargetAddress = targetAddress.ToInt64() - _mainModule.BaseAddress.ToInt64();
                if (relativeTargetAddress >= 0 && relativeTargetAddress < regionSize)
                {
                    paths.Add(new PointerPath
                    {
                        ModuleName = _mainModule.ModuleName,
                        BaseOffset = relativeTargetAddress,
                        Offsets = new List<int>() // Offset yok, doğrudan adres
                    });
                }
                // Hedef adrese *giden* pointer'ları bulmak için belleği tararız.
                SearchPointersRecursive(targetAddress, new List<int>(), maxDepth, memoryDump, regionStart, visitedAddresses, paths);

                // Benzersiz yolları döndür
                return paths.Distinct(new PointerPathComparer()).ToList();
            });
        }
        /// Geriye dönük pointer araması yapar.
        private void SearchPointersRecursive(IntPtr currentTarget, List<int> currentOffsets, int depth, byte[] memoryDump, IntPtr memoryBase, HashSet<IntPtr> visited, List<PointerPath> foundPaths)
        {
            // Temel durumlar
            if (depth <= 0 || visited.Contains(currentTarget) || currentTarget == IntPtr.Zero) return;
            visited.Add(currentTarget);

            int pointerSize = IntPtr.Size; // 32-bit için 4, 64-bit için 8
            long targetValue = currentTarget.ToInt64(); // Aradığımız değer (pointer'ın göstermesi gereken adres)

            // Bellekteki her adreste pointer olup olmadığını kontrol et
            // 4-byte hizalama genellikle iyidir ve performansı artırır.
            for (int i = 0; i <= memoryDump.Length - pointerSize; i += 4)
            {
                try
                {
                    // Bellekteki değeri oku
                    long potentialPointerValue = (pointerSize == 8)
                        ? BitConverter.ToInt64(memoryDump, i)
                        : BitConverter.ToInt32(memoryDump, i);

                    // Bu değer, hedef adresle eşleşiyor mu? (Yani bu adres, hedefi gösteren bir pointer mı?)
                    if (potentialPointerValue == targetValue)
                    {
                        // Evet, eşleşiyor. Bu adres bir pointer.
                        IntPtr pointerAddress = IntPtr.Add(memoryBase, i); // Pointer'ın bulunduğu adres
                        long relativePointerAddress = pointerAddress.ToInt64() - _mainModule.BaseAddress.ToInt64();

                        // Pointer adresi modül içinde mi?
                        if (relativePointerAddress >= 0 && relativePointerAddress < _mainModule.ModuleMemorySize)
                        {
                            // Modül içi pointer: Kararlı ve tercih edilen yol
                            int calculatedOffset = (int)(targetValue - potentialPointerValue); // Her zaman 0

                            // Yeni offset zinciri oluştur: [yeni_offset] + [önceki_zincir]
                            var newOffsets = new List<int> { calculatedOffset };
                            newOffsets.AddRange(currentOffsets);

                            // PointerPath'i oluştur ve listeye ekle
                            foundPaths.Add(new PointerPath
                            {
                                ModuleName = _mainModule.ModuleName,
                                BaseOffset = relativePointerAddress, // Bu pointer'ın modül içindeki sabit offset'i
                                Offsets = newOffsets // Bu zincire ait tüm offsetler
                            });

                            // Zinciri daha derin araştırmak için özyinelemeyi çağır
                            // Şimdi bu pointer adresine (pointerAddress) giden yolları aramalıyız.
                            if (depth > 1)
                            {
                                SearchPointersRecursive(
                                    pointerAddress, // Yeni hedef: bu pointer adresi
                                    newOffsets,     // Bu hedef için yeni offset zinciri
                                    depth - 1,      // Derinliği azalt
                                    memoryDump,
                                    memoryBase,
                                    visited,
                                    foundPaths
                                );
                            }
                        }
                        else // Pointer adresi modül dışında (örneğin heap).
                        {
                            // Modül dışı adres: Daha az kararlı, mutlak adres olarak ele alındı.
                            int calculatedOffset = (int)(targetValue - potentialPointerValue); // Her zaman 0

                            var newOffsets = new List<int> { calculatedOffset };
                            newOffsets.AddRange(currentOffsets);

                            // Modül dışı adres için özel işaret ve mutlak adres
                            foundPaths.Add(new PointerPath
                            {
                                ModuleName = "[EXTERNAL]", // Modül dışı olduğunu belirtmek için özel işaret
                                BaseOffset = pointerAddress.ToInt64(), // Mutlak adres
                                Offsets = newOffsets
                            });

                            // Zinciri daha derin araştırmak için özyinelemeyi çağır
                            if (depth > 1)
                            {
                                SearchPointersRecursive(
                                    pointerAddress, // Yeni hedef: bu pointer adresi
                                    newOffsets,     // Bu hedef için yeni offset zinciri
                                    depth - 1,      // Derinliği azalt
                                    memoryDump,
                                    memoryBase,
                                    visited,
                                    foundPaths
                                );
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Bit dönüştürme hatası gibi şeyler için sessizce devam et veya logla
                    _logger?.LogError($"Memory processing error at index 0x{i:X}: {ex.Message}");
                }
            }
        }
    }

    // PointerPath nesnelerinin benzersiz olup olmadığını kontrol etmek için comparer
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