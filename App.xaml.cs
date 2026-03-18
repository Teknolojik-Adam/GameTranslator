using System;
using System.Windows;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Diagnostics;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace GameTranslatorUltimate
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

                // Diagnostic bilgisi logla (logger varsa)
                // Başlangıçtaki tanılama popup'ını devre dışı bırakmak için diagnostik çağrısı kaldırıldı.
                try
                {
                    // var logger = ServiceContainer.GetService<ILogger>();
                    // LogStartupDiagnostics(logger);
                }
                catch { }

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

        // Startup diagnostic method - logs environment, tessdata checks etc.
        private void LogStartupDiagnostics(ILogger logger)
        {
            try
            {
                if (logger == null) return;

                var sb = new StringBuilder();
                void Log(string line)
                {
                    try { logger.LogInformation(line); } catch { }
                    try { sb.AppendLine(line); } catch { }
                }

                Log("== Startup diagnostic report ==");
                try { Log($"OS: {Environment.OSVersion}"); } catch { }
                try { Log($"OS Description: {RuntimeInformation.OSDescription}"); } catch { }
                Log($"Process bitness: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");

                bool isElevated = false;
                try
                {
                    isElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { }
                Log($"Is elevated (admin): {isElevated}");

                string entryPath = Assembly.GetEntryAssembly()?.Location ?? Process.GetCurrentProcess().MainModule?.FileName ?? "(unknown)";
                Log($"Entry assembly path: {entryPath}");
                Log($"AppDomain.BaseDirectory: {AppDomain.CurrentDomain.BaseDirectory}");
                Log($"Environment.CurrentDirectory: {Environment.CurrentDirectory}");

                // Tessdata checks
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var candidates = new List<string>
                {
                    Path.Combine(baseDir, "tessdata"),
                    Path.Combine(Environment.CurrentDirectory, "tessdata"),
                    Path.Combine(Path.GetDirectoryName(entryPath) ?? baseDir, "tessdata")
                }.Distinct();

                foreach (var p in candidates)
                {
                    if (Directory.Exists(p))
                    {
                        var files = Directory.GetFiles(p);
                        Log($"tessdata found at: {p} (files: {files.Length})");
                        foreach (var f in files.Take(10)) Log($"  - {Path.GetFileName(f)}");

                        var eng = files.FirstOrDefault(x => Path.GetFileName(x).Equals("eng.traineddata", StringComparison.OrdinalIgnoreCase));
                        if (eng != null)
                        {
                            try
                            {
                                using (var fs = File.Open(eng, FileMode.Open, FileAccess.Read))
                                {
                                    Log($"eng.traineddata is readable, length={fs.Length}");
                                }
                            }
                            catch (Exception ex)
                            {
                                try { logger.LogError($"eng.traineddata exists but cannot be opened: {ex.Message}", ex); } catch { }
                                sb.AppendLine($"eng.traineddata exists but cannot be opened: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        Log($"tessdata not found at: {p}");
                    }
                }

                try
                {
                    var testPath = Path.Combine(baseDir, "gt_diag_write_test.tmp");
                    File.WriteAllText(testPath, "ok");
                    File.Delete(testPath);
                    Log("BaseDirectory write test: OK");
                }
                catch (Exception ex)
                {
                    Log($"BaseDirectory yazma izni yok veya hata: {ex.Message}");
                }

                Log("== End of diagnostic report ==");

                // Show summary to user as MessageBox
                try
                {
                    var summary = sb.ToString();
                    if (string.IsNullOrWhiteSpace(summary)) summary = "No diagnostic information available.";
                    MessageBox.Show(summary, "Startup diagnostic", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch { }
            }
            catch (Exception ex)
            {
                try { logger?.LogError("Startup diagnostic failed", ex); } catch { }
            }
        }
    }
}
