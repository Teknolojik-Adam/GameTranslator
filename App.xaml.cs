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
                MessageBox.Show($"Tema başlatma hatası: {ex.Message}",
                    "Tema Hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}