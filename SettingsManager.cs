using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Input;

namespace P5S_ceviri
{
    public class SettingsManager
    {
        private readonly ILogger _logger;
        private readonly string _settingsFilePath;
        private readonly object _lockObject = new object();
       

        public SettingsManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        }
        //
        public AppSettings LoadSettings()
        {
            lock (_lockObject)
            {
                try
                {
                    if (File.Exists(_settingsFilePath))
                    {
                        string json = File.ReadAllText(_settingsFilePath);
                        if (string.IsNullOrWhiteSpace(json))
                        {
                            _logger.LogWarning($"Ayar dosyası boş: '{_settingsFilePath}'. Varsayılan ayarlar kullanılacak.");
                            return new AppSettings(_logger);
                        }
                        var options = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            Converters = {
                                new JsonStringEnumConverter(),
                                new HotkeyJsonConverter()
                            }
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
                return new AppSettings(_logger);
            }
        }

        public void SaveSettings(AppSettings settings)
        {
            if (settings == null)
            {
                _logger.LogWarning("Ayarlar nesnesi null olduğu için kaydetme işlemi iptal edildi.");
                return;
            }
            lock (_lockObject)
            {
                try
                {
                   

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Converters = {
                            new JsonStringEnumConverter(),
                            new HotkeyJsonConverter()
                        }
                    };
                    string jsonString = JsonSerializer.Serialize(settings, options);

                    if (File.Exists(_settingsFilePath))
                    {
                        string backupPath = _settingsFilePath + ".backup";
                        File.Copy(_settingsFilePath, backupPath, true);
                    }
                    File.WriteAllText(_settingsFilePath, jsonString);
                    _logger.LogInformation($"Ayarlar '{_settingsFilePath}' dosyasına kaydedildi.");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Ayarlar kaydedilemedi: '{_settingsFilePath}'. Hata: {ex.Message}", ex);
                    try
                    {
                        string backupPath = _settingsFilePath + ".backup";
                        if (File.Exists(backupPath))
                        {
                            File.Copy(backupPath, _settingsFilePath, true);
                            _logger.LogInformation("Ayarlar yedekten geri yüklendi.");
                        }
                    }
                    catch (Exception backupEx)
                    {
                        _logger.LogError($"Yedekten geri yükleme başarısız: {backupEx.Message}", backupEx);
                    }
                }
            }
        }

    }
    public class HotkeyJsonConverter : JsonConverter<Hotkey>
    {
        public override Hotkey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            if (reader.TokenType != JsonTokenType.String)
                return null;

            try
            {
                var value = reader.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    return null;

                var parts = value.Split(new[] { " + " }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return null;

                var modifiers = ModifierKeys.None;
                
                // Son kısım key, geri kalanlar modifier
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    switch (parts[i].Trim())
                    {
                        case "Ctrl":
                        case "Control":
                            modifiers |= ModifierKeys.Control;
                            break;
                        case "Shift":
                            modifiers |= ModifierKeys.Shift;
                            break;
                        case "Alt":
                            modifiers |= ModifierKeys.Alt;
                            break;
                        case "Win":
                        case "Windows":
                            modifiers |= ModifierKeys.Windows;
                            break;
                    }
                }

                // Son kısım key değeri
                var keyString = parts[parts.Length - 1].Trim();
                if (Enum.TryParse<Key>(keyString, true, out var key))
                {
                    return new Hotkey(modifiers, key);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, Hotkey value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }

            try
            {
                string hotkeyString = value.ToString();
                writer.WriteStringValue(hotkeyString);
            }
            catch
            {
                writer.WriteNullValue();
            }
        }
    }
}