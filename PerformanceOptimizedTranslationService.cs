using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public sealed class PerformanceOptimizedTranslationService :
        ITranslationService,
        IDisposable
    {
        private readonly ITranslationService _baseService;
        private readonly ILogger _logger;
        private readonly TranslationCacheManager _cacheManager;

        private readonly ConcurrentDictionary<string, Task<string>>
            _ongoingTranslations;

        private readonly int _maxConcurrentTranslations;
        private readonly int _batchSize;
        private readonly TimeSpan _batchCollectionWindow;
        private readonly bool _enableBatchProcessing;
        private readonly bool _enableRealtimeBatchProcessing;
        private readonly int _realtimeBatchThresholdMs;

        private readonly object _batchLock;

        private List<BatchTranslationItem> _currentBatch;
        private Timer _batchTimer;

        private DateTime _lastBatchProcessTime;

        private readonly SemaphoreSlim _concurrencySemaphore;

        private readonly ConcurrentDictionary<string, CacheEntry>
            _smartCache;

        private Timer _cacheCleanupTimer;

        private readonly int _maxCacheSize;
        private readonly int _cacheCleanupIntervalMinutes;
        private readonly bool _enableSmartCache;
        private readonly double _cacheCleanupThreshold;

        private readonly object _statisticsLock;

        private int _disposed;

        private long _totalTranslations;
        private long _batchTranslations;
        private long _individualTranslations;

        public PerformanceStats Statistics
        {
            get;
            private set;
        }

        public event EventHandler<PerformanceStats> StatsUpdated;

        public ITranslationService BaseService
        {
            get
            {
                return _baseService;
            }
        }

        public PerformanceOptimizedTranslationService(
            ITranslationService baseService,
            ILogger logger,
            int maxConcurrentTranslations = 10,
            int batchSize = 20,
            TimeSpan? batchCollectionWindow = null,
            bool enableBatchProcessing = true,
            bool enableRealtimeBatchProcessing = true,
            int realtimeBatchThresholdMs = 200,
            int maxCacheSize = 10000,
            int cacheCleanupIntervalMinutes = 30,
            bool enableSmartCache = true,
            double cacheCleanupThreshold = 0.8)
        {
            if (baseService == null)
            {
                throw new ArgumentNullException(
                    nameof(baseService));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(
                    nameof(logger));
            }

            if (maxConcurrentTranslations < 1)
            {
                maxConcurrentTranslations =
                    1;
            }

            if (batchSize < 1)
            {
                batchSize =
                    1;
            }

            if (realtimeBatchThresholdMs < 1)
            {
                realtimeBatchThresholdMs =
                    1;
            }

            if (maxCacheSize < 1)
            {
                maxCacheSize =
                    1;
            }

            if (cacheCleanupIntervalMinutes < 1)
            {
                cacheCleanupIntervalMinutes =
                    1;
            }

            if (double.IsNaN(
                    cacheCleanupThreshold) ||
                double.IsInfinity(
                    cacheCleanupThreshold))
            {
                cacheCleanupThreshold =
                    0.8;
            }

            cacheCleanupThreshold =
                Math.Max(
                    0.1,
                    Math.Min(
                        1.0,
                        cacheCleanupThreshold));

            _baseService =
                baseService;

            _logger =
                logger;

            _maxConcurrentTranslations =
                maxConcurrentTranslations;

            _batchSize =
                batchSize;

            _batchCollectionWindow =
                batchCollectionWindow ??
                TimeSpan.FromMilliseconds(100);

            _enableBatchProcessing =
                enableBatchProcessing;

            _enableRealtimeBatchProcessing =
                enableRealtimeBatchProcessing;

            _realtimeBatchThresholdMs =
                realtimeBatchThresholdMs;

            _maxCacheSize =
                maxCacheSize;

            _cacheCleanupIntervalMinutes =
                cacheCleanupIntervalMinutes;

            _enableSmartCache =
                enableSmartCache;

            _cacheCleanupThreshold =
                cacheCleanupThreshold;

            _batchLock =
                new object();

            _statisticsLock =
                new object();

            _currentBatch =
                new List<BatchTranslationItem>();

            _lastBatchProcessTime =
                DateTime.MinValue;

            _ongoingTranslations =
                new ConcurrentDictionary<string, Task<string>>(
                    StringComparer.Ordinal);

            _concurrencySemaphore =
                new SemaphoreSlim(
                    _maxConcurrentTranslations,
                    _maxConcurrentTranslations);

            _smartCache =
                new ConcurrentDictionary<string, CacheEntry>(
                    StringComparer.Ordinal);

            _cacheManager =
                new TranslationCacheManager(
                    _logger);

            Statistics =
                new PerformanceStats();

            try
            {
                _cacheManager.ExpireEntries();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Çeviri önbelleği başlatılırken temizlenemedi: {ex.Message}");
            }

            if (_enableBatchProcessing)
            {
                InitializeBatchProcessing();
            }

            if (_enableSmartCache)
            {
                InitializeCacheCleanup();
            }

            _logger.LogInformation(
                $"PerformanceOptimizedTranslationService başlatıldı. " +
                $"Base servis: {_baseService.GetType().Name}, " +
                $"maksimum eşzamanlı çeviri: {_maxConcurrentTranslations}");
        }

        private void InitializeBatchProcessing()
        {
            TimeSpan interval =
                TimeSpan.FromMilliseconds(
                    Math.Max(
                        25,
                        Math.Min(
                            _batchCollectionWindow.TotalMilliseconds,
                            250)));

            _batchTimer =
                new Timer(
                    ProcessBatch,
                    null,
                    interval,
                    interval);

            _logger.LogInformation(
                $"Toplu işleme başlatıldı. Batch boyutu: {_batchSize}, " +
                $"toplama penceresi: {_batchCollectionWindow.TotalMilliseconds:F0} ms");
        }

        private void InitializeCacheCleanup()
        {
            TimeSpan interval =
                TimeSpan.FromMinutes(
                    _cacheCleanupIntervalMinutes);

            _cacheCleanupTimer =
                new Timer(
                    CleanupCache,
                    null,
                    interval,
                    interval);

            _logger.LogInformation(
                $"Önbellek temizleme başlatıldı. " +
                $"Maksimum boyut: {_maxCacheSize}, " +
                $"temizleme aralığı: {_cacheCleanupIntervalMinutes} dakika");
        }

        public Task<string> TranslateAsync(
            string text,
            string targetLanguage,
            string strategyId)
        {
            ThrowIfDisposed();

            Type strategyType =
                ResolveStrategyType(
                    strategyId);

            return TranslateAsync(
                text,
                targetLanguage,
                strategyType);
        }

        public async Task<string> TranslateAsync(
            string text,
            string targetLanguage,
            Type strategyType = null)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(
                text))
            {
                return string.Empty;
            }

            string normalizedTarget =
                NormalizeTargetLanguage(
                    targetLanguage);

            UpdateStats(
                delegate (PerformanceStats stats)
                {
                    stats.TotalRequests++;
                    stats.ConcurrentRequests++;
                });

            DateTime startTime =
                DateTime.UtcNow;

            try
            {
                string cacheKey =
                    GenerateCacheKey(
                        text,
                        normalizedTarget,
                        strategyType);

                Task<string> existingTask;

                if (_ongoingTranslations.TryGetValue(
                    cacheKey,
                    out existingTask))
                {
                    _logger.LogInformation(
                        $"Devam eden aynı çeviri isteği kullanılıyor: {CreatePreview(text)}");

                    return await existingTask;
                }

                string cachedResult;

                if (_enableSmartCache &&
                    TryGetFromCache(
                        cacheKey,
                        out cachedResult))
                {
                    UpdateStats(
                        delegate (PerformanceStats stats)
                        {
                            stats.CacheHits++;
                        });

                    return cachedResult;
                }

                UpdateStats(
                    delegate (PerformanceStats stats)
                    {
                        stats.CacheMisses++;
                    });

                if (_enableBatchProcessing &&
                    text.Length < 100)
                {
                    Interlocked.Increment(
                        ref _totalTranslations);

                    Interlocked.Increment(
                        ref _batchTranslations);

                    var completionSource =
                        new TaskCompletionSource<string>();

                    var batchItem =
                        new BatchTranslationItem(
                            text,
                            normalizedTarget,
                            strategyType,
                            completionSource,
                            cacheKey);

                    bool processNow =
                        false;

                    lock (_batchLock)
                    {
                        ThrowIfDisposed();

                        _currentBatch.Add(
                            batchItem);

                        if (_currentBatch.Count >=
                            _batchSize)
                        {
                            processNow =
                                true;
                        }
                        else if (_enableRealtimeBatchProcessing &&
                                 _currentBatch.Count > 1 &&
                                 DateTime.UtcNow -
                                 _lastBatchProcessTime >=
                                 TimeSpan.FromMilliseconds(
                                     _realtimeBatchThresholdMs))
                        {
                            processNow =
                                true;
                        }
                    }

                    if (processNow)
                    {
                        ProcessBatchImmediately();
                    }

                    return await completionSource.Task;
                }

                Interlocked.Increment(
                    ref _totalTranslations);

                Interlocked.Increment(
                    ref _individualTranslations);

                Task<string> translationTask =
                    ExecuteWithConcurrencyLimit(
                        delegate
                        {
                            return _baseService.TranslateAsync(
                                text,
                                normalizedTarget,
                                strategyType);
                        });

                Task<string> actualTask =
                    _ongoingTranslations.GetOrAdd(
                        cacheKey,
                        translationTask);

                try
                {
                    string result =
                        await actualTask;

                    if (_enableSmartCache &&
                        !string.IsNullOrWhiteSpace(
                            result))
                    {
                        AddToCache(
                            cacheKey,
                            result);
                    }

                    TimeSpan duration =
                        DateTime.UtcNow -
                        startTime;

                    UpdateAverageResponseTime(
                        duration);

                    UpdateStats(
                        delegate (PerformanceStats stats)
                        {
                            stats.IndividualProcessedRequests++;
                        });

                    _logger.LogInformation(
                        $"Bireysel çeviri tamamlandı: {CreatePreview(text)}");

                    return result;
                }
                finally
                {
                    Task<string> ignored;

                    _ongoingTranslations.TryRemove(
                        cacheKey,
                        out ignored);
                }
            }
            catch (Exception ex)
            {
                UpdateStats(
                    delegate (PerformanceStats stats)
                    {
                        stats.FailedRequests++;
                    });

                _logger.LogError(
                    "Çeviri başarısız oldu.",
                    ex);

                throw;
            }
            finally
            {
                UpdateStats(
                    delegate (PerformanceStats stats)
                    {
                        if (stats.ConcurrentRequests > 0)
                        {
                            stats.ConcurrentRequests--;
                        }
                    });
            }
        }

        public Task<string> TranslateRealtimeAsync(string text, string targetLanguage, Type strategyType = null)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(string.Empty);
            string normalizedTarget = NormalizeTargetLanguage(targetLanguage);
            UpdateStats(delegate (PerformanceStats stats) { stats.TotalRequests++; stats.ConcurrentRequests++; });
            DateTime startTime = DateTime.UtcNow;
            return TranslateRealtimeInternalAsync(text, normalizedTarget, strategyType, startTime);
        }

        private async Task<string> TranslateRealtimeInternalAsync(string text, string normalizedTarget, Type strategyType, DateTime startTime)
        {
            try
            {
                string cacheKey = GenerateCacheKey(text, normalizedTarget, strategyType);
                Task<string> existingTask;
                if (_ongoingTranslations.TryGetValue(cacheKey, out existingTask)) return await existingTask.ConfigureAwait(false);
                string cachedResult;
                if (_enableSmartCache && TryGetFromCache(cacheKey, out cachedResult))
                {
                    UpdateStats(delegate (PerformanceStats stats) { stats.CacheHits++; });
                    return cachedResult;
                }
                UpdateStats(delegate (PerformanceStats stats) { stats.CacheMisses++; });
                Interlocked.Increment(ref _totalTranslations);
                Interlocked.Increment(ref _individualTranslations);
                Task<string> translationTask = ExecuteWithConcurrencyLimit(delegate { return _baseService.TranslateAsync(text, normalizedTarget, strategyType); });
                Task<string> actualTask = _ongoingTranslations.GetOrAdd(cacheKey, translationTask);
                try
                {
                    string result = await actualTask.ConfigureAwait(false);
                    if (_enableSmartCache && !string.IsNullOrWhiteSpace(result)) AddToCache(cacheKey, result);
                    UpdateAverageResponseTime(DateTime.UtcNow - startTime);
                    UpdateStats(delegate (PerformanceStats stats) { stats.IndividualProcessedRequests++; });
                    return result;
                }
                finally
                {
                    Task<string> ignored; _ongoingTranslations.TryRemove(cacheKey, out ignored);
                }
            }
            catch (Exception ex)
            {
                UpdateStats(delegate (PerformanceStats stats) { stats.FailedRequests++; });
                _logger.LogError("Çeviri başarısız oldu.", ex);
                throw;
            }
            finally
            {
                UpdateStats(delegate (PerformanceStats stats) { if (stats.ConcurrentRequests > 0) stats.ConcurrentRequests--; });
            }
        }

        private void ProcessBatch(
            object state)
        {
            if (Volatile.Read(
                    ref _disposed) != 0)
            {
                return;
            }

            bool shouldProcess =
                false;

            lock (_batchLock)
            {
                if (_currentBatch.Count == 0)
                {
                    return;
                }

                DateTime oldest =
                    _currentBatch[0].CreatedAt;

                if (DateTime.UtcNow -
                    oldest >=
                    _batchCollectionWindow)
                {
                    shouldProcess =
                        true;
                }
            }

            if (shouldProcess)
            {
                ProcessBatchImmediately();
            }
        }

        private void ProcessBatchImmediately()
        {
            if (Volatile.Read(
                    ref _disposed) != 0)
            {
                return;
            }

            List<BatchTranslationItem> batchToProcess;

            lock (_batchLock)
            {
                if (_currentBatch.Count == 0)
                {
                    return;
                }

                batchToProcess =
                    _currentBatch;

                _currentBatch =
                    new List<BatchTranslationItem>();

                _lastBatchProcessTime =
                    DateTime.UtcNow;
            }

            Task.Run(
                async delegate
                {
                    await ProcessBatchAsync(
                        batchToProcess);
                });
        }

        private async Task ProcessBatchAsync(
            List<BatchTranslationItem> batch)
        {
            if (batch == null ||
                batch.Count == 0)
            {
                return;
            }

            DateTime startTime =
                DateTime.UtcNow;

            try
            {
                var groups =
                    batch
                        .GroupBy(
                            item =>
                                new BatchGroupKey(
                                    item.TargetLanguage,
                                    item.StrategyType))
                        .ToList();

                foreach (IGrouping<BatchGroupKey, BatchTranslationItem> group
                         in groups)
                {
                    List<BatchTranslationItem> items =
                        group.ToList();

                    string[] texts =
                        items
                            .Select(
                                item => item.Text)
                            .ToArray();

                    string[] results =
                        null;

                    IBatchTranslationService batchService =
                        _baseService as
                            IBatchTranslationService;

                    if (batchService != null)
                    {
                        results =
                            await ExecuteWithConcurrencyLimit(
                                delegate
                                {
                                    return batchService.TranslateBatchAsync(
                                        texts,
                                        group.Key.TargetLanguage,
                                        group.Key.StrategyType);
                                });
                    }
                    else
                    {
                        var tasks =
                            new Task<string>[items.Count];

                        for (int i = 0;
                             i < items.Count;
                             i++)
                        {
                            BatchTranslationItem item =
                                items[i];

                            tasks[i] =
                                ExecuteWithConcurrencyLimit(
                                    delegate
                                    {
                                        return _baseService.TranslateAsync(
                                            item.Text,
                                            item.TargetLanguage,
                                            item.StrategyType);
                                    });
                        }

                        results =
                            await Task.WhenAll(
                                tasks);
                    }

                    for (int i = 0;
                         i < items.Count;
                         i++)
                    {
                        BatchTranslationItem item =
                            items[i];

                        string result =
                            item.Text;

                        if (results != null &&
                            i < results.Length &&
                            !string.IsNullOrWhiteSpace(
                                results[i]))
                        {
                            result =
                                results[i];
                        }

                        if (_enableSmartCache &&
                            !string.IsNullOrWhiteSpace(
                                result))
                        {
                            AddToCache(
                                item.CacheKey,
                                result);
                        }

                        item.CompletionSource.TrySetResult(
                            result);
                    }
                }

                UpdateStats(
                    delegate (PerformanceStats stats)
                    {
                        stats.BatchProcessedRequests +=
                            batch.Count;
                    });

                UpdateAverageResponseTime(
                    DateTime.UtcNow -
                    startTime);

                _logger.LogInformation(
                    $"Toplu çeviri tamamlandı. {batch.Count} öğe işlendi.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Toplu çeviri sırasında hata oluştu.",
                    ex);

                for (int i = 0;
                     i < batch.Count;
                     i++)
                {
                    batch[i]
                        .CompletionSource
                        .TrySetException(
                            ex);
                }

                UpdateStats(
                    delegate (PerformanceStats stats)
                    {
                        stats.FailedRequests +=
                            batch.Count;
                    });
            }
        }

        private bool TryGetFromCache(
            string cacheKey,
            out string result)
        {
            result =
                null;

            if (string.IsNullOrWhiteSpace(
                cacheKey))
            {
                return false;
            }

            try
            {
                result =
                    _cacheManager.GetTranslation(
                        cacheKey);

                if (!string.IsNullOrWhiteSpace(
                    result))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Kalıcı önbellek okunamadı: {ex.Message}");
            }

            CacheEntry cacheEntry;

            if (!_smartCache.TryGetValue(
                cacheKey,
                out cacheEntry))
            {
                return false;
            }

            cacheEntry.LastAccessTime =
                DateTime.UtcNow;

            Interlocked.Increment(
                ref cacheEntry.AccessCount);

            result =
                cacheEntry.TranslatedText;

            return !string.IsNullOrWhiteSpace(
                result);
        }

        private void AddToCache(
            string cacheKey,
            string translatedText)
        {
            if (string.IsNullOrWhiteSpace(
                    cacheKey) ||
                string.IsNullOrWhiteSpace(
                    translatedText))
            {
                return;
            }

            try
            {
                _cacheManager.AddTranslation(
                    cacheKey,
                    translatedText);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Kalıcı önbelleğe yazılamadı: {ex.Message}");
            }

            var entry =
                new CacheEntry
                {
                    TranslatedText =
                        translatedText,

                    LastAccessTime =
                        DateTime.UtcNow,

                    AccessCount =
                        1,

                    CreationTime =
                        DateTime.UtcNow,

                    Size =
                        CalculateStringSize(
                            translatedText)
                };

            _smartCache[cacheKey] =
                entry;

            if (_smartCache.Count >
                _maxCacheSize)
            {
                CleanupSmartCache(
                    true);
            }
        }

        private void CleanupCache(
            object state)
        {
            if (Volatile.Read(
                    ref _disposed) != 0)
            {
                return;
            }

            try
            {
                _cacheManager.ExpireEntries();

                CleanupSmartCache(
                    false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Önbellek temizleme sırasında hata oluştu.",
                    ex);
            }
        }

        private void CleanupSmartCache(
            bool force)
        {
            int count =
                _smartCache.Count;

            if (count == 0)
            {
                return;
            }

            int threshold =
                (int)(
                    _maxCacheSize *
                    _cacheCleanupThreshold);

            if (!force &&
                count <= threshold)
            {
                return;
            }

            int targetCount =
                (int)(
                    _maxCacheSize *
                    0.7);

            targetCount =
                Math.Max(
                    0,
                    targetCount);

            int removeCount =
                Math.Max(
                    0,
                    count -
                    targetCount);

            if (removeCount == 0)
            {
                return;
            }

            List<KeyValuePair<string, CacheEntry>> entries =
                _smartCache
                    .OrderBy(
                        pair =>
                            pair.Value.LastAccessTime)
                    .ThenBy(
                        pair =>
                            pair.Value.AccessCount)
                    .Take(
                        removeCount)
                    .ToList();

            for (int i = 0;
                 i < entries.Count;
                 i++)
            {
                CacheEntry ignored;

                _smartCache.TryRemove(
                    entries[i].Key,
                    out ignored);
            }

            UpdateStats(
                delegate (PerformanceStats stats)
                {
                    stats.CacheCleanupCount++;
                });

            _logger.LogInformation(
                $"Smart cache temizlendi. {entries.Count} öğe kaldırıldı. " +
                $"Kalan: {_smartCache.Count}");
        }

        private static long CalculateStringSize(
            string text)
        {
            if (string.IsNullOrEmpty(
                text))
            {
                return 0;
            }

            return
                text.Length *
                sizeof(char);
        }

        public void ClearCache()
        {
            ThrowIfDisposed();

            _smartCache.Clear();

            try
            {
                Dictionary<string, string> emptyCache =
                    new Dictionary<string, string>();

                _cacheManager.SaveCache(
                    emptyCache);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Kalıcı çeviri önbelleği temizlenemedi: {ex.Message}");
            }

            UpdateStats(
                delegate (PerformanceStats stats)
                {
                    stats.CacheHits =
                        0;

                    stats.CacheMisses =
                        0;

                    stats.CacheCleanupCount++;
                });

            _logger.LogInformation(
                "Çeviri önbelleği temizlendi.");
        }

        public CacheInfo GetCacheInfo()
        {
            ThrowIfDisposed();

            CacheEntry[] values =
                _smartCache.Values.ToArray();

            long totalSize =
                values.Sum(
                    entry =>
                        entry.Size);

            int totalItems =
                values.Length;

            PerformanceStats statistics =
                GetStatisticsSnapshot();

            int cacheRequests =
                statistics.CacheHits +
                statistics.CacheMisses;

            double hitRate =
                cacheRequests > 0
                    ? statistics.CacheHits *
                      100.0 /
                      cacheRequests
                    : 0;

            DateTime oldest =
                values.Length > 0
                    ? values.Min(
                        entry =>
                            entry.CreationTime)
                    : DateTime.MinValue;

            int mostAccessed =
                values.Length > 0
                    ? values.Max(
                        entry =>
                            Volatile.Read(
                                ref entry.AccessCount))
                    : 0;

            var info =
                new CacheInfo
                {
                    TotalItems =
                        totalItems,

                    TotalSizeBytes =
                        totalSize,

                    HitRate =
                        hitRate,

                    OldestItem =
                        oldest,

                    MostAccessedItem =
                        mostAccessed
                };

            _logger.LogInformation(
                $"Önbellek bilgisi - Toplam: {totalItems}, " +
                $"Boyut: {totalSize} byte, İsabet: %{hitRate:F2}");

            return info;
        }

        private void UpdateStats(
            Action<PerformanceStats> updateAction)
        {
            if (updateAction == null)
            {
                return;
            }

            PerformanceStats snapshot;

            lock (_statisticsLock)
            {
                updateAction(
                    Statistics);

                snapshot =
                    Statistics.Clone();
            }

            EventHandler<PerformanceStats> handler =
                StatsUpdated;

            if (handler == null)
            {
                return;
            }

            try
            {
                handler(
                    this,
                    snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "StatsUpdated event'i sırasında hata oluştu.",
                    ex);
            }
        }

        private void UpdateAverageResponseTime(
            TimeSpan duration)
        {
            lock (_statisticsLock)
            {
                int completed =
                    Statistics.BatchProcessedRequests +
                    Statistics.IndividualProcessedRequests;

                if (completed <= 0)
                {
                    Statistics.AverageResponseTime =
                        duration;

                    return;
                }

                long previousTicks =
                    Statistics
                        .AverageResponseTime
                        .Ticks;

                long newTicks =
                    (previousTicks *
                     completed +
                     duration.Ticks) /
                    (completed + 1);

                Statistics.AverageResponseTime =
                    TimeSpan.FromTicks(
                        newTicks);
            }
        }

        private PerformanceStats GetStatisticsSnapshot()
        {
            lock (_statisticsLock)
            {
                return Statistics.Clone();
            }
        }

        public (
            long Total,
            long Batch,
            long Individual)
            GetPerformanceStats()
        {
            long total =
                Interlocked.Read(
                    ref _totalTranslations);

            long batch =
                Interlocked.Read(
                    ref _batchTranslations);

            long individual =
                Interlocked.Read(
                    ref _individualTranslations);

            _logger.LogInformation(
                $"Performans İstatistikleri - Toplam: {total}, " +
                $"Toplu: {batch}, Bireysel: {individual}");

            return (
                total,
                batch,
                individual);
        }

        private Type ResolveStrategyType(
            string strategyId)
        {
            if (string.IsNullOrWhiteSpace(
                strategyId))
            {
                return null;
            }

            string normalized =
                strategyId
                    .Trim()
                    .ToLowerInvariant();

            switch (normalized)
            {
                case "ollama":
                    return typeof(
                        OllamaTranslationStrategy);

                case "google-contextual":
                case "google-smart":
                case "google-smart-translation":
                    return typeof(
                        ContextualGoogleTranslationStrategy);

                case "google":
                    return typeof(
                        GoogleTranslationStrategy);

                case "deepl":
                    return typeof(
                        DeepLWebScrapingStrategy);

                case "bing":
                    return typeof(
                        BingWebTranslationStrategy);

                case "yandex":
                    return typeof(
                        YandexWebScrapingStrategy);

                default:
                    _logger.LogWarning(
                        $"Bilinmeyen çeviri stratejisi ID'si: {strategyId}");

                    return null;
            }
        }

        private static string NormalizeTargetLanguage(
            string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(
                targetLanguage))
            {
                return "tr";
            }

            return targetLanguage
                .Trim()
                .Replace(
                    '_',
                    '-')
                .ToLowerInvariant();
        }

        private static string GenerateCacheKey(
            string text,
            string targetLanguage,
            Type strategyType)
        {
            string normalizedText =
                NormalizeTextForCache(
                    text);

            string normalizedTarget =
                NormalizeTargetLanguage(
                    targetLanguage);

            string strategyIdentity =
                strategyType != null
                    ? strategyType.FullName
                    : "default";

            string material =
                normalizedTarget +
                "\n" +
                strategyIdentity +
                "\n" +
                normalizedText;

            using (SHA256 sha256 =
                   SHA256.Create())
            {
                byte[] input =
                    Encoding.UTF8.GetBytes(
                        material);

                byte[] hash =
                    sha256.ComputeHash(
                        input);

                var builder =
                    new StringBuilder(
                        hash.Length * 2);

                for (int i = 0;
                     i < hash.Length;
                     i++)
                {
                    builder.Append(
                        hash[i].ToString(
                            "x2"));
                }

                return builder.ToString();
            }
        }

        private static string NormalizeTextForCache(
            string text)
        {
            if (string.IsNullOrWhiteSpace(
                text))
            {
                return string.Empty;
            }

            var builder =
                new StringBuilder(
                    text.Length);

            bool previousWhitespace =
                false;

            string trimmed =
                text.Trim();

            for (int i = 0;
                 i < trimmed.Length;
                 i++)
            {
                char current =
                    trimmed[i];

                if (char.IsWhiteSpace(
                    current))
                {
                    if (!previousWhitespace)
                    {
                        builder.Append(
                            ' ');

                        previousWhitespace =
                            true;
                    }

                    continue;
                }

                builder.Append(
                    current);

                previousWhitespace =
                    false;
            }

            return builder.ToString();
        }

        private async Task<T> ExecuteWithConcurrencyLimit<T>(
            Func<Task<T>> taskFactory)
        {
            if (taskFactory == null)
            {
                throw new ArgumentNullException(
                    nameof(taskFactory));
            }

            ThrowIfDisposed();

            await _concurrencySemaphore.WaitAsync();

            try
            {
                ThrowIfDisposed();

                return await taskFactory();
            }
            finally
            {
                _concurrencySemaphore.Release();
            }
        }

        private static string CreatePreview(
            string text)
        {
            if (string.IsNullOrEmpty(
                text))
            {
                return string.Empty;
            }

            string normalized =
                text
                    .Replace(
                        '\r',
                        ' ')
                    .Replace(
                        '\n',
                        ' ');

            int length =
                Math.Min(
                    50,
                    normalized.Length);

            string preview =
                normalized.Substring(
                    0,
                    length);

            if (normalized.Length >
                length)
            {
                preview +=
                    "...";
            }

            return preview;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(
                    ref _disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(
                        PerformanceOptimizedTranslationService));
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

            _logger.LogInformation(
                "PerformanceOptimizedTranslationService kapatılıyor...");

            Timer batchTimer =
                Interlocked.Exchange(
                    ref _batchTimer,
                    null);

            Timer cleanupTimer =
                Interlocked.Exchange(
                    ref _cacheCleanupTimer,
                    null);

            if (batchTimer != null)
            {
                batchTimer.Dispose();
            }

            if (cleanupTimer != null)
            {
                cleanupTimer.Dispose();
            }

            lock (_batchLock)
            {
                for (int i = 0;
                     i < _currentBatch.Count;
                     i++)
                {
                    _currentBatch[i]
                        .CompletionSource
                        .TrySetException(
                            new ObjectDisposedException(
                                nameof(
                                    PerformanceOptimizedTranslationService)));
                }

                _currentBatch.Clear();
            }

            _ongoingTranslations.Clear();

            try
            {
                _cacheManager.ExpireEntries();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Servis kapanırken önbellek temizlenemedi: {ex.Message}");
            }

            _concurrencySemaphore.Dispose();

            _logger.LogInformation(
                $"PerformanceOptimizedTranslationService kapatıldı. " +
                $"Toplam çeviri: {Interlocked.Read(ref _totalTranslations)}");

            GC.SuppressFinalize(
                this);
        }

        private sealed class BatchTranslationItem
        {
            public string Text { get; private set; }

            public string TargetLanguage { get; private set; }

            public Type StrategyType { get; private set; }

            public TaskCompletionSource<string> CompletionSource
            {
                get;
                private set;
            }

            public string CacheKey { get; private set; }

            public DateTime CreatedAt { get; private set; }

            public BatchTranslationItem(
                string text,
                string targetLanguage,
                Type strategyType,
                TaskCompletionSource<string> completionSource,
                string cacheKey)
            {
                Text =
                    text;

                TargetLanguage =
                    targetLanguage;

                StrategyType =
                    strategyType;

                CompletionSource =
                    completionSource;

                CacheKey =
                    cacheKey;

                CreatedAt =
                    DateTime.UtcNow;
            }
        }

        private sealed class BatchGroupKey :
            IEquatable<BatchGroupKey>
        {
            public string TargetLanguage
            {
                get;
                private set;
            }

            public Type StrategyType
            {
                get;
                private set;
            }

            public BatchGroupKey(
                string targetLanguage,
                Type strategyType)
            {
                TargetLanguage =
                    targetLanguage;

                StrategyType =
                    strategyType;
            }

            public bool Equals(
                BatchGroupKey other)
            {
                if (ReferenceEquals(
                    other,
                    null))
                {
                    return false;
                }

                return
                    string.Equals(
                        TargetLanguage,
                        other.TargetLanguage,
                        StringComparison.OrdinalIgnoreCase) &&
                    StrategyType ==
                    other.StrategyType;
            }

            public override bool Equals(
                object obj)
            {
                return Equals(
                    obj as BatchGroupKey);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash =
                        17;

                    hash =
                        hash * 31 +
                        StringComparer
                            .OrdinalIgnoreCase
                            .GetHashCode(
                                TargetLanguage ??
                                string.Empty);

                    hash =
                        hash * 31 +
                        (StrategyType != null
                            ? StrategyType.GetHashCode()
                            : 0);

                    return hash;
                }
            }
        }

        private sealed class CacheEntry
        {
            public string TranslatedText { get; set; }

            public DateTime LastAccessTime { get; set; }

            public int AccessCount;

            public DateTime CreationTime { get; set; }

            public long Size { get; set; }
        }

        public sealed class CacheInfo
        {
            public int TotalItems { get; set; }

            public long TotalSizeBytes { get; set; }

            public double HitRate { get; set; }

            public DateTime OldestItem { get; set; }

            public int MostAccessedItem { get; set; }
        }

        public sealed class PerformanceStats
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

            public double CacheHitRate
            {
                get
                {
                    int total =
                        CacheHits +
                        CacheMisses;

                    if (total <= 0)
                    {
                        return 0;
                    }

                    return
                        CacheHits *
                        100.0 /
                        total;
                }
            }

            public double SuccessRate
            {
                get
                {
                    if (TotalRequests <= 0)
                    {
                        return 0;
                    }

                    return
                        Math.Max(
                            0,
                            TotalRequests -
                            FailedRequests) *
                        100.0 /
                        TotalRequests;
                }
            }

            internal PerformanceStats Clone()
            {
                return new PerformanceStats
                {
                    TotalRequests =
                        TotalRequests,

                    BatchProcessedRequests =
                        BatchProcessedRequests,

                    IndividualProcessedRequests =
                        IndividualProcessedRequests,

                    CacheHits =
                        CacheHits,

                    CacheMisses =
                        CacheMisses,

                    FailedRequests =
                        FailedRequests,

                    CacheCleanupCount =
                        CacheCleanupCount,

                    AverageResponseTime =
                        AverageResponseTime,

                    ConcurrentRequests =
                        ConcurrentRequests
                };
            }
        }
    }

    public interface IBatchTranslationService :
        ITranslationService
    {
        Task<string[]> TranslateBatchAsync(
            string[] texts,
            string targetLanguage,
            Type strategyType = null);
    }
}