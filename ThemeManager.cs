using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameTranslatorUltimate
{
    public static class ThemeManager
    {
        public enum Theme
        {
            Light,
            Dark
        }

        private const string LightThemeUri = "Themes/LightTheme.xaml";
        private const string DarkThemeUri = "Themes/DarkTheme.xaml";
        private const string ThemeSettingsFileName = "theme_settings.json";

        private static readonly object SyncRoot = new object();

        private static Theme _currentTheme = Theme.Dark;

        public static Theme CurrentTheme => _currentTheme;

        private static string ThemeSettingsPath =>
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                ThemeSettingsFileName);

        public static event EventHandler ThemeChanged;

        public static void ChangeTheme(Theme theme)
        {
            ApplyTheme(theme, true);
        }

        public static Theme GetThemeFromString(string themeString)
        {
            if (Enum.TryParse(themeString, true, out Theme result))
                return result;

            return Theme.Dark;
        }

        public static string GetStringFromTheme(Theme theme)
        {
            return theme.ToString();
        }

        public static void LoadThemeSettings()
        {
            Theme theme = Theme.Dark;

            try
            {
                if (TryLoadThemeFile(out Theme fileTheme))
                {
                    theme = fileTheme;
                }
                else if (TryLoadThemeFromAppSettings(out Theme settingsTheme))
                {
                    theme = settingsTheme;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Tema ayarları yüklenirken hata oluştu: {ex.Message}");

                theme = Theme.Dark;
            }

            ApplyTheme(theme, false);
        }

        public static void SaveThemeSettings(Theme theme)
        {
            try
            {
                string json =
                    JsonSerializer.Serialize(theme);

                File.WriteAllText(
                    ThemeSettingsPath,
                    json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Tema dosyası kaydedilemedi: {ex.Message}");
            }

            try
            {
                var appSettings =
                    ServiceContainer.GetService<AppSettings>();

                var settingsManager =
                    ServiceContainer.GetService<SettingsManager>();

                if (appSettings == null ||
                    settingsManager == null)
                {
                    return;
                }

                appSettings.Theme =
                    GetStringFromTheme(theme);

                settingsManager.SaveSettings(appSettings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Tema AppSettings'e kaydedilemedi: {ex.Message}");
            }
        }

        public static void ApplyThemeToWindow(Window window)
        {
            if (window == null)
                return;

            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.Invoke(
                    () => ApplyThemeToWindow(window));

                return;
            }

            try
            {
                if (Application.Current?.TryFindResource("ThemedWindow")
                    is Style windowStyle)
                {
                    window.Style = windowStyle;
                }

                if (Application.Current?.TryFindResource("PrimaryBackgroundBrush")
                    is Brush backgroundBrush)
                {
                    window.Background = backgroundBrush;
                }

                RefreshControlThemes(window);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Pencereye tema uygulanamadı: {ex.Message}");
            }
        }

        private static void ApplyTheme(
            Theme theme,
            bool save)
        {
            Application app =
                Application.Current;

            if (app == null)
                return;

            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.Invoke(
                    () => ApplyTheme(theme, save));

                return;
            }

            lock (SyncRoot)
            {
                try
                {
                    ReplaceThemeDictionary(theme);

                    _currentTheme = theme;

                    ApplyThemeToWindows();

                    if (save)
                    {
                        SaveThemeSettings(theme);
                    }

                    ThemeChanged?.Invoke(
                        null,
                        EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"Tema değiştirilemedi: {ex.Message}");

                    if (theme != Theme.Dark)
                    {
                        TryApplyDarkFallback();
                    }
                }
            }
        }

        private static void ReplaceThemeDictionary(
            Theme theme)
        {
            var dictionaries =
                Application.Current.Resources.MergedDictionaries;

            for (int i = dictionaries.Count - 1;
                 i >= 0;
                 i--)
            {
                ResourceDictionary dictionary =
                    dictionaries[i];

                if (IsThemeDictionary(dictionary))
                {
                    dictionaries.RemoveAt(i);
                }
            }

            string source =
                theme == Theme.Dark
                    ? DarkThemeUri
                    : LightThemeUri;

            dictionaries.Add(
                new ResourceDictionary
                {
                    Source =
                        new Uri(
                            source,
                            UriKind.Relative)
                });
        }

        private static bool IsThemeDictionary(
            ResourceDictionary dictionary)
        {
            string source =
                dictionary?.Source?.OriginalString;

            if (string.IsNullOrWhiteSpace(source))
                return false;

            return source.EndsWith(
                       "LightTheme.xaml",
                       StringComparison.OrdinalIgnoreCase) ||
                   source.EndsWith(
                       "DarkTheme.xaml",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyThemeToWindows()
        {
            if (Application.Current == null)
                return;

            foreach (Window window in
                     Application.Current.Windows)
            {
                ApplyThemeToWindow(window);
            }
        }

        private static void RefreshControlThemes(
            DependencyObject parent)
        {
            if (parent == null)
                return;

            int childCount =
                VisualTreeHelper.GetChildrenCount(parent);

            for (int i = 0;
                 i < childCount;
                 i++)
            {
                DependencyObject child =
                    VisualTreeHelper.GetChild(parent, i);

                ApplyControlTheme(child);
                RefreshControlThemes(child);
            }
        }

        private static void ApplyControlTheme(
            DependencyObject control)
        {
            if (!(control is FrameworkElement element))
                return;

            if (element.ReadLocalValue(
                    FrameworkElement.StyleProperty) !=
                DependencyProperty.UnsetValue)
            {
                return;
            }

            string styleKey =
                GetStyleKey(control);

            if (styleKey == null)
                return;

            if (Application.Current?.TryFindResource(styleKey)
                is Style style)
            {
                element.Style = style;
            }
        }

        private static string GetStyleKey(
            DependencyObject control)
        {
            if (control is Button)
                return "ThemedButton";

            if (control is TextBox)
                return "ThemedTextBox";

            if (control is ComboBox)
                return "ThemedComboBox";

            if (control is GroupBox)
                return "ThemedGroupBox";

            if (control is Label)
                return "ThemedLabel";

            if (control is ListBox)
                return "ThemedListBox";

            if (control is ListView)
                return "ThemedListView";

            if (control is CheckBox)
                return "ThemedCheckBox";

            if (control is RadioButton)
                return "ThemedRadioButton";

            if (control is Expander)
                return "ThemedExpander";

            if (control is TextBlock)
                return "ThemedTextBlock";

            return null;
        }

        private static bool TryLoadThemeFile(
            out Theme theme)
        {
            theme = Theme.Dark;

            if (!File.Exists(ThemeSettingsPath))
                return false;

            try
            {
                string json =
                    File.ReadAllText(
                        ThemeSettingsPath);

                if (string.IsNullOrWhiteSpace(json))
                    return false;

                theme =
                    JsonSerializer.Deserialize<Theme>(json);

                return Enum.IsDefined(
                    typeof(Theme),
                    theme);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryLoadThemeFromAppSettings(
            out Theme theme)
        {
            theme = Theme.Dark;

            try
            {
                var appSettings =
                    ServiceContainer.GetService<AppSettings>();

                if (appSettings == null ||
                    string.IsNullOrWhiteSpace(appSettings.Theme))
                {
                    return false;
                }

                theme =
                    GetThemeFromString(
                        appSettings.Theme);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryApplyDarkFallback()
        {
            try
            {
                ReplaceThemeDictionary(
                    Theme.Dark);

                _currentTheme =
                    Theme.Dark;

                ApplyThemeToWindows();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Dark tema fallback uygulanamadı: {ex.Message}");
            }
        }
    }
}