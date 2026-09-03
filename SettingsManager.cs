using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Input;

namespace GameTranslatorUltimate
{
    public class SettingsManager
    {
        private static readonly object FileLock = new object();

        private readonly ILogger _logger;
        private readonly string _settingsFilePath;
        private readonly string _backupFilePath;
        private readonly string _tempFilePath;

        public SettingsManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            string baseDirectory =
                AppDomain.CurrentDomain.BaseDirectory;

            _settingsFilePath =
                Path.Combine(baseDirectory, "appsettings.json");

            _backupFilePath =
                _settingsFilePath + ".backup";

            _tempFilePath =
                _settingsFilePath + ".tmp";
        }

        public AppSettings LoadSettings()
        {
            lock (FileLock)
            {
                AppSettings settings =
                    TryLoadSettingsFile(_settingsFilePath);

                if (settings != null)
                {
                    InitializeSettings(settings);

                    _logger.LogInformation(
                        $"Ayarlar '{_settingsFilePath}' dosyasından yüklendi.");

                    return settings;
                }

                if (File.Exists(_backupFilePath))
                {
                    _logger.LogWarning(
                        "Ana ayar dosyası okunamadı. Yedek ayar dosyası deneniyor.");

                    settings =
                        TryLoadSettingsFile(_backupFilePath);

                    if (settings != null)
                    {
                        InitializeSettings(settings);

                        TryRestoreBackup();

                        _logger.LogInformation(
                            "Ayarlar yedek dosyadan başarıyla yüklendi.");

                        return settings;
                    }
                }

                _logger.LogInformation(
                    "Varsayılan uygulama ayarları kullanılacak.");

                return CreateDefaultSettings();
            }
        }

        public void SaveSettings(AppSettings settings)
        {
            if (settings == null)
            {
                _logger.LogWarning(
                    "Ayarlar null olduğu için kaydetme işlemi iptal edildi.");

                return;
            }

            lock (FileLock)
            {
                try
                {
                    JsonSerializerOptions options =
                        CreateJsonOptions(true);

                    string json =
                        JsonSerializer.Serialize(
                            settings,
                            options);

                    WriteSettingsAtomically(json);

                    _logger.LogInformation(
                        $"Ayarlar '{_settingsFilePath}' dosyasına kaydedildi.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        $"Ayarlar kaydedilemedi: '{_settingsFilePath}'.",
                        ex);

                    TryRestoreBackup();
                }
            }
        }

        private AppSettings TryLoadSettingsFile(
            string path)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                string json =
                    File.ReadAllText(
                        path,
                        Encoding.UTF8);

                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger.LogWarning(
                        $"Ayar dosyası boş: '{path}'.");

