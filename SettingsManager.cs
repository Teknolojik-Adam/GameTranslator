using System;
using System.IO;
using System.Text.Json;

namespace P5S_ceviri
{
    public class SettingsManager
    {
        private readonly string _fileName = "settings.json";
        private readonly ILogger _logger;

        public SettingsManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void SaveSettings(AppSettings settings)
        {
            if (settings == null)
            {
                _logger.LogWarning("Ayarlar nesnesi null olduğu için kaydetme işlemi iptal edildi.");
                return;
            }
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(_fileName, jsonString);
                _logger.LogInformation($"Ayarlar '{_fileName}' dosyasına kaydedildi.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Ayarlar kaydedilemedi: '{_fileName}'", ex);
            }
        }

        public AppSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(_fileName))
                {
                    _logger.LogInformation($"Ayar dosyası bulunamadı: '{_fileName}'. Varsayılan ayarlar kullanılacak.");
                    return new AppSettings();
                }
                string jsonString = File.ReadAllText(_fileName);
                if (string.IsNullOrWhiteSpace(jsonString))
                {
                    _logger.LogWarning($"Ayar dosyası boş: '{_fileName}'. Varsayılan ayarlar kullanılacak.");
                    return new AppSettings();
                }
                var settings = JsonSerializer.Deserialize<AppSettings>(jsonString);
                _logger.LogInformation($"Ayarlar '{_fileName}' dosyasından yüklendi.");
                return settings ?? new AppSettings();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Ayarlar yüklenemedi: '{_fileName}'", ex);
                return new AppSettings();
            }
        }
    }
}