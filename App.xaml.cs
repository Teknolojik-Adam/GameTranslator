using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace P5S_ceviri
{
    /// <summary>
    /// App.xaml etkileşim mantığı
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Set up global exception handling
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                Exception ex = (Exception)args.ExceptionObject;
                MessageBox.Show($"An unhandled exception occurred: {ex.Message}",
                              "Error",
                              MessageBoxButton.OK,
                              MessageBoxImage.Error);
            };

            // Handle UI thread exceptions
            Current.DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show($"An error occurred: {args.Exception.Message}",
                              "Error",
                              MessageBoxButton.OK,
                              MessageBoxImage.Error);
                args.Handled = true;
            };
        }
    }
}