                    return null;
                }

                JsonSerializerOptions options =
                    CreateJsonOptions(false);

                return JsonSerializer.Deserialize<AppSettings>(
                    json,
                    options);
            }
            catch (JsonException ex)
            {
                _logger.LogError(
                    $"Ayar dosyası geçersiz JSON içeriyor: '{path}'.",
                    ex);

                if (string.Equals(
                    path,
                    _settingsFilePath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    BackupCorruptedSettings();
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    $"Ayar dosyası okunamadı: '{path}'.",
                    ex);

                return null;
            }
        }

        private void InitializeSettings(
            AppSettings settings)
        {
            try
            {
                settings.SetLogger(_logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"AppSettings logger atanamadı: {ex.Message}");
            }
        }

        private AppSettings CreateDefaultSettings()
        {
            try
            {
                return new AppSettings(_logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Varsayılan ayarlar oluşturulamadı.",
                    ex);

                throw;
            }
        }

        private JsonSerializerOptions CreateJsonOptions(
            bool writeIndented)
        {
            var options =
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    WriteIndented = writeIndented,
                    AllowTrailingCommas = true,
                    ReadCommentHandling =
                        JsonCommentHandling.Skip
                };

            options.Converters.Add(
                new JsonStringEnumConverter());

            options.Converters.Add(
                new HotkeyJsonConverter());

            return options;
        }

        private void WriteSettingsAtomically(
            string json)
        {
            SafeDelete(_tempFilePath);

            File.WriteAllText(
                _tempFilePath,
                json,
                new UTF8Encoding(false));

            if (!File.Exists(_settingsFilePath))
            {
                File.Move(
                    _tempFilePath,
                    _settingsFilePath);

                return;
            }

            try
            {
                File.Replace(
                    _tempFilePath,
                    _settingsFilePath,
                    _backupFilePath,
                    true);
            }
            catch
            {
                File.Copy(
                    _settingsFilePath,
                    _backupFilePath,
                    true);

                File.Delete(
                    _settingsFilePath);

                File.Move(
                    _tempFilePath,
                    _settingsFilePath);
            }
            finally
            {
                SafeDelete(_tempFilePath);
            }
        }

        private void TryRestoreBackup()
        {
            try
            {
                if (!File.Exists(_backupFilePath))
                    return;

                File.Copy(
                    _backupFilePath,
                    _settingsFilePath,
                    true);

                _logger.LogInformation(
                    "Ayar dosyası yedekten geri yüklendi.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Ayar dosyası yedekten geri yüklenemedi.",
                    ex);
            }
        }

        private void BackupCorruptedSettings()
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                    return;

                string corruptPath =
                    _settingsFilePath +
                    ".corrupt_" +
                    DateTime.Now.ToString(
                        "yyyyMMdd_HHmmss");

                File.Copy(
                    _settingsFilePath,
                    corruptPath,
                    true);

                _logger.LogWarning(
                    $"Bozuk ayar dosyası yedeklendi: {Path.GetFileName(corruptPath)}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Bozuk ayar dosyası yedeklenemedi: {ex.Message}");
            }
        }

        private static void SafeDelete(
            string path)
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
            }
        }
    }

    public class HotkeyJsonConverter :
        JsonConverter<Hotkey>
    {
        public override Hotkey Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType ==
                JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType !=
                JsonTokenType.String)
            {
                return null;
            }

            string value =
                reader.GetString();

            if (string.IsNullOrWhiteSpace(value))
                return null;

            return ParseHotkey(value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            Hotkey value,
            JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStringValue(
                FormatHotkey(value));
        }

        private static Hotkey ParseHotkey(
            string value)
        {
            try
            {
                string[] parts =
                    value.Split(
                        new[] { '+' },
                        StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 0)
                    return null;

                ModifierKeys modifiers =
                    ModifierKeys.None;

                Key key =
                    Key.None;

                for (int i = 0;
                     i < parts.Length;
                     i++)
                {
                    string part =
                        parts[i].Trim();

                    if (string.IsNullOrWhiteSpace(part))
                        continue;

                    if (IsControl(part))
                    {
                        modifiers |=
                            ModifierKeys.Control;

                        continue;
                    }

                    if (part.Equals(
                        "Shift",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        modifiers |=
                            ModifierKeys.Shift;

                        continue;
                    }

                    if (part.Equals(
                            "Alt",
                            StringComparison.OrdinalIgnoreCase) ||
                        part.Equals(
                            "Menu",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        modifiers |=
                            ModifierKeys.Alt;

                        continue;
                    }

                    if (IsWindows(part))
                    {
                        modifiers |=
                            ModifierKeys.Windows;

                        continue;
                    }

                    Key parsedKey;

                    if (Enum.TryParse(
                        part,
                        true,
                        out parsedKey))
                    {
                        key = parsedKey;
                    }
                }

                if (key == Key.None)
                    return null;

                return new Hotkey(
                    modifiers,
                    key);
            }
            catch
            {
                return null;
            }
        }

        private static string FormatHotkey(
            Hotkey hotkey)
        {
            if (hotkey == null)
                return null;

            return hotkey.ToString();
        }

        private static bool IsControl(
            string value)
        {
            return
                value.Equals(
                    "Ctrl",
                    StringComparison.OrdinalIgnoreCase) ||
                value.Equals(
                    "Control",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWindows(
            string value)
        {
            return
                value.Equals(
                    "Win",
                    StringComparison.OrdinalIgnoreCase) ||
                value.Equals(
                    "Windows",
                    StringComparison.OrdinalIgnoreCase) ||
                value.Equals(
                    "Meta",
                    StringComparison.OrdinalIgnoreCase);
        }
    }
}