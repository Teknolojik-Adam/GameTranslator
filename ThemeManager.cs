using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GameTranslatorUltimate
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
                // Mevcut tema kaynaklarÄ±nÄ± temizle
                ClearThemeResources();

                // Yeni tema kaynaklarÄ±nÄ± yÃ¼kle
                string themeUri = theme == Theme.Dark ? DARK_THEME_URI : LIGHT_THEME_URI;
                var themeResource = new ResourceDictionary()
                {
                    Source = new Uri(themeUri, UriKind.Relative)
                };

                Application.Current.Resources.MergedDictionaries.Add(themeResource);

                // TÃ¼m pencerelere yeni temayÄ± uygula
                ApplyThemeToWindows();

                // TemayÄ± kaydet
                SaveThemeSettings(theme);
            }
            catch (Exception ex)
            {
                // Hata durumunda varsayÄ±lan temaya geri dÃ¶nmek iÃ§in
                MessageBox.Show($"Tema deÄŸiÅŸtirme sÄ±rasÄ±nda hata oluÅŸtu: {ex.Message}", "Tema HatasÄ±",
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

                // Window'un arka plan rengini doÄŸrudan ayarla
                if (Application.Current.Resources["PrimaryBackgroundBrush"] is SolidColorBrush windowBackground)
                {
                    window.Background = windowBackground;
                }

                // Alt kontrollerin temalarÄ±nÄ± gÃ¼ncelle
                RefreshControlThemes(window);
            }
            catch (Exception ex)
            {
                // Hata durumu
                System.Diagnostics.Debug.WriteLine($"Pencereye tema uygulama hatasÄ±: {ex.Message}");
            }
        }

        private static void RefreshControlThemes(DependencyObject parent)
        {
            if (parent == null) return;

            int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);

                // Kontrol tipine gÃ¶re ilgili stili uygula
                ApplyControlTheme(child);

                // Alt kontrolleri iÅŸle
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
                // Ã–nce dosyadan tema ayarlarÄ±nÄ± yÃ¼klemeyi dene
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
                // Hata durumunda varsayÄ±lan temaya geri dÃ¶n
                ChangeTheme(Theme.Light);
                MessageBox.Show($"Tema ayarlarÄ± yÃ¼klenirken hata oluÅŸtu: {ex.Message}", "Tema HatasÄ±",
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

                // AppSettings'e de kaydet (eÄŸer mevcut ise)
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
                // Hata durumunda uyarÄ±
                MessageBox.Show($"Tema ayarlarÄ± kaydedilirken hata oluÅŸtu: {ex.Message}", "Tema HatasÄ±",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
