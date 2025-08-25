using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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

        public async Task<List<IntPtr>> FindPatternAddressesAsync(Process process, string pattern, CancellationToken ct, IProgress<int> progress = null)
        {
            if (process == null || string.IsNullOrWhiteSpace(pattern)) return new List<IntPtr>();

            try
            {
                ReportStatus("Pattern ayrıştırılıyor...");
                var parsedPattern = ParsePattern(pattern);
                if (parsedPattern == null)
                {
                    ReportStatus("Geçersiz pattern!");
                    _logger.LogError($"Geçersiz pattern formatı: {pattern}");
                    return new List<IntPtr>();
                }
                var (patternBytes, patternMask) = parsedPattern.Value;

                var module = process.MainModule;
                if (module == null)
                {
                    ReportStatus("Ana modül bulunamadı!");
                    _logger.LogError("Ana modül bulunamadı.");
                    return new List<IntPtr>();
                }

                ReportStatus("Bellek bölgeleri taranıyor...");
                var results = new ConcurrentBag<IntPtr>();
                long moduleEnd = module.BaseAddress.ToInt64() + module.ModuleMemorySize;
                long totalBytesToScan = module.ModuleMemorySize;
                long totalBytesScanned = 0;
                int chunkSize = 4 * 1024 * 1024; // 4 MB

                var chunks = new List<(long address, int size)>();
                for (long currentBase = module.BaseAddress.ToInt64(); currentBase < moduleEnd; currentBase += chunkSize)
                {
                    chunks.Add((currentBase, chunkSize));
                }

                await Task.Run(() =>
                {
                    Parallel.ForEach(chunks, new ParallelOptions { CancellationToken = ct }, (chunk, loopState) =>
                    {
                        var buffer = new byte[chunk.size + patternBytes.Length];
                        if (!ReadProcessMemory(process.Handle, (IntPtr)chunk.address, buffer, buffer.Length, out _))
                        {
                            // Bu bölge okunamadı, atla.
                            return;
                        }

                        for (int i = 0; i < chunk.size; i++)
                        {
                            if (ct.IsCancellationRequested)
                            {
                                loopState.Stop();
                                return;
                            }

                            if (MatchesWithMask(buffer, i, patternBytes, patternMask))
                            {
                                results.Add(new IntPtr(chunk.address + i));
                            }
                        }

                        long scanned = Interlocked.Add(ref totalBytesScanned, chunk.size);
                        ReportProgress((int)((double)scanned / totalBytesToScan * 100));
                    });
                }, ct);

                ct.ThrowIfCancellationRequested();

                ReportStatus($"Tarama tamamlandı. {results.Count} adet sonuç bulundu.");
                _logger.LogInformation($"Pattern taraması tamamlandı. {results.Count} adet sonuç bulundu.");
                return results.ToList();
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

        private (byte[] bytes, bool[] masks)? ParsePattern(string pattern)
        {
            var parts = pattern.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var bytes = new List<byte>();
            var masks = new List<bool>();

            foreach (var p in parts)
            {
                if (p == "??" || p == "?")
                {
                    bytes.Add(0);
                    masks.Add(false);
                }
                else if (byte.TryParse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    bytes.Add(b);
                    masks.Add(true);
                }
                else
                {
                    _logger.LogError($"Geçersiz pattern parçası: {p}");
                    return null; // Geçersiz pattern'de taramayı durdur.
                }
            }
            return (bytes.ToArray(), masks.ToArray());
        }

        private bool MatchesWithMask(byte[] buffer, int offset, byte[] pattern, bool[] masks)
        {
            if (offset + pattern.Length > buffer.Length) return false;

            for (int i = 0; i < pattern.Length; i++)
            {
                if (masks[i] && buffer[offset + i] != pattern[i])
                    return false;
            }
            return true;
        }
    }
}