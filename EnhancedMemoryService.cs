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

        public EnhancedMemoryService(ILogger logger) : base(logger, new AppSettings(logger))
        {
            _logger.LogInformation("EnhancedMemoryService başlatıldı");
        }

        public EnhancedMemoryService(ILogger logger, AppSettings appSettings) : base(logger, appSettings)
        {
            _logger.LogInformation("EnhancedMemoryService başlatıldı (AppSettings ile)");
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
            return await FindPatternAddressesAsync(process, pattern, ct, progress, 4 * 1024 * 1024, 1024, true);
        }

        public async Task<List<IntPtr>> FindPatternAddressesAsync(Process process, string pattern, CancellationToken ct, IProgress<int> progress, int chunkSize)
        {
            return await FindPatternAddressesAsync(process, pattern, ct, progress, chunkSize, 1024, true);
        }

        public async Task<List<IntPtr>> FindPatternAddressesAsync(Process process, string pattern, CancellationToken ct, IProgress<int> progress, int chunkSize, int bufferSize)
        {
            return await FindPatternAddressesAsync(process, pattern, ct, progress, chunkSize, bufferSize, true);
        }

        public async Task<List<IntPtr>> FindPatternAddressesAsync(Process process, string pattern, CancellationToken ct, IProgress<int> progress, int chunkSize, int bufferSize, bool useOverlappingBuffers)
        {
            if (process == null || string.IsNullOrWhiteSpace(pattern))
            {
                ReportStatus("Geçersiz giriş parametreleri!");
                _logger.LogWarning("Geçersiz giriş parametreleri!");
                return new List<IntPtr>();
            }

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

                var chunks = new List<(long address, int size)>();
                for (long currentBase = module.BaseAddress.ToInt64(); currentBase < moduleEnd; currentBase += chunkSize)
                {
                    int remainingBytes = (int)(moduleEnd - currentBase);
                    chunks.Add((currentBase, Math.Min(chunkSize, remainingBytes)));
                }

                await Task.Run(() =>
                {
                    Parallel.ForEach(chunks, new ParallelOptions { CancellationToken = ct }, (chunk, loopState) =>
                    {
                        int bufferOverlap = useOverlappingBuffers ? patternBytes.Length - 1 : 0;
                        int bufferCapacity = bufferSize + bufferOverlap;
                        var buffer = new byte[bufferCapacity];
                        int bytesRead = 0;

                        for (long currentAddress = chunk.address; currentAddress < chunk.address + chunk.size; currentAddress += bufferSize - bufferOverlap)
                        {
                            if (ct.IsCancellationRequested)
                            {
                                loopState.Stop();
                                return;
                            }

                            int remainingBytes = (int)(chunk.address + chunk.size - currentAddress);
                            int readSize = Math.Min(bufferSize, remainingBytes);

                            if (!ReadProcessMemory(process.Handle, (IntPtr)currentAddress, buffer, readSize, out bytesRead) || bytesRead == 0)
                            {
                                _logger.LogWarning($"Bellek okuma başarısız: Adres 0x{currentAddress:X}, Okunan Byte {bytesRead}");
                                continue;
                            }

                            for (int i = 0; i < bytesRead - bufferOverlap; i++)
                            {
                                if (ct.IsCancellationRequested)
                                {
                                    loopState.Stop();
                                    return;
                                }

                                if (MatchesWithMask(buffer, i, patternBytes, patternMask))
                                {
                                    results.Add(new IntPtr(currentAddress + i));
                                }
                            }

                            long scanned = Interlocked.Add(ref totalBytesScanned, readSize);
                            int currentProgress = (int)((double)scanned / totalBytesToScan * 100);
                            ReportProgress(currentProgress);
                            progress?.Report(currentProgress);
                        }
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
                    return null;
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

        public new bool AttachToProcess(int processId)
        {
            var success = base.AttachToProcess(processId);
            if (success)
            {
                try
                {
                    var process = Process.GetProcessById(processId);
                    ReportStatus($"Process'e başarıyla bağlanıldı (ID: {processId}, Adı: {process?.ProcessName ?? "Bilinmiyor"}).");
                }
                catch
                {
                    ReportStatus($"Process'e başarıyla bağlanıldı (ID: {processId}).");
                }
            }
            else
            {
                ReportStatus($"Process'e bağlanılamadı (ID: {processId}).");
            }
            return success;
        }

        public new void Dispose()
        {
            base.Dispose();
            ReportStatus("EnhancedMemoryService disposed.");
            _logger.LogInformation("EnhancedMemoryService disposed.");
        }

        public void ReportStatusWithTimestamp(string status)
        {
            ReportStatus($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {status}");
        }

        public void ReportProgressWithTimestamp(int progress)
        {
            ReportProgress(progress);
            ReportStatusWithTimestamp($"İlerleme: {progress}%");
        }
    }

 
}