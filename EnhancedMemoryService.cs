using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public class EnhancedMemoryService : MemoryService
    {
        public event Action<string> StatusChanged;
        public event Action<int> ProgressChanged; // 0-100

        public EnhancedMemoryService(ILogger logger) : base(logger)
        {
        }

        private void ReportProgress(int value) => ProgressChanged?.Invoke(value);
        private void ReportStatus(string status) => StatusChanged?.Invoke(status);

        public async Task<List<IntPtr>> FindPatternAddressesAsync(Process process, string pattern, CancellationToken ct, IProgress<int> progress = null)
        {
            ReportStatus("Pattern aranıyor...");
            (byte[] bytes, bool[] masks) parsedPattern;
            try
            {
                parsedPattern = ParsePattern(pattern);
            }
            catch (ArgumentException ex)
            {
                ReportStatus($"Hata: {ex.Message}");
                AppendToLog($"Pattern ayrıştırma hatası: {ex.Message}"); // Logger'a protected erişim yoksa _logger'ı private yapıp property ile erişim sağlayın
                return new List<IntPtr>();
            }

            var module = process.MainModule;
            var memory = new byte[module.ModuleMemorySize];

            ReportStatus("Bellek okunuyor...");
            if (!ReadProcessMemory(process.Handle, module.BaseAddress, memory, memory.Length, out _))
            {
                ReportStatus("Bellek okuma hatası!");
                AppendToLog($"Bellek okuma hatası! Hata kodu: {Marshal.GetLastWin32Error()}"); // Aynı şekilde burada da _logger'a property ile erişin
                return new List<IntPtr>();
            }

            return await Task.Run(() =>
            {
                ReportStatus("Tarama başlatıldı...");
                var results = new List<IntPtr>();
                int total = memory.Length - parsedPattern.bytes.Length;
                for (int i = 0; i <= total; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (MatchesWithMask(memory, i, parsedPattern.bytes, parsedPattern.masks))
                        results.Add(IntPtr.Add(module.BaseAddress, i));

                    if (progress != null && i % 100000 == 0)
                        progress.Report((int)((double)i / total * 100));

                    ReportProgress((int)((double)i / total * 100));
                }
                ReportStatus($"Tarama tamamlandı. {results.Count} adet sonuç bulundu.");
                AppendToLog($"Pattern taraması tamamlandı. {results.Count} adet sonuç bulundu."); // Ve burada
                return results;
            }, ct);
        }

        private void AppendToLog(string v)
        {
            throw new NotImplementedException();
        }

        private (byte[] bytes, bool[] masks) ParsePattern(string pattern)
        {
            var parts = pattern.Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
            var bytes = new List<byte>();
            var masks = new List<bool>();

            foreach (var p in parts)
            {
                if (p == "??" || p == "?")
                {
                    bytes.Add(0);
                    masks.Add(false);
                }
                else if (byte.TryParse(p, NumberStyles.HexNumber, null, out byte b))
                {
                    bytes.Add(b);
                    masks.Add(true);
                }
                else
                {
                    throw new ArgumentException($"Geçersiz hex: {p}");
                }
            }
            return (bytes.ToArray(), masks.ToArray());
        }

        private bool MatchesWithMask(byte[] memory, int position, byte[] patternBytes, bool[] patternMasks)
        {
            for (int i = 0; i < patternBytes.Length; i++)
            {
                if (patternMasks[i] && memory[position + i] != patternBytes[i])
                    return false;
            }
            return true;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            [Out] byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);
    }
}