using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Text;

namespace P5S_ceviri
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


    public class StabilitySample
    {
        public DateTime Timestamp { get; set; }
        public IntPtr Address { get; set; }
        public string Value { get; set; }
        public bool IsSuccessful { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class PointerValidationService
    {
        private readonly IMemoryService _memoryService;
        private readonly ILogger _logger;

        public PointerValidationService(IMemoryService memoryService, ILogger logger)
        {
            _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<List<PointerValidationResult>> ValidatePointersAsync(Process process, List<PointerPath> paths, string expectedText = null)
        {
            var results = new List<PointerValidationResult>();

            if (!_memoryService.AttachToProcess(process.Id))
            {
                _logger.LogError("Process'e bağlanılamadı - pointer validation başarısız");
                return results;
            }

            foreach (var path in paths)
            {
                var result = await ValidateSinglePointerAsync(process, path, expectedText);
                results.Add(result);
            }

            return results.OrderByDescending(r => r.Score).ToList();
        }

        public async Task<PointerValidationResult> ValidateSinglePointerAsync(Process process, PointerPath path, string expectedText)
        {
            var result = new PointerValidationResult
            {
                Path = path,
                IsValid = false,
                Score = 0
            };

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var pathInfo = new PathInfo
                {
                    BaseAddressModule = path.ModuleName,
                    BaseAddressOffset = path.BaseOffset,
                    PointerOffsets = path.Offsets
                };

                var resolvedAddress = _memoryService.ResolveAddressFromPath(process, pathInfo);
                if (resolvedAddress == IntPtr.Zero)
                {
                    result.ErrorMessage = "Adres çözümlenemedi";
                    return result;
                }

                result.ResolvedAddress = resolvedAddress;
                result.CurrentValue = _memoryService.TryReadStringDeep(resolvedAddress);

                stopwatch.Stop();
                result.ResponseTime = stopwatch.Elapsed;

                if (string.IsNullOrWhiteSpace(result.CurrentValue))
                {
                    result.ErrorMessage = "Boş veya geçersiz veri okundu";
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
                        result.ErrorMessage = "Beklenen metin bulunamadı";
                        result.Score = 10;
                    }
                }
                else
                {
                    result.IsValid = true;
                    result.Score = 50;
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.ResponseTime = stopwatch.Elapsed;
                result.ErrorMessage = $"Doğrulama hatası: {ex.Message}";
                _logger.LogError($"Pointer doğrulama hatası: {ex.Message}", ex);
            }

            return result;
        }

        public async Task<PointerStabilityResult> TestPointerStabilityAsync(Process process, PointerPath path, int testDurationSeconds = 15, int sampleIntervalMs = 500)
        {
            _logger.LogInformation($"Pointer kararlılık testi başlatıldı. Süre: {testDurationSeconds}s, Aralık: {sampleIntervalMs}ms");

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

                    var address = _memoryService.ResolveAddressFromPath(process, pathInfo);
                    sample.Address = address;

                    if (address != IntPtr.Zero)
                    {
                        sample.Value = _memoryService.TryReadStringDeep(address);
                        sample.IsSuccessful = !string.IsNullOrEmpty(sample.Value);
                        if (!sample.IsSuccessful) sample.ErrorMessage = "Boş veri okundu";
                    }
                    else
                    {
                        sample.ErrorMessage = "Adres çözümlenemedi";
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

            // Sonuçları analiz et
            int successfulSamples = samples.Count(s => s.IsSuccessful);
            double successRate = samples.Count > 0 ? (double)successfulSamples / samples.Count * 100 : 0;

            var validSamples = samples.Where(s => s.IsSuccessful).ToList();
            int uniqueAddresses = validSamples.Select(s => s.Address).Distinct().Count();
            double addressConsistency = validSamples.Any() ? (uniqueAddresses == 1 ? 100.0 : 0.0) : 0.0;

            int uniqueValues = validSamples.Select(s => s.Value).Distinct().Count();
            double valueConsistency = validSamples.Any() ? 100.0 * (1.0 - ((double)(uniqueValues - 1) / validSamples.Count)) : 0.0;

            double stabilityScore = (successRate * 0.5) + (addressConsistency * 0.3) + (valueConsistency * 0.2);
            bool isStable = stabilityScore >= 80;

            var result = new PointerStabilityResult
            {
                Path = path,
                IsStable = isStable,
                Message = $"Kararlılık: {successRate:F1}% ({successfulSamples}/{samples.Count} başarılı)",
                LastKnownAddress = samples.LastOrDefault()?.Address ?? IntPtr.Zero,
                SuccessRate = successRate,
                AddressConsistency = addressConsistency,
                ValueConsistency = valueConsistency,
                StabilityScore = stabilityScore
            };

            return result;
        }
    }
}
