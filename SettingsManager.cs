using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace P5S_ceviri 
{
    public class SettingsManager
    {
        private readonly ILogger _logger;
     
        private readonly string _settingsFilePath;

        public SettingsManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // Uygulama dizininde "appsettings.json" dosyası oluşturmak için
            _settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        }
        public AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        _logger.LogWarning($"Ayar dosyası boş: '{_settingsFilePath}'. Varsayılan ayarlar kullanılacak.");
                        return new AppSettings();
                    }

                    // JSON'u AppSettings nesnesine deserialize et
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true, 
                        Converters = { new JsonStringEnumConverter() } 
                        
                    };

                    var settings = JsonSerializer.Deserialize<AppSettings>(json, options);

                    if (settings != null)
                    {
                        _logger.LogInformation($"Ayarlar '{_settingsFilePath}' dosyasından yüklendi.");

                        return settings;
                    }
                    else
                    {
                        _logger.LogWarning($"Ayarlar dosyası deserialize edilemedi: '{_settingsFilePath}'. Varsayılan ayarlar kullanılacak.");
                    }
                }
                else
                {
                    _logger.LogInformation($"Ayar dosyası bulunamadı: '{_settingsFilePath}'. Varsayılan ayarlar kullanılacak.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Ayarlar yüklenirken hata oluştu: '{_settingsFilePath}'. Hata: {ex.Message}", ex);
            }

           
            return new AppSettings();
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

                // JSON seçeneklerini ayarla (okunabilirlik için girintileme)
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() } 
                };

                // Ayarları JSON string'ine serialize et
                string jsonString = JsonSerializer.Serialize(settings, options);

                // JSON string'ini dosyaya yaz
                File.WriteAllText(_settingsFilePath, jsonString);

                _logger.LogInformation($"Ayarlar '{_settingsFilePath}' dosyasına kaydedildi.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Ayarlar kaydedilemedi: '{_settingsFilePath}'. Hata: {ex.Message}", ex);
            }
        }

    }
}