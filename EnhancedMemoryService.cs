using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace P5S_ceviri 
{
  
    public class EnhancedMemoryService : MemoryService 
    {
        public event Action<string> StatusChanged;
        public event Action<int> ProgressChanged; 

        public EnhancedMemoryService(ILogger logger) : base(logger)
        {
        }

        protected virtual void ReportStatus(string status)
        {
            StatusChanged?.Invoke(status);
        }

        protected virtual void ReportProgress(int progress)
        {
            ProgressChanged?.Invoke(progress);
        }

        // txt dosyasından alınan ve düzeltilen FindPatternAddressesAsync metodu
        public async Task<List<IntPtr>> FindPatternAddressesAsync(Process process, string pattern, CancellationToken ct, IProgress<int> progress = null)
        {
            if (process == null || string.IsNullOrWhiteSpace(pattern)) return new List<IntPtr>();

            try
            {
                ReportStatus("Pattern ayrıştırılıyor...");
                var parsedPattern = ParsePattern(pattern);
                if (parsedPattern.bytes == null || parsedPattern.bytes.Length == 0)
                {
                    ReportStatus("Geçersiz pattern!");
                    _logger.LogError($"Geçersiz pattern formatı: {pattern}");
                    return new List<IntPtr>();
                }

                var module = process.MainModule;
                if (module == null)
                {
                    ReportStatus("Ana modül bulunamadı!");
                    _logger.LogError("Ana modül bulunamadı.");
                    return new List<IntPtr>();
                }

                var memory = new byte[module.ModuleMemorySize];
                ReportStatus("Bellek okunuyor...");
                if (!ReadProcessMemory(process.Handle, module.BaseAddress, memory, memory.Length, out _))
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    ReportStatus("Bellek okuma hatası!");
                    _logger.LogError($"Bellek okuma hatası! Hata kodu: {errorCode}");
                    return new List<IntPtr>();
                }

                ReportStatus("Tarama başlatıldı...");
                var results = new List<IntPtr>();
                int total = memory.Length - parsedPattern.bytes.Length;

                return await Task.Run(() =>
                {
                    for (int i = 0; i <= total; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (MatchesWithMask(memory, i, parsedPattern.bytes, parsedPattern.masks))
                            results.Add(IntPtr.Add(module.BaseAddress, i));

                        if (progress != null && i % 100000 == 0)
                            progress.Report((int)((double)i / total * 100));

                        if (i % 500000 == 0) // UI'yi çok sık güncellememek için
                            ReportProgress((int)((double)i / total * 100));
                    }
                    ReportStatus($"Tarama tamamlandı. {results.Count} adet sonuç bulundu.");
                    _logger.LogInformation($"Pattern taraması tamamlandı. {results.Count} adet sonuç bulundu.");
                    return results;
                }, ct);
            }
            catch (OperationCanceledException)
            {
                ReportStatus("Pattern taraması kullanıcı tarafından durduruldu.");
                _logger.LogInformation("Pattern taraması kullanıcı tarafından durduruldu.");
                return new List<IntPtr>();
            }
            catch (Exception ex)
            {
                ReportStatus($"Tarama sırasında hata: {ex.Message}");
                _logger.LogError($"Tarama sırasında hata: {ex.Message}", ex);
                return new List<IntPtr>();
            }
        }

        private (byte[] bytes, bool[] masks) ParsePattern(string pattern)
        {
            var parts = pattern.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var bytes = new List<byte>();
            var masks = new List<bool>();

            foreach (var p in parts)
            {
                if (p == "??" || p == "?")
                {
                    bytes.Add(0);
                    masks.Add(false);
                }
                else if (byte.TryParse(p, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                {
                    bytes.Add(b);
                    masks.Add(true);
                }
                else
                {
                    _logger.LogError($"Geçersiz pattern parçası: {p}");
                   
                    bytes.Add(0); // Varsayılan olarak 0 ekleyip maskesiz yapabiliriz
                    masks.Add(false);
                }
            }
            return (bytes.ToArray(), masks.ToArray());
        }

        private bool MatchesWithMask(byte[] buffer, int offset, byte[] pattern, bool[] masks)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                if (masks[i] && buffer[offset + i] != pattern[i])
                    return false;
            }
            return true;
        }
    }
}