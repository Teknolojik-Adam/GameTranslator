using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
        private sealed class ValidationCacheEntry
        {
            public string Key { get; set; }
            public string PathKey { get; set; }
            public PointerPath Path { get; set; }
            public PointerValidationResult Result { get; set; }
            public DateTime CreatedAtUtc { get; set; }
        }

        private static readonly TimeSpan ValidationCacheLifetime =
            TimeSpan.FromSeconds(5);

        private readonly IMemoryService _memoryService;
        private readonly ILogger _logger;
        private readonly AppSettings _appSettings;

        private readonly Dictionary<string, ValidationCacheEntry> _validationCache =
            new Dictionary<string, ValidationCacheEntry>(
                StringComparer.Ordinal);

        private readonly object _cacheLock =
            new object();

        private readonly SemaphoreSlim _memoryGate =
            new SemaphoreSlim(1, 1);

        private int _disposed;

        public PointerValidationService(
            IMemoryService memoryService,
            ILogger logger,
            AppSettings appSettings)
        {
            _memoryService =
                memoryService ?? throw new ArgumentNullException(nameof(memoryService));

            _logger =
                logger ?? throw new ArgumentNullException(nameof(logger));

            _appSettings =
                appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        }

        public async Task<List<PointerValidationResult>> ValidatePointersAsync(
            Process process,
            List<PointerPath> paths,
            string expectedText = null)
        {
            ThrowIfDisposed();

            var results =
                new List<PointerValidationResult>();

            if (process == null)
                return results;

            if (paths == null || paths.Count == 0)
                return results;

            int processId;

            try
            {
                if (process.HasExited)
                    return results;

                processId = process.Id;
            }
            catch
            {
                return results;
            }

            bool attached =
                await AttachToProcessAsync(processId)
                    .ConfigureAwait(false);

            if (!attached)
            {
                _logger.LogError(
                    "Process'e bağlanılamadı. Pointer doğrulama başlatılamadı.");

                return results;
            }

            foreach (PointerPath path in paths)
            {
                if (path == null)
                    continue;

                PointerValidationResult result =
                    await ValidateSinglePointerCoreAsync(
                            process,
                            processId,
                            path,
                            expectedText)
                        .ConfigureAwait(false);

                if (result != null)
                {
                    results.Add(result);
                }
            }

            return results
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.ResponseTime)
                .ToList();
        }

        public async Task<PointerValidationResult> ValidateSinglePointerAsync(
            Process process,
            PointerPath path,
            string expectedText)
        {
            ThrowIfDisposed();

            if (path == null)
            {
                return new PointerValidationResult
                {
                    ErrorMessage = "Pointer yolu null."
                };
            }

            if (process == null)
            {
                return CreateFailure(
                    path,
                    "Process bulunamadı.");
            }

            int processId;

            try
            {
                if (process.HasExited)
                {
                    return CreateFailure(
                        path,
                        "Process çalışmıyor.");
                }

                processId = process.Id;
            }
            catch (Exception ex)
            {
                return CreateFailure(
                    path,
                    ex.Message);
            }

            bool attached =
                await AttachToProcessAsync(processId)
                    .ConfigureAwait(false);

            if (!attached)
            {
                return CreateFailure(
                    path,
                    "Process'e bağlanılamadı.");
            }

            return await ValidateSinglePointerCoreAsync(
                    process,
                    processId,
                    path,
                    expectedText)
                .ConfigureAwait(false);
        }

        private async Task<PointerValidationResult> ValidateSinglePointerCoreAsync(
            Process process,
            int processId,
            PointerPath path,
            string expectedText)
        {
            string cacheKey =
                BuildCacheKey(
                    processId,
                    path,
                    expectedText);

            PointerValidationResult cached =
                GetCachedResult(cacheKey);

            if (cached != null)
                return cached;

            var stopwatch =
                Stopwatch.StartNew();

            var result =
                new PointerValidationResult
                {
                    Path = path,
                    ExpectedValue = expectedText,
                    IsValid = false,
                    Score = 0
                };

            try
            {
                await _memoryGate
                    .WaitAsync()
                    .ConfigureAwait(false);

                try
                {
                    var pathInfo =
                        new PathInfo
                        {
                            BaseAddressModule =
                                path.ModuleName,

                            BaseAddressOffset =
                                path.BaseOffset,

                            PointerOffsets =
                                path.Offsets
                        };

                    IntPtr address =
                        _memoryService.ResolveAddressFromPathCached(
                            process,
                            pathInfo);

                    if (address == IntPtr.Zero)
                    {
                        result.ErrorMessage =
                            "Adres çözümlenemedi.";

                        return result;
                    }

                    result.ResolvedAddress =
                        address;

                    result.CurrentValue =
                        _memoryService.TryReadStringDeep(
                            address);
                }
                finally
                {
                    _memoryGate.Release();
                }

                if (string.IsNullOrWhiteSpace(
                    result.CurrentValue))
                {
                    result.ErrorMessage =
                        "Boş veya geçersiz veri okundu.";

                    result.Score = 0;

                    return result;
                }

                if (string.IsNullOrWhiteSpace(expectedText))
                {
                    result.IsValid = true;
                    result.Score = 60;
                }
                else if (result.CurrentValue.IndexOf(
                             expectedText,
                             StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.IsValid = true;
                    result.Score = 100;
                }
                else
                {
                    result.IsValid = false;
                    result.Score = 10;
                    result.ErrorMessage =
                        "Beklenen metin bulunamadı.";
                }

                SetCachedResult(
                    cacheKey,
                    path,
                    result);

                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage =
                    $"Doğrulama hatası: {ex.Message}";

                _logger.LogError(
                    $"Pointer doğrulama hatası: {ex.Message}",
                    ex);

                return result;
            }
            finally
            {
                stopwatch.Stop();

                result.ResponseTime =
                    stopwatch.Elapsed;
            }
        }

        public async Task<PointerStabilityResult> TestPointerStabilityAsync(
            Process process,
            PointerPath path,
            int testDurationSeconds = 15,
            int sampleIntervalMs = 500)
        {
            ThrowIfDisposed();

            if (process == null)
            {
                return new PointerStabilityResult
                {
                    Path = path,
                    Message = "Process bulunamadı."
                };
            }

            if (path == null)
            {
                return new PointerStabilityResult
                {
                    Message = "Pointer yolu bulunamadı."
                };
            }

            int processId;

            try
            {
                if (process.HasExited)
                {
                    return new PointerStabilityResult
                    {
                        Path = path,
                        Message = "Process çalışmıyor."
                    };
                }

                processId = process.Id;
            }
            catch (Exception ex)
            {
                return new PointerStabilityResult
                {
                    Path = path,
                    Message = ex.Message
                };
            }

            bool attached =
                await AttachToProcessAsync(processId)
                    .ConfigureAwait(false);

            if (!attached)
            {
                return new PointerStabilityResult
                {
                    Path = path,
                    Message = "Process'e bağlanılamadı."
                };
            }

            if (testDurationSeconds < 1)
                testDurationSeconds = 1;

            if (testDurationSeconds > 300)
                testDurationSeconds = 300;

            if (sampleIntervalMs < 50)
                sampleIntervalMs = 50;

            if (sampleIntervalMs > 10000)
                sampleIntervalMs = 10000;

            var samples =
                new List<StabilitySample>();

            var stopwatch =
                Stopwatch.StartNew();

            TimeSpan testDuration =
                TimeSpan.FromSeconds(
                    testDurationSeconds);

            while (stopwatch.Elapsed < testDuration)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    break;

                var sample =
                    new StabilitySample
                    {
                        Timestamp = DateTime.UtcNow
                    };

                try
                {
                    await _memoryGate
                        .WaitAsync()
                        .ConfigureAwait(false);

                    try
                    {
                        var pathInfo =
                            new PathInfo
                            {
                                BaseAddressModule =
                                    path.ModuleName,

                                BaseAddressOffset =
                                    path.BaseOffset,

                                PointerOffsets =
                                    path.Offsets
                            };

                        IntPtr address =
                            _memoryService.ResolveAddressFromPathCached(
                                process,
                                pathInfo);

                        sample.Address =
                            address;

                        if (address != IntPtr.Zero)
                        {
                            sample.Value =
                                _memoryService.TryReadStringDeep(
                                    address);

                            sample.IsSuccessful =
                                !string.IsNullOrWhiteSpace(
                                    sample.Value);

                            if (!sample.IsSuccessful)
                            {
                                sample.ErrorMessage =
                                    "Boş veri okundu.";
                            }
                        }
                        else
                        {
                            sample.IsSuccessful = false;
                            sample.ErrorMessage =
                                "Adres çözümlenemedi.";
                        }
                    }
                    finally
                    {
                        _memoryGate.Release();
                    }
                }
                catch (Exception ex)
                {
                    sample.IsSuccessful = false;
                    sample.ErrorMessage = ex.Message;
                }

                samples.Add(sample);

                TimeSpan remaining =
                    testDuration - stopwatch.Elapsed;

                if (remaining <= TimeSpan.Zero)
                    break;

                int delay =
                    (int)Math.Min(
                        sampleIntervalMs,
                        remaining.TotalMilliseconds);

                if (delay > 0)
                {
                    await Task.Delay(delay)
                        .ConfigureAwait(false);
                }
            }

            stopwatch.Stop();

            int successfulSamples =
                samples.Count(x => x.IsSuccessful);

            double successRate =
                samples.Count == 0
                    ? 0
                    : (double)successfulSamples /
                      samples.Count *
                      100.0;

            List<StabilitySample> validSamples =
                samples
                    .Where(x => x.IsSuccessful)
                    .ToList();

            int uniqueAddresses =
                validSamples
                    .Select(x => x.Address)
                    .Distinct()
                    .Count();

            int uniqueValues =
                validSamples
                    .Select(x => x.Value ?? string.Empty)
                    .Distinct(StringComparer.Ordinal)
                    .Count();

            double addressConsistency =
                CalculateConsistency(
                    validSamples.Count,
                    uniqueAddresses);

            double valueConsistency =
                CalculateConsistency(
                    validSamples.Count,
                    uniqueValues);

            double stabilityScore =
                successRate * 0.50 +
                addressConsistency * 0.35 +
                valueConsistency * 0.15;

            stabilityScore =
                Math.Max(
                    0,
                    Math.Min(
                        100,
                        stabilityScore));

            bool isStable =
                successRate >= 80 &&
                stabilityScore >= 80;

            StabilitySample lastSuccessful =
                validSamples.LastOrDefault();

            var result =
                new PointerStabilityResult
                {
                    Path = path,
                    IsStable = isStable,
                    Message =
                        $"Kararlılık: {stabilityScore:F1}% | Başarı: {successRate:F1}% ({successfulSamples}/{samples.Count})",
                    LastKnownAddress =
                        lastSuccessful != null
                            ? lastSuccessful.Address
                            : IntPtr.Zero,
                    SuccessRate = successRate,
                    AddressConsistency = addressConsistency,
                    ValueConsistency = valueConsistency,
                    StabilityScore = stabilityScore
                };

            _logger.LogInformation(
                $"Pointer kararlılık testi tamamlandı. {path}: {result.Message}");

            return result;
        }

        public List<PointerPath> GetRegisteredPointerPaths()
        {
            ThrowIfDisposed();

            lock (_cacheLock)
            {
                RemoveExpiredCacheEntries();

                return _validationCache
                    .Values
                    .GroupBy(
                        x => x.PathKey,
                        StringComparer.Ordinal)
                    .Select(x => x.First().Path)
                    .Where(x => x != null)
                    .ToList();
            }
        }

        public void InvalidatePointerCache(
            PointerPath path)
        {
            ThrowIfDisposed();

            if (path == null)
                return;

            string pathKey =
                BuildPathKey(path);

            int removed = 0;

            lock (_cacheLock)
            {
                string[] keys =
                    _validationCache
                        .Where(x =>
                            string.Equals(
                                x.Value.PathKey,
                                pathKey,
                                StringComparison.Ordinal))
                        .Select(x => x.Key)
                        .ToArray();

                foreach (string key in keys)
                {
                    if (_validationCache.Remove(key))
                        removed++;
                }
            }

            if (removed > 0)
            {
                _logger.LogInformation(
                    $"Pointer cache temizlendi: {removed} kayıt.");
            }
        }

        public void ClearPointerCache()
        {
            ThrowIfDisposed();

            int count;

            lock (_cacheLock)
            {
                count =
                    _validationCache.Count;

                _validationCache.Clear();
            }

            if (count > 0)
            {
                _logger.LogInformation(
                    $"Pointer cache temizlendi: {count} kayıt.");
            }
        }

        private async Task<bool> AttachToProcessAsync(
            int processId)
        {
            await _memoryGate
                .WaitAsync()
                .ConfigureAwait(false);

            try
            {
                return _memoryService.AttachToProcess(
                    processId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    $"Process'e bağlanılırken hata oluştu. PID: {processId}",
                    ex);

                return false;
            }
            finally
            {
                _memoryGate.Release();
            }
        }

        private PointerValidationResult GetCachedResult(
            string key)
        {
            lock (_cacheLock)
            {
                ValidationCacheEntry entry;

                if (!_validationCache.TryGetValue(
                    key,
                    out entry))
                {
                    return null;
                }

                if (DateTime.UtcNow -
                    entry.CreatedAtUtc >
                    ValidationCacheLifetime)
                {
                    _validationCache.Remove(key);
                    return null;
                }

                return CloneResult(
                    entry.Result);
            }
        }

        private void SetCachedResult(
            string key,
            PointerPath path,
            PointerValidationResult result)
        {
            if (result == null)
                return;

            lock (_cacheLock)
            {
                RemoveExpiredCacheEntries();

                _validationCache[key] =
                    new ValidationCacheEntry
                    {
                        Key = key,
                        PathKey = BuildPathKey(path),
                        Path = path,
                        Result = CloneResult(result),
                        CreatedAtUtc = DateTime.UtcNow
                    };
            }
        }

        private void RemoveExpiredCacheEntries()
        {
            DateTime now =
                DateTime.UtcNow;

            string[] expiredKeys =
                _validationCache
                    .Where(x =>
                        now -
                        x.Value.CreatedAtUtc >
                        ValidationCacheLifetime)
                    .Select(x => x.Key)
                    .ToArray();

            foreach (string key in expiredKeys)
            {
                _validationCache.Remove(key);
            }
        }

        private static double CalculateConsistency(
            int sampleCount,
            int uniqueCount)
        {
            if (sampleCount <= 0)
                return 0;

            if (sampleCount == 1)
                return 100;

            if (uniqueCount <= 1)
                return 100;

            double changeRatio =
                (double)(uniqueCount - 1) /
                (sampleCount - 1);

            double consistency =
                (1.0 - changeRatio) *
                100.0;

            return Math.Max(
                0,
                Math.Min(
                    100,
                    consistency));
        }

        private static string BuildCacheKey(
            int processId,
            PointerPath path,
            string expectedText)
        {
            return processId +
                   "|" +
                   BuildPathKey(path) +
                   "|" +
                   (expectedText ?? string.Empty);
        }

        private static string BuildPathKey(
            PointerPath path)
        {
            if (path == null)
                return string.Empty;

            string offsets =
                path.Offsets == null
                    ? string.Empty
                    : string.Join(
                        ",",
                        path.Offsets.Select(
                            x => x.ToString()));

            return
                (path.ModuleName ?? string.Empty) +
                "|" +
                path.BaseOffset +
                "|" +
                offsets;
        }

        private static PointerValidationResult CloneResult(
            PointerValidationResult source)
        {
            if (source == null)
                return null;

            return new PointerValidationResult
            {
                Path = source.Path,
                IsValid = source.IsValid,
                CurrentValue = source.CurrentValue,
                ExpectedValue = source.ExpectedValue,
                ErrorMessage = source.ErrorMessage,
                Score = source.Score,
                ResponseTime = source.ResponseTime,
                ResolvedAddress = source.ResolvedAddress
            };
        }

        private static PointerValidationResult CreateFailure(
            PointerPath path,
            string message)
        {
            return new PointerValidationResult
            {
                Path = path,
                IsValid = false,
                Score = 0,
                ErrorMessage = message
            };
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(PointerValidationService));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) != 0)
            {
                return;
            }

            lock (_cacheLock)
            {
                _validationCache.Clear();
            }
        }
    }
}