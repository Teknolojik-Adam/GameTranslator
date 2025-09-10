using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public class PerformanceOptimizedTranslationService : ITranslationService, IDisposable
    {
        private readonly ITranslationService _baseService;
        private readonly ILogger _logger;
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

        // Akıllı önbellek için yeni alanlar
        private readonly ConcurrentDictionary<string, CacheEntry> _smartCache;
        private Timer _cacheCleanupTimer;
        private readonly int _maxCacheSize;
        private readonly int _cacheCleanupIntervalMinutes;
        private readonly bool _enableSmartCache;
        private readonly double _cacheCleanupThreshold;
        
        // İstatistikler için
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
            
            var realTimeInterval = TimeSpan.FromMilliseconds(25); // 25ms gerçek zaman a en yakin
            _batchTimer = new Timer(ProcessBatch, null, realTimeInterval, realTimeInterval);
        }

        private void InitializeCacheCleanup()
        {
            _cacheCleanupTimer = new Timer(CleanupCache, null, 
                TimeSpan.FromMinutes(_cacheCleanupIntervalMinutes), 
                TimeSpan.FromMinutes(_cacheCleanupIntervalMinutes));
        }

        public async Task<string> TranslateAsync(string text, string targetLanguage, Type strategyType = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            if (_disposed)
                throw new ObjectDisposedException(nameof(PerformanceOptimizedTranslationService));

            UpdateStats(stats => stats.TotalRequests++);
            UpdateStats(stats => stats.ConcurrentRequests++);

            var startTime = DateTime.UtcNow;
            
            try
            {
                
                string cacheKey = GenerateCacheKey(text, targetLanguage, strategyType);
                
                if (_ongoingTranslations.TryGetValue(cacheKey, out var ongoingTask))
                {
                    _logger.LogInformation($"Reusing ongoing translation for: {text.Substring(0, Math.Min(50, text.Length))}...");
                    return await ongoingTask;
                }

                // Önce önbelleği kontrol et
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
                            _logger.LogInformation($"Processing batch immediately (size: {_currentBatch.Count})");
                            ProcessBatchImmediately();
                        }
                        else if (_currentBatch.Count == 1)
                        {
                           
                            _logger.LogInformation("Processing single item immediately for real-time response");
                            ProcessBatchImmediately();
                        }
                        else if (_enableRealtimeBatchProcessing && _currentBatch.Count > 1 && 
                                 DateTime.UtcNow - _lastBatchProcessTime > TimeSpan.FromMilliseconds(_realtimeBatchThresholdMs))
                        {
                            
                            _logger.LogInformation($"Processing batch due to time threshold (size: {_currentBatch.Count})");
                            ProcessBatchImmediately();
                        }
                    }
                    
                    return await completionSource.Task;
                }
                
                
                Interlocked.Increment(ref _totalTranslations);
                Interlocked.Increment(ref _individualTranslations);
                
                var translationTask = ExecuteWithConcurrencyLimit(() => 
                    _baseService.TranslateAsync(text, targetLanguage, strategyType));
                
                _ongoingTranslations[cacheKey] = translationTask;
                
                try
                {
                    var result = await translationTask;
                    
                    // Sonucu önbelleğe ekle
                    if (_enableSmartCache && !string.IsNullOrEmpty(result))
                    {
                        AddToCache(cacheKey, result);
                    }
                    
                    // İstatistikleri güncelle
                    var duration = DateTime.UtcNow - startTime;
                    UpdateStats(stats => 
                    {
                        stats.AverageResponseTime = TimeSpan.FromTicks(
                            (stats.AverageResponseTime.Ticks * (stats.TotalRequests - 1) + duration.Ticks) / stats.TotalRequests
                        );
                    });
                    
                    _logger.LogInformation($"Individual translation completed for: {text.Substring(0, Math.Min(50, text.Length))}...");
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
                _logger.LogError("Translation failed", ex);
                throw;
            }
            finally
            {
                UpdateStats(stats => stats.ConcurrentRequests--);
            }
        }

        #region Akıllı Önbellek Yönetimi

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
            
            if (_smartCache.TryGetValue(cacheKey, out var cacheEntry))
            {
                // Erişim istatistiklerini güncelle
                cacheEntry.LastAccessTime = DateTime.UtcNow;
                cacheEntry.AccessCount++;
                
                result = cacheEntry.TranslatedText;
                return true;
            }
            
            return false;
        }

        private void AddToCache(string cacheKey, string translatedText)
        {
            if (string.IsNullOrEmpty(translatedText) || _smartCache.Count >= _maxCacheSize)
                return;

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

        private void CleanupCache(object state)
        {
            try
            {
                // Önbellek boyut sınırını aşıldiysa temizlik yap
                if (_smartCache.Count <= _maxCacheSize * _cacheCleanupThreshold) 
                    return;

                // LRU (Least Recently Used) ve LFU (Least Frequently Used) kombinasyonu
                var entriesToRemove = _smartCache
                    .OrderBy(x => x.Value.LastAccessTime) // En az kullanılanlar
                    .ThenBy(x => x.Value.AccessCount)     // En az erişilenler
                    .Take(_smartCache.Count - (int)(_maxCacheSize * 0.7)) // %70'e kadar temizle
                    .ToList();

                foreach (var entry in entriesToRemove)
                {
                    _smartCache.TryRemove(entry.Key, out _);
                }

                _logger.LogInformation($"Önbellek temizlendi. {entriesToRemove.Count} öğe kaldırıldı. Yeni boyut: {_smartCache.Count}");
                
                // İstatistikleri güncelle
                UpdateStats(stats => stats.CacheCleanupCount++);
            }
            catch (Exception ex)
            {
                _logger.LogError("Önbellek temizleme hatası", ex);
            }
        }

        private long CalculateStringSize(string text)
        {
            // Basit bir metin boyutu hesaplama
            if (string.IsNullOrEmpty(text)) return 0;
            
            // UTF-16 kullandığımız için her karakter 2 byte
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
            
            return new CacheInfo
            {
                TotalItems = totalItems,
                TotalSizeBytes = totalSize,
                HitRate = Statistics.TotalRequests > 0 ? 
                    (double)Statistics.CacheHits / (Statistics.CacheHits + Statistics.CacheMisses) * 100 : 0,
                OldestItem = _smartCache.Values.OrderBy(v => v.CreationTime).FirstOrDefault()?.CreationTime ?? DateTime.MinValue,
                MostAccessedItem = _smartCache.Values.OrderByDescending(v => v.AccessCount).FirstOrDefault()?.AccessCount ?? 0
            };
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

        #region İstatistik Yönetimi

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
            return (Interlocked.Read(ref _totalTranslations), 
                   Interlocked.Read(ref _batchTranslations), 
                   Interlocked.Read(ref _individualTranslations));
        }

        #endregion

        #region Yardımcı Metotlar

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
                            _logger.LogInformation($"Processing batch of {texts.Length} items using batch service");
                            var results = await batchService.TranslateBatchAsync(texts, group.Key.TargetLanguage, group.Key.StrategyType);

                            for (int i = 0; i < group.Count(); i++)
                            {
                                var item = group.ElementAt(i);
                                var result = results[i] ?? item.Text; 
                                
                                // Sonucu önbelleğe ekle
                                if (_enableSmartCache && !string.IsNullOrEmpty(result))
                                {
                                    AddToCache(item.CacheKey, result);
                                }
                                
                                item.CompletionSource.SetResult(result);
                            }
                        }
                        else
                        {
                            
                            _logger.LogInformation($"Processing batch of {group.Count()} items using individual translations");
                            var tasks = group.Select(item => 
                                _baseService.TranslateAsync(item.Text, item.TargetLanguage, item.StrategyType));
                            
                            var results = await Task.WhenAll(tasks);
                            
                            for (int i = 0; i < group.Count(); i++)
                            {
                                var item = group.ElementAt(i);
                                var result = results[i] ?? item.Text; 
                                
                                // Sonucu önbelleğe ekle
                                if (_enableSmartCache && !string.IsNullOrEmpty(result))
                                {
                                    AddToCache(item.CacheKey, result);
                                }
                                
                                item.CompletionSource.SetResult(result);
                            }
                        }
                    }
                    
                    // İstatistikleri güncelle
                    UpdateStats(stats => stats.BatchProcessedRequests += batchToProcess.Count);
                    
                    var processingTime = DateTime.UtcNow - startTime;
                    _logger.LogInformation($"Toplu işlem tamamlandı. {batchToProcess.Count} öğe, {processingTime.TotalMilliseconds}ms'de işlendi.");
                }
                catch (Exception ex)
                {
                    _logger.LogError("Toplu çeviri hatası", ex);
                    
                    // Tüm batch öğelerine hata bildir
                    foreach (var item in batchToProcess)
                    {
                        item.CompletionSource.SetException(ex);
                    }
                    
                    UpdateStats(stats => stats.FailedRequests += batchToProcess.Count);
                }
            });
        }

        private string GenerateCacheKey(string text, string targetLanguage, Type strategyType)
        {
            // Önbellek anahtarı oluştur
            var key = $"{text}_{targetLanguage}_{strategyType?.Name}";
            
            // Anahtar çok uzunsa hash kullan
            if (key.Length > 100)
            {
                return $"{key.GetHashCode():X8}_{targetLanguage}_{strategyType?.Name}";
            }
            
            return key;
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
                    _batchTimer?.Dispose();
                    _cacheCleanupTimer?.Dispose();
                    _concurrencySemaphore?.Dispose();
                    
                    // Bekleyen tüm batch öğelerini iptal et
                    lock (_batchLock)
                    {
                        foreach (var item in _currentBatch)
                        {
                            item.CompletionSource.SetException(new ObjectDisposedException("Çeviri servisi kapatıldı"));
                        }
                        _currentBatch.Clear();
                    }
                    
                    // Devam eden çevirileri iptal et
                    foreach (var ongoing in _ongoingTranslations)
                    {
                        
                    }
                    _ongoingTranslations.Clear();
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