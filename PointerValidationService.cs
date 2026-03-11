using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Text;

namespace GameTranslatorUltimate
{
    public class PointerValidationResult
    {
        public PointerPath Path { get; set; }
        public bool IsValid { get; set; }
        public string CurrentValue { get; set; }
        public string ExpectedValue { get; set; }
        public string ErrorMessage { get; set; }
        public int Score { get; set; }
        public TimeSpan ResponseTime { get; set; }
        public IntPtr ResolvedAddress { get; set; }
    }

    // KararlÄ±lÄ±k testi iÃ§in Ã¶rnek veri yapÄ±sÄ±
    public class StabilitySample
    {
        public DateTime Timestamp { get; set; }
        public IntPtr Address { get; set; }
        public string Value { get; set; }
        public bool IsSuccessful { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class PointerValidationService : IDisposable
    {
        private readonly IMemoryService _memoryService;
        private readonly ILogger _logger;
        private readonly AppSettings _appSettings;
        private readonly Dictionary<PointerPath, PointerValidationResult> _validationCache = new Dictionary<PointerPath, PointerValidationResult>();
        private readonly object _cacheLockObject = new object();
        private bool _disposed = false;

        public PointerValidationService(IMemoryService memoryService, ILogger logger, AppSettings appSettings)
        {
            _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        }

        public async Task<List<PointerValidationResult>> ValidatePointersAsync(Process process, List<PointerPath> paths, string expectedText = null)
        {
            var results = new List<PointerValidationResult>();

            if (!_memoryService.AttachToProcess(process.Id))
            {
                _logger.LogError("Process'e baÄŸlanÄ±lamadÄ± - pointer validation baÅŸarÄ±sÄ±z");
                return results;
            }

            // Paralel validasyon iÃ§in Task.WhenAll kullan
            var tasks = paths.Select(path => ValidateSinglePointerAsync(process, path, expectedText));
            results.AddRange(await Task.WhenAll(tasks));

            return results.OrderByDescending(r => r.Score).ToList();
        }

        public async Task<PointerValidationResult> ValidateSinglePointerAsync(Process process, PointerPath path, string expectedText)
        {
            if (_disposed)
            {
                _logger.LogWarning("PointerValidationService dispose edilmiÅŸ durumda. Ä°ÅŸlem reddedildi.");
                return new PointerValidationResult { Path = path, ErrorMessage = "Servis dispose edilmiÅŸ durumda." };
            }

            var result = new PointerValidationResult
            {
                Path = path,
                IsValid = false,
                Score = 0
            };

            var stopwatch = Stopwatch.StartNew();
            try
            {
                // Ã–nbellekten kontrol et
                lock (_cacheLockObject)
                {
                    if (_validationCache.TryGetValue(path, out var cachedResult))
                    {
                        _logger.LogInformation($"Pointer yolu Ã¶nbellekten alÄ±ndÄ±: {path}");
                        return cachedResult;
                    }
                }

                var pathInfo = new PathInfo
                {
                    BaseAddressModule = path.ModuleName,
                    BaseAddressOffset = path.BaseOffset,
                    PointerOffsets = path.Offsets
                };

                var resolvedAddress = _memoryService.ResolveAddressFromPathCached(process, pathInfo);
                if (resolvedAddress == IntPtr.Zero)
                {
                    result.ErrorMessage = "Adres Ã§Ã¶zÃ¼mlenemedi";
                    return result;
                }

                result.ResolvedAddress = resolvedAddress;
                result.CurrentValue = _memoryService.TryReadStringDeep(resolvedAddress);

                stopwatch.Stop();
                result.ResponseTime = stopwatch.Elapsed;

                if (string.IsNullOrWhiteSpace(result.CurrentValue))
                {
                    result.ErrorMessage = "BoÅŸ veya geÃ§ersiz veri okundu";
                    result.Score = 0;
                }
                else if (!string.IsNullOrEmpty(expectedText))
                {
                    result.ExpectedValue = expectedText;
                    if (result.CurrentValue.Contains(expectedText))
                    {
                        result.IsValid = true;
                        result.Score = 100;
                    }
                    else
                    {
                        result.ErrorMessage = "Beklenen metin bulunamadÄ±";
                        result.Score = 10;
                    }
                }
                else
                {
                    result.IsValid = true;
                    result.Score = 50;
                }

                // Ã–nbelleÄŸe kaydet
                lock (_cacheLockObject)
                {
                    _validationCache[path] = result;
                }

                _logger.LogInformation($"Pointer doÄŸrulama tamamlandÄ±: {path}. SonuÃ§: {result.IsValid}, Puan: {result.Score}");
                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.ResponseTime = stopwatch.Elapsed;
                result.ErrorMessage = $"DoÄŸrulama hatasÄ±: {ex.Message}";
                _logger.LogError($"Pointer doÄŸrulama hatasÄ±: {ex.Message}", ex);
                return result;
            }
        }

        public async Task<PointerStabilityResult> TestPointerStabilityAsync(Process process, PointerPath path, int testDurationSeconds = 15, int sampleIntervalMs = 500)
        {
            if (_disposed)
            {
                _logger.LogWarning("PointerValidationService dispose edilmiÅŸ durumda. Ä°ÅŸlem reddedildi.");
                return new PointerStabilityResult { Path = path, Message = "Servis dispose edilmiÅŸ durumda." };
            }

            _logger.LogInformation($"Pointer kararlÄ±lÄ±k testi baÅŸlatÄ±ldÄ±. SÃ¼re: {testDurationSeconds}s, AralÄ±k: {sampleIntervalMs}ms");

            var samples = new List<StabilitySample>();
            var endTime = DateTime.Now.AddSeconds(testDurationSeconds);

            while (DateTime.Now < endTime)
            {
                var sample = new StabilitySample { Timestamp = DateTime.Now };
                try
                {
                    var pathInfo = new PathInfo
                    {
                        BaseAddressModule = path.ModuleName,
                        BaseAddressOffset = path.BaseOffset,
                        PointerOffsets = path.Offsets
                    };

                    var address = _memoryService.ResolveAddressFromPathCached(process, pathInfo);
                    sample.Address = address;

                    if (address != IntPtr.Zero)
                    {
                        sample.Value = _memoryService.TryReadStringDeep(address);
                        sample.IsSuccessful = !string.IsNullOrEmpty(sample.Value);
                        if (!sample.IsSuccessful) sample.ErrorMessage = "BoÅŸ veri okundu";
                    }
                    else
                    {
                        sample.ErrorMessage = "Adres Ã§Ã¶zÃ¼mlenemedi";
                        sample.IsSuccessful = false;
                    }
                }
                catch (Exception ex)
                {
                    sample.ErrorMessage = ex.Message;
                    sample.IsSuccessful = false;
                }

                samples.Add(sample);
                await Task.Delay(sampleIntervalMs);
            }

            // SonuÃ§larÄ± analiz et
            int successfulSamples = samples.Count(s => s.IsSuccessful);
            double successRate = samples.Count > 0 ? (double)successfulSamples / samples.Count * 100 : 0;

            var validSamples = samples.Where(s => s.IsSuccessful).ToList();
            int uniqueAddresses = validSamples.Select(s => s.Address).Distinct().Count();
            double addressConsistency = validSamples.Any() ? (double)uniqueAddresses / validSamples.Count * 100 : 0.0;

            int uniqueValues = validSamples.Select(s => s.Value).Distinct().Count();
            double valueConsistency = validSamples.Any() ? (double)uniqueValues / validSamples.Count * 100 : 0.0;

            double stabilityScore = (successRate * 0.5) + (addressConsistency * 0.3) + (valueConsistency * 0.2);
            bool isStable = stabilityScore >= 80;

            var result = new PointerStabilityResult
            {
                Path = path,
                IsStable = isStable,
                Message = $"KararlÄ±lÄ±k: {successRate:F1}% ({successfulSamples}/{samples.Count} baÅŸarÄ±lÄ±)",
                LastKnownAddress = samples.LastOrDefault()?.Address ?? IntPtr.Zero,
                SuccessRate = successRate,
                AddressConsistency = addressConsistency,
                ValueConsistency = valueConsistency,
                StabilityScore = stabilityScore
            };

            _logger.LogInformation($"Pointer kararlÄ±lÄ±k testi tamamlandÄ±. {path}: {result.Message}");
            return result;
        }

        public List<PointerPath> GetRegisteredPointerPaths()
        {
            if (_disposed)
            {
                _logger.LogWarning("PointerValidationService dispose edilmiÅŸ durumda. Ä°ÅŸlem reddedildi.");
                return new List<PointerPath>();
            }

            lock (_cacheLockObject)
            {
                return _validationCache.Keys.ToList();
            }
        }

        public void InvalidatePointerCache(PointerPath path)
        {
            if (_disposed)
            {
                _logger.LogWarning("PointerValidationService dispose edilmiÅŸ durumda. Ä°ÅŸlem reddedildi.");
                return;
            }

            lock (_cacheLockObject)
            {
                if (_validationCache.ContainsKey(path))
                {
                    _validationCache.Remove(path);
                    _logger.LogInformation($"Pointer yolunun Ã¶nbelleÄŸi silindi: {path}");
                }
            }
        }

        public void ClearPointerCache()
        {
            if (_disposed)
            {
                _logger.LogWarning("PointerValidationService dispose edilmiÅŸ durumda. Ä°ÅŸlem reddedildi.");
                return;
            }

            lock (_cacheLockObject)
            {
                int count = _validationCache.Count;
                _validationCache.Clear();
                _logger.LogInformation($"TÃ¼m pointer yolunun Ã¶nbelleÄŸi temizlendi ({count} adet).");
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
                        _validationCache.Clear();
                    }
                    _logger.LogInformation("PointerValidationService kapatÄ±ldÄ±");
                }
                _disposed = true;
            }
        }

        ~PointerValidationService()
        {
            Dispose(false);
        }
    }
}

