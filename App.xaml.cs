using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
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
                MessageBox.Show($"An unhandled exception occurred: {ex.Message}",
                              "Error",
                              MessageBoxButton.OK,
                              MessageBoxImage.Error);
            };

            
            Current.DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show($"An error occurred: {args.Exception.Message}",
                              "Error",
                              MessageBoxButton.OK,
                              MessageBoxImage.Error);
                args.Handled = true;
            };
        }

        private void InitializeTheme()
        {
            try
            {
                // Geçici bir logger
                var tempLogger = new ConsoleLogger();
                var settingsManager = new SettingsManager(tempLogger);
                var appSettings = settingsManager.LoadSettings();

                // Kullanıcının tema tercihini al
                var selectedTheme = ThemeManager.GetThemeFromString(appSettings.Theme);

                // Temayı uygula
                ThemeManager.ChangeTheme(selectedTheme);
            }
            catch (Exception ex)
            {
                // Hata durumunda varsayılan tema kullan
                ThemeManager.ChangeTheme(ThemeManager.Theme.Light);
                System.Diagnostics.Debug.WriteLine($"Tema başlatma hatası: {ex.Message}");
            }
        }
    }
}