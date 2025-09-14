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

                // Ayarları yükle ve temayı uygula
                InitializeTheme();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Uygulama başlatılırken hata: {ex.Message}", "Başlatma Hatası",
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
                // ServiceContainer'dan ayarları al
                var settingsManager = ServiceContainer.GetService<SettingsManager>();
                var appSettings = ServiceContainer.GetService<AppSettings>();

                // Kullanıcının tema tercihini al
                var selectedTheme = ThemeManager.GetThemeFromString(appSettings.Theme);

                // Temayı uygula
                ThemeManager.ChangeTheme(selectedTheme);
            }
            catch (Exception ex)
            {
                ThemeManager.ChangeTheme(ThemeManager.Theme.Light);
                MessageBox.Show($"Tema başlatma hatası: {ex.Message}\nVarsayılan tema kullanılıyor.",
                    "Tema Hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}