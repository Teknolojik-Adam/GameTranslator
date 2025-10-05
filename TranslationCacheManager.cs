using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace P5S_ceviri
{
    public class TranslationCacheManager
    {
       
        private class CacheEntry
        {
            public string TranslatedText { get; set; }
            public DateTime LastAccessTime { get; set; }
        }

        private readonly string _cacheFilePath;
        private readonly ILogger _logger;
     
        private Dictionary<string, CacheEntry> _cache;

        
        private readonly int _maxCacheSize = 5000;
        private readonly TimeSpan _cacheTimeout = TimeSpan.FromHours(24);

      
        private static readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        public TranslationCacheManager(ILogger logger)
        {
            _logger = logger;
            _cacheFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "translation_cache.json");
            
            _cache = LoadCacheFromFile();
        }

        
        public string GetTranslation(string originalText)
        {
            if (_cache.TryGetValue(originalText, out CacheEntry entry))
            {
             
                entry.LastAccessTime = DateTime.UtcNow;
                return entry.TranslatedText;
            }
            return null;
        }

        
        public void AddTranslation(string originalText, string translatedText)
        {
            _cache[originalText] = new CacheEntry
            {
                TranslatedText = translatedText,
                LastAccessTime = DateTime.UtcNow
            };

            // Önbellek maksimum boyutu aştıysa, en eski girdileri temizle.
            if (_cache.Count > _maxCacheSize)
            {
                TrimCache();
            }

            SaveChangesToFile();
        }

        // Zamanı dolmuş önbellek girdilerini temizler.
        public void ExpireEntries()
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _cache.Where(pair => now - pair.Value.LastAccessTime > _cacheTimeout)
                                    .Select(pair => pair.Key)
                                    .ToList();

            if (expiredKeys.Any())
            {
                foreach (var key in expiredKeys)
                {
                    _cache.Remove(key);
                }
                _logger.LogInformation($"{expiredKeys.Count} adet zamanı dolmuş çeviri önbellekten silindi.");
                SaveChangesToFile();
            }
        }

       
        public Dictionary<string, string> LoadCache()
        {
            return _cache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.TranslatedText);
        }

       
        public void SaveCache(Dictionary<string, string> cacheData)
        {
            if (cacheData == null) return;

            _cache.Clear();
            foreach (var kvp in cacheData)
            {
                _cache[kvp.Key] = new CacheEntry
                {
                    TranslatedText = kvp.Value,
                    LastAccessTime = DateTime.UtcNow
                };
            }

            SaveChangesToFile();
        }

        // Önbelleği maksimum boyuta getirmek için en son kullanılanları tutar, en eskileri atar .
        private void TrimCache()
        {
            int itemsToRemove = _cache.Count - _maxCacheSize;
            if (itemsToRemove <= 0) return;

           
            var keysToRemove = _cache.OrderBy(pair => pair.Value.LastAccessTime)
                                     .Take(itemsToRemove)
                                     .Select(pair => pair.Key)
                                     .ToList();

            foreach (var key in keysToRemove)
            {
                _cache.Remove(key);
            }
            _logger.LogInformation($"{keysToRemove.Count} adet en eski çeviri, boyut sınırı için önbellekten silindi.");
        }

    
        private Dictionary<string, CacheEntry> LoadCacheFromFile()
        {
            if (!File.Exists(_cacheFilePath))
            {
                _logger.LogInformation("Çeviri önbellek dosyası bulunamadı. Yeni bir tane oluşturulacak.");
                return new Dictionary<string, CacheEntry>();
            }

            try
            {
                _lock.EnterReadLock();
                string json = File.ReadAllText(_cacheFilePath);
                
              
                try
                {
                    var cache = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
                    _logger.LogInformation($"{cache.Count} adet çeviri önbellekten yüklendi.");
                    return cache ?? new Dictionary<string, CacheEntry>();
                }
                catch
                {
                    
                    try
                    {
                        var oldCache = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                        var newCache = new Dictionary<string, CacheEntry>();
                        
                        foreach (var kvp in oldCache)
                        {
                            newCache[kvp.Key] = new CacheEntry
                            {
                                TranslatedText = kvp.Value,
                                LastAccessTime = DateTime.UtcNow
                            };
                        }
                        
                        _logger.LogInformation($"{newCache.Count} adet çeviri eski formattan yeni formata dönüştürüldü.");
                        return newCache;
                    }
                    catch
                    {
                        _logger.LogWarning("Önbellek dosyası geçersiz format. Yeni önbellek oluşturuluyor.");
                        return new Dictionary<string, CacheEntry>();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Çeviri önbelleği yüklenirken hata oluştu.", ex);
                return new Dictionary<string, CacheEntry>();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        
        private void SaveChangesToFile()
        {
            try
            {
                _lock.EnterWriteLock();
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_cache, options);
                File.WriteAllText(_cacheFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError("Çeviri önbelleği kaydedilirken hata oluştu.", ex);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
    }
}