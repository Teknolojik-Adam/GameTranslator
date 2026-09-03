using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace GameTranslatorUltimate
{
    public class TranslationCacheManager : IDisposable
    {
        private sealed class CacheEntry
        {
            public string TranslatedText { get; set; }
            public DateTime LastAccessTime { get; set; }
        }

        private readonly string _cacheFilePath;
        private readonly string _tempFilePath;

        private readonly ILogger _logger;

        private readonly ReaderWriterLockSlim _cacheLock =
            new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

        private readonly object _saveTimerLock = new object();

        private Dictionary<string, CacheEntry> _cache;

        private Timer _saveTimer;

        private bool _hasUnsavedChanges;
        private bool _disposed;

        private const int MaxCacheSize = 5000;

        private static readonly TimeSpan CacheTimeout =
            TimeSpan.FromHours(24);

        // Birden fazla çeviri peş peşe gelirse her biri için
        // dosyayı tekrar yazmak yerine son değişiklikten sonra bekle.
        private const int SaveDelayMilliseconds = 750;

        public TranslationCacheManager(ILogger logger)
        {
            _logger =
                logger ?? throw new ArgumentNullException(nameof(logger));

            _cacheFilePath =
                Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "translation_cache.json");

            _tempFilePath =
                _cacheFilePath + ".tmp";

            _cache =
                LoadCacheFromFile();

            _saveTimer =
                new Timer(
                    SaveTimerCallback,
                    null,
                    Timeout.Infinite,
                    Timeout.Infinite);
        }

        /// <summary>
        /// Cache içinde kayıtlı çeviriyi döndürür.
        /// Bulunamazsa null döner.
        /// </summary>
        public string GetTranslation(string originalText)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(originalText))
                return null;

            /*
             * LastAccessTime değiştiği için burada yalnızca read lock
             * kullanamayız. CacheEntry üzerinde mutation yapıyoruz.
             */
            _cacheLock.EnterWriteLock();

            try
            {
                CacheEntry entry;

                if (!_cache.TryGetValue(originalText, out entry) ||
                    entry == null)
                {
                    return null;
                }

                // Süresi dolmuş entry ise doğrudan kaldır.
                if (DateTime.UtcNow - entry.LastAccessTime >
                    CacheTimeout)
                {
                    _cache.Remove(originalText);

                    MarkDirty();

                    return null;
                }

                entry.LastAccessTime =
                    DateTime.UtcNow;

                /*
                 * Her cache hit'inde diske yazmıyoruz.
                 * LastAccessTime sonraki normal save veya Dispose
                 * sırasında persist edilir.
                 */
                _hasUnsavedChanges = true;

                return entry.TranslatedText;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Yeni çeviriyi cache'e ekler veya mevcut olanı günceller.
        /// </summary>
        public void AddTranslation(
            string originalText,
            string translatedText)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(originalText))
                return;

            if (translatedText == null)
                return;

            int removedCount = 0;

            _cacheLock.EnterWriteLock();

            try
            {
                _cache[originalText] =
                    new CacheEntry
                    {
                        TranslatedText = translatedText,
                        LastAccessTime = DateTime.UtcNow
                    };

                if (_cache.Count > MaxCacheSize)
                {
                    removedCount =
                        TrimCacheInternal();
                }

                _hasUnsavedChanges = true;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }

            if (removedCount > 0)
            {
                _logger.LogInformation(
                    $"{removedCount} adet eski çeviri cache boyut sınırı nedeniyle silindi.");
            }

            ScheduleSave();
        }

        /// <summary>
        /// Süresi dolmuş cache girdilerini temizler.
        /// </summary>
        public void ExpireEntries()
        {
            ThrowIfDisposed();

            int removedCount = 0;

            DateTime now =
                DateTime.UtcNow;

            _cacheLock.EnterWriteLock();

            try
            {
                List<string> expiredKeys =
                    _cache
                        .Where(pair =>
                            pair.Value == null ||
                            now - pair.Value.LastAccessTime >
                            CacheTimeout)
                        .Select(pair => pair.Key)
                        .ToList();

                foreach (string key in expiredKeys)
                {
                    if (_cache.Remove(key))
                    {
                        removedCount++;
                    }
                }

                if (removedCount > 0)
                {
                    _hasUnsavedChanges = true;
                }
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }

            if (removedCount > 0)
            {
                _logger.LogInformation(
                    $"{removedCount} adet zamanı dolmuş çeviri önbellekten silindi.");

                ScheduleSave();
            }
        }

        /// <summary>
        /// Cache'in string-string snapshot'ını döndürür.
        /// Dışarıya gerçek Dictionary instance verilmez.
        /// </summary>
        public Dictionary<string, string> LoadCache()
        {
            ThrowIfDisposed();

            _cacheLock.EnterReadLock();

            try
            {
                return _cache
                    .Where(pair =>
                        pair.Value != null)
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.TranslatedText,
                        StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Cache içeriğini tamamen verilen verilerle değiştirir.
        /// Bu metod explicit save olduğu için doğrudan diske yazar.
        /// </summary>
        public void SaveCache(
            Dictionary<string, string> cacheData)
        {
            ThrowIfDisposed();

            if (cacheData == null)
                return;

            _cacheLock.EnterWriteLock();

            try
            {
                _cache.Clear();

                foreach (KeyValuePair<string, string> pair in cacheData)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                        continue;

                    if (pair.Value == null)
                        continue;

                    _cache[pair.Key] =
                        new CacheEntry
                        {
                            TranslatedText = pair.Value,
                            LastAccessTime = DateTime.UtcNow
                        };
                }

                TrimCacheInternal();

                _hasUnsavedChanges = true;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }

            SaveChangesToFile();
        }

        /// <summary>
        /// Bekleyen cache değişikliklerini anında diske yazar.
        /// Uygulama kapanırken kullanılabilir.
        /// </summary>
        public void Flush()
        {
            ThrowIfDisposed();

            SaveChangesToFile();
        }

        private int TrimCacheInternal()
        {
            int itemsToRemove =
                _cache.Count - MaxCacheSize;

            if (itemsToRemove <= 0)
                return 0;

            List<string> keysToRemove =
                _cache
                    .OrderBy(pair =>
                        pair.Value?.LastAccessTime ??
                        DateTime.MinValue)
                    .Take(itemsToRemove)
                    .Select(pair => pair.Key)
                    .ToList();

            int removedCount = 0;

            foreach (string key in keysToRemove)
            {
                if (_cache.Remove(key))
                {
                    removedCount++;
                }
            }

            return removedCount;
        }

        private Dictionary<string, CacheEntry>
            LoadCacheFromFile()
        {
            if (!File.Exists(_cacheFilePath))
            {
                _logger.LogInformation(
                    "Çeviri önbellek dosyası bulunamadı. Yeni önbellek oluşturuluyor.");

                return CreateEmptyCache();
            }

            try
            {
                string json =
                    File.ReadAllText(_cacheFilePath);

                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger.LogWarning(
                        "Çeviri önbellek dosyası boş. Yeni önbellek oluşturuluyor.");

                    return CreateEmptyCache();
                }

                Dictionary<string, CacheEntry> newFormatCache =
                    TryLoadNewFormat(json);

                if (newFormatCache != null)
                {
                    _logger.LogInformation(
                        $"{newFormatCache.Count} adet çeviri önbellekten yüklendi.");

                    return newFormatCache;
                }

                Dictionary<string, CacheEntry> oldFormatCache =
                    TryLoadOldFormat(json);

                if (oldFormatCache != null)
                {
                    _logger.LogInformation(
                        $"{oldFormatCache.Count} adet çeviri eski cache formatından dönüştürüldü.");

                    return oldFormatCache;
                }

                _logger.LogWarning(
                    "Çeviri önbellek dosyası geçersiz formatta.");

                BackupCorruptedCacheFile();

                return CreateEmptyCache();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Çeviri önbelleği yüklenirken hata oluştu.",
                    ex);

                BackupCorruptedCacheFile();

                return CreateEmptyCache();
            }
        }

        private Dictionary<string, CacheEntry>
            TryLoadNewFormat(string json)
        {
            try
            {
                Dictionary<string, CacheEntry> cache =
                    JsonSerializer.Deserialize<
                        Dictionary<string, CacheEntry>>(json);

                if (cache == null)
                    return null;

                var result =
                    new Dictionary<string, CacheEntry>(
                        StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, CacheEntry> pair in cache)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                        continue;

                    if (pair.Value == null)
                        continue;

                    if (pair.Value.TranslatedText == null)
                        continue;

                    if (pair.Value.LastAccessTime ==
                        default(DateTime))
                    {
                        pair.Value.LastAccessTime =
                            DateTime.UtcNow;
                    }

                    result[pair.Key] =
                        pair.Value;
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        private Dictionary<string, CacheEntry>
            TryLoadOldFormat(string json)
        {
            try
            {
                Dictionary<string, string> oldCache =
                    JsonSerializer.Deserialize<
                        Dictionary<string, string>>(json);

                if (oldCache == null)
                    return null;

                var convertedCache =
                    CreateEmptyCache();

                foreach (KeyValuePair<string, string> pair in oldCache)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                        continue;

                    if (pair.Value == null)
                        continue;

                    convertedCache[pair.Key] =
                        new CacheEntry
                        {
                            TranslatedText = pair.Value,
                            LastAccessTime = DateTime.UtcNow
                        };
                }

                return convertedCache;
            }
            catch
            {
                return null;
            }
        }

        private Dictionary<string, CacheEntry>
            CreateEmptyCache()
        {
            return new Dictionary<string, CacheEntry>(
                StringComparer.OrdinalIgnoreCase);
        }

        private void ScheduleSave()
        {
            if (_disposed)
                return;

            lock (_saveTimerLock)
            {
                if (_saveTimer == null)
                    return;

                /*
                 * Her yeni ekleme timer'ı baştan başlatır.
                 *
                 * Örnek:
                 * 50 paralel çeviri tamamlandı
                 *     ↓
                 * 50 ayrı disk write yerine
                 *     ↓
                 * yaklaşık 1 adet JSON write
                 */
                _saveTimer.Change(
                    SaveDelayMilliseconds,
                    Timeout.Infinite);
            }
        }

        private void SaveTimerCallback(object state)
        {
            if (_disposed)
                return;

            try
            {
                SaveChangesToFile();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Zamanlanmış cache kaydı sırasında hata oluştu.",
                    ex);
            }
        }

        private void SaveChangesToFile()
        {
            if (_disposed)
                return;

            Dictionary<string, CacheEntry> snapshot;

            _cacheLock.EnterReadLock();

            try
            {
                if (!_hasUnsavedChanges)
                    return;

                snapshot =
                    _cache.ToDictionary(
                        pair => pair.Key,
                        pair => new CacheEntry
                        {
                            TranslatedText =
                                pair.Value?.TranslatedText,

                            LastAccessTime =
                                pair.Value?.LastAccessTime ??
                                DateTime.UtcNow
                        },
                        StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }

            try
            {
                var options =
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };

                string json =
                    JsonSerializer.Serialize(
                        snapshot,
                        options);

                /*
                 * Önce geçici dosyaya yaz.
                 *
                 * Böylece uygulama JSON yazılırken çökerse ana cache
                 * dosyasının yarım kalma ihtimali ciddi şekilde azalır.
                 */
                File.WriteAllText(
                    _tempFilePath,
                    json);

                ReplaceCacheFileAtomically();

                _cacheLock.EnterWriteLock();

                try
                {
                    /*
                     * Buradaki flag konusunda küçük bir yarış ihtimali
                     * olabilir: snapshot alındıktan sonra yeni entry eklenmişse
                     * onun dirty durumunu yanlışlıkla temizlemek istemiyoruz.
                     *
                     * Bu yüzden snapshot ile güncel cache boyutu kontrol edilir.
                     */
                    if (_cache.Count == snapshot.Count)
                    {
                        _hasUnsavedChanges = false;
                    }
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Çeviri önbelleği diske kaydedilirken hata oluştu.",
                    ex);

                SafeDeleteTempFile();
            }
        }

        private void ReplaceCacheFileAtomically()
        {
            if (!File.Exists(_cacheFilePath))
            {
                File.Move(
                    _tempFilePath,
                    _cacheFilePath);

                return;
            }

            string backupPath =
                _cacheFilePath + ".bak";

            try
            {
                File.Replace(
                    _tempFilePath,
                    _cacheFilePath,
                    backupPath,
                    true);

                TryDeleteFile(
                    backupPath);
            }
            catch
            {
                /*
                 * File.Replace bazı dosya sistemlerinde/problemli
                 * ortamlarda başarısız olabilir.
                 *
                 * Fallback:
                 * eski dosyayı kaldır ve temp dosyayı yerine taşı.
                 */

                TryDeleteFile(
                    _cacheFilePath);

                File.Move(
                    _tempFilePath,
                    _cacheFilePath);
            }
        }

        private void BackupCorruptedCacheFile()
        {
            try
            {
                if (!File.Exists(_cacheFilePath))
                    return;

                string backupPath =
                    _cacheFilePath +
                    ".corrupt_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss");

                File.Copy(
                    _cacheFilePath,
                    backupPath,
                    true);

                _logger.LogWarning(
                    $"Bozuk cache dosyası yedeklendi: " +
                    $"{Path.GetFileName(backupPath)}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Bozuk cache dosyası yedeklenemedi: {ex.Message}");
            }
        }

        private void SafeDeleteTempFile()
        {
            TryDeleteFile(
                _tempFilePath);
        }

        private void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Cleanup hatası ana işlemi bozmamalı.
            }
        }

        private void MarkDirty()
        {
            _hasUnsavedChanges = true;

            ScheduleSave();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(TranslationCacheManager));
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            /*
             * Önce son değişiklikleri yaz.
             * _disposed henüz true yapılmıyor çünkü
             * SaveChangesToFile dispose kontrolü yapıyor.
             */
            try
            {
                SaveChangesToFile();
            }
            catch
            {
                // SaveChangesToFile zaten logluyor.
            }

            _disposed = true;

            lock (_saveTimerLock)
            {
                if (_saveTimer != null)
                {
                    try
                    {
                        _saveTimer.Change(
                            Timeout.Infinite,
                            Timeout.Infinite);

                        _saveTimer.Dispose();
                    }
                    catch
                    {
                    }

                    _saveTimer = null;
                }
            }

            try
            {
                _cacheLock.Dispose();
            }
            catch
            {
            }
        }
    }
}