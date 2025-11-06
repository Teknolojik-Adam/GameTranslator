using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace P5S_ceviri
{//
    public class PerformanceOptimizedTranslationService : ITranslationService, IDisposable
    {
        private readonly ITranslationService _baseService;
        private readonly ILogger _logger;
        private readonly TranslationCacheManager _cacheManager;
        private readonly ConcurrentDictionary<string, Task<string>> _ongoingTranslations;
        private readonly int _maxConcurrentTranslations;
        private readonly int _batchSize;
        private readonly TimeSpan _batchCollectionWindow;
        private readonly bool _enableBatchProcessing;
        private readonly bool _enableRealtimeBatchProcessing;
        private readonly int _realtimeBatchThresholdMs;
        private readonly object _batchLock = new object();
        private List<BatchTranslationItem> _currentBatch = new List<BatchTranslationItem>();
        private Timer _batchTimer;
        private bool _disposed = false;
        private DateTime _lastBatchProcessTime = DateTime.MinValue;
        private SemaphoreSlim _concurrencySemaphore;

        
        private readonly ConcurrentDictionary<string, CacheEntry> _smartCache;
        private Timer _cacheCleanupTimer;
        private readonly int _maxCacheSize;
        private readonly int _cacheCleanupIntervalMinutes;
        private readonly bool _enableSmartCache;
        private readonly double _cacheCleanupThreshold;

        public PerformanceStats Statistics { get; private set; } = new PerformanceStats();
        public event EventHandler<PerformanceStats> StatsUpdated;

        private long _totalTranslations = 0;
        private long _batchTranslations = 0;
        private long _individualTranslations = 0;

        public ITranslationService BaseService => _baseService;

        public PerformanceOptimizedTranslationService(ITranslationService baseService, ILogger logger,
            int maxConcurrentTranslations = 10, int batchSize = 20, TimeSpan? batchCollectionWindow = null,
            bool enableBatchProcessing = true, bool enableRealtimeBatchProcessing = true, int realtimeBatchThresholdMs = 200,
            int maxCacheSize = 10000, int cacheCleanupIntervalMinutes = 30, bool enableSmartCache = true,
            double cacheCleanupThreshold = 0.8)
        {
            _baseService = baseService ?? throw new ArgumentNullException(nameof(baseService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _maxConcurrentTranslations = maxConcurrentTranslations;
            _batchSize = batchSize;
            _batchCollectionWindow = batchCollectionWindow ?? TimeSpan.FromMilliseconds(100);
            _enableBatchProcessing = enableBatchProcessing;
            _enableRealtimeBatchProcessing = enableRealtimeBatchProcessing;
            _realtimeBatchThresholdMs = realtimeBatchThresholdMs;
            _maxCacheSize = maxCacheSize;
            _cacheCleanupIntervalMinutes = cacheCleanupIntervalMinutes;
            _enableSmartCache = enableSmartCache;
            _cacheCleanupThreshold = cacheCleanupThreshold;

            _ongoingTranslations = new ConcurrentDictionary<string, Task<string>>();
            _concurrencySemaphore = new SemaphoreSlim(_maxConcurrentTranslations, _maxConcurrentTranslations);
            _smartCache = new ConcurrentDictionary<string, CacheEntry>();

            // TranslationCacheManager'ı başlat
            _cacheManager = new TranslationCacheManager(_logger);
            
            // Zamanı dolmuş önbellek girdilerini temizle
            _cacheManager.ExpireEntries();

            _logger.LogInformation($"PerformanceOptimizedTranslationService başlatıldı. Base Servis: {_baseService.GetType().Name}, Maksimum eşzamanlı çeviri: {_maxConcurrentTranslations}");

            if (_enableBatchProcessing)
            {
                InitializeBatchProcessing();
            }

            if (_enableSmartCache)
            {
                InitializeCacheCleanup();
            }
        }

        private void InitializeBatchProcessing()
        {
            var realTimeInterval = TimeSpan.FromMilliseconds(25); // Gerçek zamanlı işleme için 25ms
            _batchTimer = new Timer(ProcessBatch, null, realTimeInterval, realTimeInterval);
            _logger.LogInformation($"Toplu işleme başlatıldı. Batch boyutu: {_batchSize}, Toplama penceresi: {_batchCollectionWindow.TotalMilliseconds}ms");
        }

        private void InitializeCacheCleanup()
        {
            _cacheCleanupTimer = new Timer(CleanupCache, null,
                TimeSpan.FromMinutes(_cacheCleanupIntervalMinutes),
                TimeSpan.FromMinutes(_cacheCleanupIntervalMinutes));
            _logger.LogInformation($"Önbellek temizleme başlatıldı. Maksimum boyut: {_maxCacheSize}, Temizleme aralığı: {_cacheCleanupIntervalMinutes} dakika, Temizleme eşiği: {_cacheCleanupThreshold * 100}%");
        }

        public async Task<string> TranslateAsync(string text, string targetLanguage, Type strategyType = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            if (_disposed)
                throw new ObjectDisposedException(nameof(PerformanceOptimizedTranslationService), "Çeviri servisi zaten sonlandırılmış.");

            UpdateStats(stats => stats.TotalRequests++);
            UpdateStats(stats => stats.ConcurrentRequests++);

            var startTime = DateTime.UtcNow;

            try
            {
                string cacheKey = GenerateCacheKey(text, targetLanguage, strategyType);

                if (_ongoingTranslations.TryGetValue(cacheKey, out var ongoingTask))
                {
                    _logger.LogInformation($"Tekrarlanan istek kullanılıyor: {text.Substring(0, Math.Min(50, text.Length))}...");
                    return await ongoingTask;
                }

                if (_enableSmartCache && TryGetFromCache(cacheKey, out var cachedResult))
                {
                    UpdateStats(stats => stats.CacheHits++);
                    return cachedResult;
                }

                UpdateStats(stats => stats.CacheMisses++);

                if (_enableBatchProcessing && text.Length < 100)
                {
                    Interlocked.Increment(ref _totalTranslations);
                    Interlocked.Increment(ref _batchTranslations);

                    var completionSource = new TaskCompletionSource<string>();
                    var batchItem = new BatchTranslationItem(text, targetLanguage, strategyType, completionSource, cacheKey);

                    lock (_batchLock)
                    {
                        _currentBatch.Add(batchItem);

                        if (_currentBatch.Count >= _batchSize)
                        {
                            ProcessBatchImmediately();
                        }
                        else if (_currentBatch.Count == 1)
                        {
                            ProcessBatchImmediately();
                        }
                        else if (_enableRealtimeBatchProcessing && _currentBatch.Count > 1 &&
                                 DateTime.UtcNow - _lastBatchProcessTime > TimeSpan.FromMilliseconds(_realtimeBatchThresholdMs))
                        {
                            ProcessBatchImmediately();
                        }
                    }

                    return await completionSource.Task;
                }

                Interlocked.Increment(ref _totalTranslations);
                Interlocked.Increment(ref _individualTranslations);

                // Eşzamanlılık limiti ile çeviri işlemini başlat
                var translationTask = ExecuteWithConcurrencyLimit(() =>
                    _baseService.TranslateAsync(text, targetLanguage, strategyType));

                _ongoingTranslations[cacheKey] = translationTask;

                try
                {
                    // Görevin sonucunu al
                    var result = await translationTask;

                    if (_enableSmartCache && !string.IsNullOrEmpty(result))
                    {
                        AddToCache(cacheKey, result);
                    }

                    var duration = DateTime.UtcNow - startTime;
                    UpdateStats(stats =>
                    {
                        stats.AverageResponseTime = TimeSpan.FromTicks(
                            (stats.AverageResponseTime.Ticks * (stats.TotalRequests - 1) + duration.Ticks) / stats.TotalRequests
                        );
                    });

                    _logger.LogInformation($"Bireysel çeviri tamamlandı: {text.Substring(0, Math.Min(50, text.Length))}...");
                    return result;
                }
                finally
                {
                    _ongoingTranslations.TryRemove(cacheKey, out _);
                }
            }
            catch (Exception ex)
            {
                UpdateStats(stats => stats.FailedRequests++);
                _logger.LogError("Çeviri başarısız oldu", ex);
                throw;
            }
            finally
            {
                UpdateStats(stats => stats.ConcurrentRequests--);
            }
        }

        private void ProcessBatch(object state)
        {
            ProcessBatchImmediately();
        }

        private void ProcessBatchImmediately()
        {
            List<BatchTranslationItem> batchToProcess;

            lock (_batchLock)
            {
                if (_currentBatch.Count == 0)
                    return;

                batchToProcess = _currentBatch;
                _currentBatch = new List<BatchTranslationItem>();
                _lastBatchProcessTime = DateTime.UtcNow;
            }

            if (batchToProcess.Count == 0)
                return;

            Task.Run(async () =>
            {
                var startTime = DateTime.UtcNow;

                try
                {
                    var groupedBatch = batchToProcess
                        .GroupBy(item => new { item.TargetLanguage, item.StrategyType });

                    foreach (var group in groupedBatch)
                    {
                        var texts = group.Select(item => item.Text).ToArray();

                        if (_baseService is IBatchTranslationService batchService)
                        {
                            _logger.LogInformation($"Toplu çeviri servisi kullanılarak {texts.Length} öğe işleniyor");
                            var results = await batchService.TranslateBatchAsync(texts, group.Key.TargetLanguage, group.Key.StrategyType);

                            for (int i = 0; i < group.Count(); i++)
                            {
                                var item = group.ElementAt(i);
                                var result = results[i] ?? item.Text;

                                if (_enableSmartCache && !string.IsNullOrEmpty(result))
                                {
                                    AddToCache(item.CacheKey, result);
                                }

                                item.CompletionSource.SetResult(result);
                            }
                        }
                        else
                        {
                            _logger.LogInformation($"Bireysel çeviriler kullanılarak {group.Count()} öğe işleniyor");
                            var tasks = group.Select(item =>
                                _baseService.TranslateAsync(item.Text, item.TargetLanguage, item.StrategyType));

                            var results = await Task.WhenAll(tasks);

                            for (int i = 0; i < group.Count(); i++)
                            {
                                var item = group.ElementAt(i);
                                var result = results[i] ?? item.Text;

                                if (_enableSmartCache && !string.IsNullOrEmpty(result))
                                {
                                    AddToCache(item.CacheKey, result);
                                }

                                item.CompletionSource.SetResult(result);
                            }
                        }
                    }

                    UpdateStats(stats => stats.BatchProcessedRequests += batchToProcess.Count);

                    var processingTime = DateTime.UtcNow - startTime;
                    _logger.LogInformation($"Toplu çeviri tamamlandı. {batchToProcess.Count} öğe {processingTime.TotalMilliseconds}ms sürede işlendi.");
                }
                catch (Exception ex)
                {
                    _logger.LogError("Toplu çeviri sırasında hata oluştu", ex);

                    foreach (var item in batchToProcess)
                    {
                        item.CompletionSource.SetException(ex);
                    }

                    UpdateStats(stats => stats.FailedRequests += batchToProcess.Count);
                }
            });
        }

        #region Smart Cache Management

        private class CacheEntry
        {
            public string TranslatedText { get; set; }
            public DateTime LastAccessTime { get; set; }
            public int AccessCount { get; set; }
            public DateTime CreationTime { get; set; }
            public long Size { get; set; } // Tahmini bellek boyutu
        }

        private bool TryGetFromCache(string cacheKey, out string result)
        {
            result = null;

            // Önce TranslationCacheManager'dan kontrol et
            result = _cacheManager.GetTranslation(cacheKey);
            if (!string.IsNullOrEmpty(result))
            {
                _logger.LogInformation($"TranslationCacheManager'dan çeviri alındı");
                return true;
            }

            // Sonra smart cache'den kontrol et
            if (_smartCache.TryGetValue(cacheKey, out var cacheEntry))
            {
                cacheEntry.LastAccessTime = DateTime.UtcNow;
                cacheEntry.AccessCount++;

                result = cacheEntry.TranslatedText;
                return true;
            }

            return false;
        }

        private void AddToCache(string cacheKey, string translatedText)
        {
            if (string.IsNullOrEmpty(translatedText))
                return;

            // TranslationCacheManager'a kaydet (kalıcı önbellek)
            _cacheManager.AddTranslation(cacheKey, translatedText);
            _logger.LogInformation($"Çeviri TranslationCacheManager'a kaydedildi");

            // Smart cache'e de ekle (hızlı erişim için)
            if (_smartCache.Count < _maxCacheSize)
            {
                var cacheEntry = new CacheEntry
                {
                    TranslatedText = translatedText,
                    LastAccessTime = DateTime.UtcNow,
                    AccessCount = 1,
                    CreationTime = DateTime.UtcNow,
                    Size = CalculateStringSize(translatedText)
                };

                _smartCache[cacheKey] = cacheEntry;
            }
        }

        private void CleanupCache(object state)
        {
            try
            {
                // TranslationCacheManager'dan eski önbellekleri temizle
                _cacheManager?.ExpireEntries();

                if (_smartCache.Count <= _maxCacheSize * _cacheCleanupThreshold)
                    return;

                var entriesToRemove = _smartCache
                    .OrderBy(x => x.Value.LastAccessTime) // En az yakın zamanda kullanılan
                    .ThenBy(x => x.Value.AccessCount)     // En az sıklıkla kullanılan
                    .Take(_smartCache.Count - (int)(_maxCacheSize * 0.7)) // En eski ve en az erişilen öğelerin %70'ini kaldirmak için
                    .ToList();

                foreach (var entry in entriesToRemove)
                {
                    _smartCache.TryRemove(entry.Key, out _);
                }

                _logger.LogInformation($"Smart önbellek temizlendi. {entriesToRemove.Count} öğe kaldırıldı. Yeni boyut: {_smartCache.Count}");

                UpdateStats(stats => stats.CacheCleanupCount++);
            }
            catch (Exception ex)
            {
                _logger.LogError("Önbellek temizleme sırasında hata oluştu", ex);
            }
        }

        private long CalculateStringSize(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            // UTF-16 kodlaması, her karakter 2 byte
            return text.Length * 2;
        }

        public void ClearCache()
        {
            _smartCache.Clear();
            _logger.LogInformation("Önbellek tamamen temizlendi.");

            // İstatistikleri güncelle
            UpdateStats(stats =>
            {
                stats.CacheHits = 0;
                stats.CacheMisses = 0;
                stats.CacheCleanupCount++;
            });
        }

        public CacheInfo GetCacheInfo()
        {
            long totalSize = _smartCache.Values.Sum(entry => entry.Size);
            int totalItems = _smartCache.Count;
            double hitRate = Statistics.TotalRequests > 0 ?
                (double)Statistics.CacheHits / (Statistics.CacheHits + Statistics.CacheMisses) * 100 : 0;

            var cacheInfo = new CacheInfo
            {
                TotalItems = totalItems,
                TotalSizeBytes = totalSize,
                HitRate = hitRate,
                OldestItem = _smartCache.Values.OrderBy(v => v.CreationTime).FirstOrDefault()?.CreationTime ?? DateTime.MinValue,
                MostAccessedItem = _smartCache.Values.OrderByDescending(v => v.AccessCount).FirstOrDefault()?.AccessCount ?? 0
            };

            _logger.LogInformation($"Önbellek Bilgisi - Toplam Öğe: {totalItems}, Boyut: {totalSize} byte, İsabet Oranı: {hitRate:F2}%");
            return cacheInfo;
        }

        public class CacheInfo
        {
            public int TotalItems { get; set; }
            public long TotalSizeBytes { get; set; }
            public double HitRate { get; set; }
            public DateTime OldestItem { get; set; }
            public int MostAccessedItem { get; set; }
        }

        #endregion

        #region Performance Statistics Management

        public class PerformanceStats
        {
            public int TotalRequests { get; set; }
            public int BatchProcessedRequests { get; set; }
            public int IndividualProcessedRequests { get; set; }
            public int CacheHits { get; set; }
            public int CacheMisses { get; set; }
            public int FailedRequests { get; set; }
            public int CacheCleanupCount { get; set; }
            public TimeSpan AverageResponseTime { get; set; }
            public int ConcurrentRequests { get; set; }

            public double CacheHitRate => (CacheHits + CacheMisses) > 0 ?
                (double)CacheHits / (CacheHits + CacheMisses) * 100 : 0;
            public double SuccessRate => TotalRequests > 0 ?
                (double)(TotalRequests - FailedRequests) / TotalRequests * 100 : 0;
        }

        private void UpdateStats(Action<PerformanceStats> updateAction)
        {
            updateAction(Statistics);
            StatsUpdated?.Invoke(this, Statistics);
        }

        public (long Total, long Batch, long Individual) GetPerformanceStats()
        {
            var stats = (Interlocked.Read(ref _totalTranslations),
                         Interlocked.Read(ref _batchTranslations),
                         Interlocked.Read(ref _individualTranslations));
            
            _logger.LogInformation($"Performans İstatistikleri - Toplam: {stats.Item1}, Toplu: {stats.Item2}, Bireysel: {stats.Item3}");
            return stats;
        }

        #endregion

        #region Helper Methods

        private string GenerateCacheKey(string text, string targetLanguage, Type strategyType)
        {
            var key = $"{text}_{targetLanguage}_{strategyType?.Name}";

            if (key.Length > 100)
            {
                return $"{key.GetHashCode():X8}_{targetLanguage}_{strategyType?.Name}";
            }

            return key;
        }

        private async Task<T> ExecuteWithConcurrencyLimit<T>(Func<Task<T>> taskFactory)
        {
            await _concurrencySemaphore.WaitAsync();
            try
            {
                return await taskFactory();
            }
            finally
            {
                _concurrencySemaphore.Release();
            }
        }

        #endregion

        #region IDisposable Implementation

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
                    _logger.LogInformation("PerformanceOptimizedTranslationService kapatılıyor...");

                    // Zamanı dolmuş önbellek girdilerini temizle
                    _cacheManager?.ExpireEntries();

                    // Timer'ları durdur
                    _batchTimer?.Dispose();
                    _cacheCleanupTimer?.Dispose();

                    // Semaphore'u temizle
                    _concurrencySemaphore?.Dispose();

                    lock (_batchLock)
                    {
                        foreach (var item in _currentBatch)
                        {
                            item.CompletionSource.SetException(new ObjectDisposedException("Çeviri servisi sonlandırılmış"));
                        }
                        _currentBatch.Clear();
                    }

                    foreach (var ongoing in _ongoingTranslations.Values)
                    {
                        ongoing.ContinueWith(t => t.Exception?.Handle(ex => false));
                    }
                    _ongoingTranslations.Clear();

                    _logger.LogInformation($"PerformanceOptimizedTranslationService kapatıldı. Toplam çeviri: {_totalTranslations}");
                }
                _disposed = true;
            }
        }

        ~PerformanceOptimizedTranslationService()
        {
            Dispose(false);
        }

        #endregion

        private class BatchTranslationItem
        {
            public string Text { get; }
            public string TargetLanguage { get; }
            public Type StrategyType { get; }
            public TaskCompletionSource<string> CompletionSource { get; }
            public string CacheKey { get; }

            public BatchTranslationItem(string text, string targetLanguage, Type strategyType,
                TaskCompletionSource<string> completionSource, string cacheKey)
            {
                Text = text;
                TargetLanguage = targetLanguage;
                StrategyType = strategyType;
                CompletionSource = completionSource;
                CacheKey = cacheKey;
            }
        }
    }

    public interface IBatchTranslationService : ITranslationService
    {
        Task<string[]> TranslateBatchAsync(string[] texts, string targetLanguage, Type strategyType = null);
    }
}