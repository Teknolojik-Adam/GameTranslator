using System;
using System.Windows;

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

                // Tema kaynaklarını uygulamaya ekle
                Application.Current.Resources.MergedDictionaries.Add(themeResource);

                // Tüm pencerelere yeni temayı uygula
                ApplyThemeToWindows();
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
            return Theme.Light; // Varsayılan tema
        }


        public static string GetStringFromTheme(Theme theme)
        {
            return theme.ToString();
        }

        private static void ClearThemeResources()
        {
            // Tema kaynak sözlüklerini bul ve kaldır
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
                // Window'un temalı stilini uygula
                if (Application.Current.Resources["ThemedWindow"] is Style windowStyle)
                {
                    window.Style = windowStyle;
                }

                // Alt kontrollerin temalarını güncelle
                RefreshControlThemes(window);
            }
            catch (Exception ex)
            {
                // Hata durumunda sessizce devam et
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
            }

            if (!string.IsNullOrEmpty(styleKey) && 
                Application.Current.Resources[styleKey] is Style style &&
                control is FrameworkElement element)
            {
                element.Style = style;
            }
        }
    }
} 