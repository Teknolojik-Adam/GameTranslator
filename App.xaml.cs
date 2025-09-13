using System;
using System.Windows;
namespace P5S_ceviri
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            InitializeTheme();

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

        private void InitializeTheme()
        {
            try
            {
                var tempLogger = new ConsoleLogger();
                var settingsManager = new SettingsManager(tempLogger);
                var appSettings = settingsManager.LoadSettings();

                var selectedTheme = ThemeManager.GetThemeFromString(appSettings.Theme);
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