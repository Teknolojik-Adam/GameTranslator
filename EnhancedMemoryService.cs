using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public class EnhancedMemoryService : MemoryService
    {
        public event Action<int> ProgressChanged;
        public event Action<string> StatusChanged;

        public EnhancedMemoryService(ILogger logger) : base(logger)
        {
        }

        /// Çoklu encoding ile metin arama
        public async Task<List<IntPtr>> FindStringAddressesMultiEncodingAsync(Process process, string searchText, 
            string encoding = "Unicode", CancellationToken cancellationToken = default, IProgress<int> progress = null)
        {
            if (string.IsNullOrEmpty(searchText) || process == null) 
                return new List<IntPtr>();

            return await Task.Run(() =>
            {
                var results = new List<IntPtr>();
                var mainModule = process.MainModule;
                
                StatusChanged?.Invoke($"Bellek okunuyor ({encoding})...");
                byte[] memoryDump = new byte[mainModule.ModuleMemorySize];

                if (!ReadProcessMemory(process.Handle, mainModule.BaseAddress, memoryDump, memoryDump.Length, out _))
                {
                    StatusChanged?.Invoke("Bellek okuma hatası!");
                    return results;
                }

                byte[] searchBytes = GetSearchBytes(searchText, encoding);
                if (searchBytes == null || searchBytes.Length == 0)
                {
                    StatusChanged?.Invoke("Encoding hatası!");
                    return results;
                }

                StatusChanged?.Invoke($"Tarama başlatıldı - {searchBytes.Length} byte aranıyor...");
                
                int totalSize = memoryDump.Length - searchBytes.Length;
                int lastReportedProgress = 0;

                for (int i = 0; i <= totalSize; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Progress güncelleme (her %1'de bir)
                    int currentProgress = (int)((double)i / totalSize * 100);
                    if (currentProgress > lastReportedProgress)
                    {
                        lastReportedProgress = currentProgress;
                        progress?.Report(currentProgress);
                        ProgressChanged?.Invoke(currentProgress);
                    }

                    if (IsMatchAt(memoryDump, i, searchBytes))
                    {
                        results.Add(IntPtr.Add(mainModule.BaseAddress, i));
                    }
                }

                StatusChanged?.Invoke($"Tarama tamamlandı - {results.Count} adet adres bulundu");
                progress?.Report(100);
                ProgressChanged?.Invoke(100);
                return results;
            }, cancellationToken);
        }

   
        public async Task<List<FuzzyMatch>> FindFuzzyStringMatchesAsync(Process process, string searchText, 
            string encoding = "Unicode", int tolerance = 2, CancellationToken cancellationToken = default)
        {
            var results = new List<FuzzyMatch>();
            var mainModule = process.MainModule;
            
            return await Task.Run(() =>
            {
                byte[] memoryDump = new byte[mainModule.ModuleMemorySize];
                if (!ReadProcessMemory(process.Handle, mainModule.BaseAddress, memoryDump, memoryDump.Length, out _))
                    return results;

                byte[] searchBytes = GetSearchBytes(searchText, encoding);
                if (searchBytes == null) return results;

                for (int i = 0; i <= memoryDump.Length - searchBytes.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    int differences = CountDifferences(memoryDump, i, searchBytes);
                    if (differences <= tolerance)
                    {
                        var address = IntPtr.Add(mainModule.BaseAddress, i);
                        var foundText = ExtractStringAt(memoryDump, i, searchBytes.Length, encoding);
                        
                        results.Add(new FuzzyMatch
                        {
                            Address = address,
                            FoundText = foundText,
                            Differences = differences,
                            Similarity = (double)(searchBytes.Length - differences) / searchBytes.Length * 100
                        });
                    }
                }
                return results.OrderBy(x => x.Differences).ToList();
            }, cancellationToken);
        }


        public async Task<List<PatternMatch>> FindPatternMatchesAsync(Process process, string pattern, 
            CancellationToken cancellationToken = default)
        {
            // Basit pattern matching - geliştirillebilir
            var results = new List<PatternMatch>();
            var mainModule = process.MainModule;

            return await Task.Run(() =>
            {
                byte[] memoryDump = new byte[mainModule.ModuleMemorySize];
                if (!ReadProcessMemory(process.Handle, mainModule.BaseAddress, memoryDump, memoryDump.Length, out _))
                    return results;

                // Örnek: "Hello*World" -> "Hello" ile başlayıp "World" ile biten metinler
                if (pattern.Contains("*"))
                {
                    var parts = pattern.Split('*');
                    if (parts.Length == 2)
                    {
                        results.AddRange(FindPatternWithWildcard(memoryDump, mainModule.BaseAddress, 
                            parts[0], parts[1], cancellationToken));
                    }
                }

                return results;
            }, cancellationToken);
        }

                 private byte[] GetSearchBytes(string text, string encoding)
         {
             try
             {
                 switch (encoding.ToLower())
                 {
                     case "unicode":
                         return Encoding.Unicode.GetBytes(text);
                     case "utf-8":
                         return Encoding.UTF8.GetBytes(text);
                     case "ascii":
                         return Encoding.ASCII.GetBytes(text);
                     case "shift-jis":
                         return Encoding.GetEncoding("Shift-JIS").GetBytes(text);
                     default:
                         return Encoding.Unicode.GetBytes(text);
                 }
             }
             catch
             {
                 return Encoding.Unicode.GetBytes(text); // Fallback
             }
         }

        private bool IsMatchAt(byte[] memory, int offset, byte[] pattern)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                if (memory[offset + i] != pattern[i])
                    return false;
            }
            return true;
        }

        private int CountDifferences(byte[] memory, int offset, byte[] pattern)
        {
            int differences = 0;
            for (int i = 0; i < pattern.Length && offset + i < memory.Length; i++)
            {
                if (memory[offset + i] != pattern[i])
                    differences++;
            }
            return differences;
        }

                 private string ExtractStringAt(byte[] memory, int offset, int length, string encoding)
         {
             try
             {
                 byte[] data = new byte[length];
                 Array.Copy(memory, offset, data, 0, Math.Min(length, memory.Length - offset));
                 
                 switch (encoding.ToLower())
                 {
                     case "unicode":
                         return Encoding.Unicode.GetString(data).Split('\0')[0];
                     case "utf-8":
                         return Encoding.UTF8.GetString(data).Split('\0')[0];
                     case "ascii":
                         return Encoding.ASCII.GetString(data).Split('\0')[0];
                     default:
                         return Encoding.Unicode.GetString(data).Split('\0')[0];
                 }
             }
             catch
             {
                 return "[Decode Error]";
             }
         }

        private List<PatternMatch> FindPatternWithWildcard(byte[] memory, IntPtr baseAddress, 
            string start, string end, CancellationToken cancellationToken)
        {
            var results = new List<PatternMatch>();
            var startBytes = Encoding.Unicode.GetBytes(start);
            var endBytes = Encoding.Unicode.GetBytes(end);

            for (int i = 0; i <= memory.Length - startBytes.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                if (IsMatchAt(memory, i, startBytes))
                {
                    // Start bulundu, şimdi end'i ara
                    for (int j = i + startBytes.Length; j <= memory.Length - endBytes.Length; j++)
                    {
                        if (IsMatchAt(memory, j, endBytes))
                        {
                            // Pattern bulundu
                            var address = IntPtr.Add(baseAddress, i);
                            var length = j + endBytes.Length - i;
                            var text = ExtractStringAt(memory, i, length, "unicode");
                            
                            results.Add(new PatternMatch
                            {
                                Address = address,
                                MatchedText = text,
                                Length = length
                            });
                            break; // İlk eşleşmeyi al
                        }
                    }
                }
            }
            return results;
        }

        // P/Invoke - parent class'tan alıyoruz
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, 
            [Out] byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);
    }

    public class FuzzyMatch
    {
        public IntPtr Address { get; set; }
        public string FoundText { get; set; }
        public int Differences { get; set; }
        public double Similarity { get; set; }
    }

    public class PatternMatch
    {
        public IntPtr Address { get; set; }
        public string MatchedText { get; set; }
        public int Length { get; set; }
    }
} 