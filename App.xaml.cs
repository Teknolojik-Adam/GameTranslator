using System;
using System.Windows;

namespace P5S_ceviri
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                // Servisleri başlat
                ServiceContainer.Initialize();

                // Ayarları yükle ve temayı uygulamak için
                InitializeTheme();
                InitializeLanguage();
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(GetLocalizedString("Str_AppStartError"), ex.Message), GetLocalizedString("Str_StartErrorTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                Exception ex = (Exception)args.ExceptionObject;
                MessageBox.Show($"Kritik hata: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            Current.DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show($"Hata: {args.Exception.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Servisleri temizle
            ServiceContainer.Cleanup();
            base.OnExit(e);
        }

        private void InitializeTheme()
        {
            try
            {
                // ThemeManager'ı kullanarak tema ayarlarını yükle
                ThemeManager.LoadThemeSettings();

                //AppSettings'den tema bilgisini al
                try
                {
                    var appSettings = ServiceContainer.GetService<AppSettings>();
                    if (appSettings != null && !string.IsNullOrEmpty(appSettings.Theme))
                    {
                        var theme = ThemeManager.GetThemeFromString(appSettings.Theme);
                        ThemeManager.ChangeTheme(theme);
                    }
                }
                catch
                {
                    // Eğer AppSettings yüklenemezse, varsayılan tema yüklenir
                    ThemeManager.LoadThemeSettings();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(GetLocalizedString("Str_ThemeInitError"), ex.Message),
                    GetLocalizedString("Str_ThemeErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public static string GetLocalizedString(string key)
        {
            return Application.Current.TryFindResource(key) as string ?? key;
        }

        public static void ChangeLanguage(string cultureCode)
        {
            var dict = new ResourceDictionary();
            switch (cultureCode)
            {
                case "en":
                    dict.Source = new Uri("Resources/StringResources.en.xaml", UriKind.Relative);
                    break;
                case "tr":
                default:
                    dict.Source = new Uri("Resources/StringResources.tr.xaml", UriKind.Relative);
                    break;
            }

            // Remove the old resource dictionary
            ResourceDictionary oldDict = null;
            foreach (ResourceDictionary d in Application.Current.Resources.MergedDictionaries)
            {
                if (d.Source != null && d.Source.OriginalString.StartsWith("Resources/StringResources"))
                {
                    oldDict = d;
                    break;
                }
            }

            if (oldDict != null)
            {
                Application.Current.Resources.MergedDictionaries.Remove(oldDict);
            }

            Application.Current.Resources.MergedDictionaries.Add(dict);
        }

        private void InitializeLanguage()
        {
            try
            {
                var appSettings = ServiceContainer.GetService<AppSettings>();
                if (appSettings != null && !string.IsNullOrEmpty(appSettings.Language))
                {
                    ChangeLanguage(appSettings.Language);
                }
                else
                {
                    ChangeLanguage("tr");
                }
            }
            catch
            {
                ChangeLanguage("tr");
            }
        }
    }
}