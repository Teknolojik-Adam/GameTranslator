using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace P5S_ceviri
{
    public static class ThemeManager
    {
        public enum Theme
        {
            Light,
            Dark
        }

        private const string LIGHT_THEME_URI = "Themes/LightTheme.xaml";
        private const string DARK_THEME_URI = "Themes/DarkTheme.xaml";
        private const string THEME_SETTINGS_PATH = "theme_settings.json";

        public static void ChangeTheme(Theme theme)
        {
            try
            {
                // Mevcut tema kaynaklarını temizle
                ClearThemeResources();

                // Yeni tema kaynaklarını yükle
                string themeUri = theme == Theme.Dark ? DARK_THEME_URI : LIGHT_THEME_URI;
                var themeResource = new ResourceDictionary()
                {
                    Source = new Uri(themeUri, UriKind.Relative)
                };

                Application.Current.Resources.MergedDictionaries.Add(themeResource);

                // Tüm pencerelere yeni temayı uygula
                ApplyThemeToWindows();

                // Temayı kaydet
                SaveThemeSettings(theme);
            }
            catch (Exception ex)
            {
                // Hata durumunda varsayılan temaya geri dön
                MessageBox.Show($"Tema değiştirme sırasında hata oluştu: {ex.Message}", "Tema Hatası",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public static Theme GetThemeFromString(string themeString)
        {
            if (Enum.TryParse<Theme>(themeString, true, out Theme result))
            {
                return result;
            }
            return Theme.Light;
        }

        public static string GetStringFromTheme(Theme theme)
        {
            return theme.ToString();
        }

        private static void ClearThemeResources()
        {
            for (int i = Application.Current.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                var dictionary = Application.Current.Resources.MergedDictionaries[i];
                if (dictionary.Source != null &&
                    (dictionary.Source.ToString().Contains("LightTheme.xaml") ||
                     dictionary.Source.ToString().Contains("DarkTheme.xaml")))
                {
                    Application.Current.Resources.MergedDictionaries.RemoveAt(i);
                }
            }
        }

        private static void ApplyThemeToWindows()
        {
            foreach (Window window in Application.Current.Windows)
            {
                ApplyThemeToWindow(window);
            }
        }

        public static void ApplyThemeToWindow(Window window)
        {
            if (window == null) return;

            try
            {
                // Window stilini uygula
                if (Application.Current.Resources["ThemedWindow"] is Style windowStyle)
                {
                    window.Style = windowStyle;
                }

                // Window'un arka plan rengini doğrudan ayarla
                if (Application.Current.Resources["PrimaryBackgroundBrush"] is SolidColorBrush windowBackground)
                {
                    window.Background = windowBackground;
                }

                // Alt kontrollerin temalarını güncelle
                RefreshControlThemes(window);
            }
            catch (Exception ex)
            {
                // Hata durumu
                System.Diagnostics.Debug.WriteLine($"Pencereye tema uygulama hatası: {ex.Message}");
            }
        }

        private static void RefreshControlThemes(DependencyObject parent)
        {
            if (parent == null) return;

            int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);

                // Kontrol tipine göre ilgili stili uygula
                ApplyControlTheme(child);

                // Alt kontrolleri işle
                RefreshControlThemes(child);
            }
        }

        private static void ApplyControlTheme(DependencyObject control)
        {
            string styleKey = null;
            string typeName = control.GetType().Name;

            switch (typeName)
            {
                case "Button":
                    styleKey = "ThemedButton";
                    break;
                case "TextBox":
                    styleKey = "ThemedTextBox";
                    break;
                case "ComboBox":
                    styleKey = "ThemedComboBox";
                    break;
                case "GroupBox":
                    styleKey = "ThemedGroupBox";
                    break;
                case "Label":
                    styleKey = "ThemedLabel";
                    break;
                case "ListBox":
                    styleKey = "ThemedListBox";
                    break;
                case "CheckBox":
                    styleKey = "ThemedCheckBox";
                    break;
                case "RadioButton":
                    styleKey = "ThemedRadioButton";
                    break;
                case "TextBlock":
                    styleKey = "ThemedTextBlock";
                    break;
            }

            if (!string.IsNullOrEmpty(styleKey) &&
                Application.Current.Resources[styleKey] is Style style &&
                control is FrameworkElement element)
            {
                // Stili uygula
                element.Style = style;
            }
        }

        public static void LoadThemeSettings()
        {
            try
            {
                // Önce dosyadan tema ayarlarını yüklemeyi dene
                if (File.Exists(THEME_SETTINGS_PATH))
                {
                    string json = File.ReadAllText(THEME_SETTINGS_PATH);
                    var theme = JsonSerializer.Deserialize<Theme>(json);
                    ChangeTheme(theme);
                    return;
                }

                // Dosya yoksa AppSettings'ten tema bilgisini al
                try
                {
                    var appSettings = ServiceContainer.GetService<AppSettings>();
                    if (appSettings != null)
                    {
                        var selectedTheme = GetThemeFromString(appSettings.Theme);
                        ChangeTheme(selectedTheme);
                        return;
                    }
                }
                catch
                {
                  
                }

              
                ChangeTheme(Theme.Light);
            }
            catch (Exception ex)
            {
                // Hata durumunda varsayılan temaya geri dön
                ChangeTheme(Theme.Light);
                MessageBox.Show($"Tema ayarları yüklenirken hata oluştu: {ex.Message}", "Tema Hatası",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public static void SaveThemeSettings(Theme theme)
        {
            try
            {
                // Dosyaya kaydet
                string json = JsonSerializer.Serialize(theme);
                File.WriteAllText(THEME_SETTINGS_PATH, json);

                // AppSettings'e de kaydet (eğer mevcut ise)
                try
                {
                    var appSettings = ServiceContainer.GetService<AppSettings>();
                    var settingsManager = ServiceContainer.GetService<SettingsManager>();
                    if (appSettings != null && settingsManager != null)
                    {
                        appSettings.Theme = GetStringFromTheme(theme);
                        settingsManager.SaveSettings(appSettings);
                    }
                }
                catch
                {
                   
                }
            }
            catch (Exception ex)
            {
                // Hata durumunda uyarı
                MessageBox.Show($"Tema ayarları kaydedilirken hata oluştu: {ex.Message}", "Tema Hatası",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}