using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace P5S_ceviri
{
    public class TranslationCacheManager
    {
        private readonly string _cacheFilePath;
        private readonly ILogger _logger;

        public TranslationCacheManager(ILogger logger)
        {
            _logger = logger;
            //dosya yolu: .../GameTranslator/bin/Debug/translation_cache.json
            _cacheFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "translation_cache.json");
        }

        // Önbelleği diskten yükler. Dosya yoksa boş döndürür.
        public Dictionary<string, string> LoadCache()
        {
            if (!File.Exists(_cacheFilePath))
            {
                _logger.LogInformation("Çeviri önbellek dosyası bulunamadı. Yeni bir tane oluşturulacak.");
                return new Dictionary<string, string>();
            }

            try
            {
                string json = File.ReadAllText(_cacheFilePath);
                var cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                _logger.LogInformation($"{cache.Count} adet çeviri önbellekten yüklendi.");
                return cache ?? new Dictionary<string, string>();
            }
            catch (Exception ex)
            {
                _logger.LogError("Çeviri önbelleği yüklenirken hata oluştu.", ex);
                return new Dictionary<string, string>();
            }
        }
        // verileri önbelleği diske JSON formatında kaydetmek için.
        public void SaveCache(Dictionary<string, string> cache)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(cache, options);
                File.WriteAllText(_cacheFilePath, json);
                _logger.LogInformation($"{cache.Count} adet çeviri önbelleğe kaydedildi.");
            }
            catch (Exception ex)
            {
                _logger.LogError("Çeviri önbelleği kaydedilirken hata oluştu.", ex);
            }
        }
    }
}