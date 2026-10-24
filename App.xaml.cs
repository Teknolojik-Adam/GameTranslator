using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace GameTranslatorUltimate
{
    public partial class App : Application
    {
        protected override void OnStartup(
            StartupEventArgs e)
        {
            base.OnStartup(e);

            RegisterUnhandledExceptionHandlers();

            try
            {
                ServiceContainer.Initialize();

                InitializeTheme();
                InitializeLanguage();
            }
            catch (Exception ex)
            {
                TryLogError(
                    "Uygulama başlatılırken hata oluştu.",
                    ex);

                MessageBox.Show(
                    string.Format(
                        GetLocalizedString("Str_AppStartError"),
                        ex.Message),
                    GetLocalizedString("Str_StartErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        protected override void OnExit(
            ExitEventArgs e)
        {
            try
            {
                ServiceContainer.Cleanup();
            }
            catch (Exception ex)
            {
                TryLogError(
                    "Servisler kapatılırken hata oluştu.",
                    ex);
            }
            finally
            {
                base.OnExit(e);
            }
        }

        private void RegisterUnhandledExceptionHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException +=
                CurrentDomain_UnhandledException;

            DispatcherUnhandledException +=
                App_DispatcherUnhandledException;
        }

        private void CurrentDomain_UnhandledException(
            object sender,
            UnhandledExceptionEventArgs e)
        {
            Exception ex =
                e.ExceptionObject as Exception;

            if (ex != null)
            {
                TryLogError(
                    "Yakalanmamış kritik uygulama hatası.",
                    ex);

                try
                {
                    MessageBox.Show(
                        $"Kritik hata: {ex.Message}",
                        "Hata",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch
                {
                }
            }
            else
            {
                TryLogError(
                    "Yakalanmamış bilinmeyen uygulama hatası.",
                    null);
            }
        }

        private void App_DispatcherUnhandledException(
            object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            TryLogError(
                "UI thread üzerinde yakalanmamış hata oluştu.",
                e.Exception);

            try
            {
                MessageBox.Show(
                    $"Hata: {e.Exception.Message}",
                    "Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
            }

            e.Handled =
                true;
        }

        private void InitializeTheme()
        {
            try
            {
                ThemeManager.LoadThemeSettings();

                AppSettings appSettings =
                    ServiceContainer.GetService<AppSettings>();

                if (appSettings == null ||
                    string.IsNullOrWhiteSpace(
                        appSettings.Theme))
                {
                    return;
                }

                var theme =
                    ThemeManager.GetThemeFromString(
                        appSettings.Theme);

                ThemeManager.ChangeTheme(
                    theme);
            }
            catch (Exception ex)
            {
                TryLogError(
                    "Tema başlatılırken hata oluştu.",
                    ex);

                try
                {
                    MessageBox.Show(
                        string.Format(
                            GetLocalizedString("Str_ThemeInitError"),
                            ex.Message),
                        GetLocalizedString("Str_ThemeErrorTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch
                {
                }
            }
        }

        private void InitializeLanguage()
        {
            try
            {
                AppSettings appSettings =
                    ServiceContainer.GetService<AppSettings>();

                string language =
                    appSettings != null
                        ? appSettings.Language
                        : null;

                ChangeLanguage(
                    string.IsNullOrWhiteSpace(language)
                        ? "tr"
                        : language);
            }
            catch (Exception ex)
            {
                TryLogError(
                    "Uygulama dili başlatılırken hata oluştu.",
                    ex);

                ChangeLanguage(
                    "tr");
            }
        }

        public static string GetLocalizedString(
            string key)
        {
            if (string.IsNullOrWhiteSpace(
                key))
            {
                return string.Empty;
            }

            Application app =
                Current;

            if (app == null)
            {
                return key;
            }

            object value =
                app.TryFindResource(
                    key);

            return value as string ??
                   key;
        }

        public static void ChangeLanguage(
            string cultureCode)
        {
            Application app =
                Current;

            if (app == null)
                return;

            string normalized =
                NormalizeUiLanguage(
                    cultureCode);

            string resourcePath;

            switch (normalized)
            {
                case "en":
                    resourcePath =
                        "Resources/StringResources.en.xaml";
                    break;

                case "tr":
                default:
                    resourcePath =
                        "Resources/StringResources.tr.xaml";
                    break;
            }

            var newDictionary =
                new ResourceDictionary
                {
                    Source =
                        new Uri(
                            resourcePath,
                            UriKind.Relative)
                };

            List<ResourceDictionary> oldDictionaries =
                app.Resources
                    .MergedDictionaries
                    .Where(
                        IsLanguageResourceDictionary)
                    .ToList();

            for (int i = 0;
                 i < oldDictionaries.Count;
                 i++)
            {
                app.Resources
                    .MergedDictionaries
                    .Remove(
                        oldDictionaries[i]);
            }

            app.Resources
                .MergedDictionaries
                .Add(
                    newDictionary);
        }

        private static bool IsLanguageResourceDictionary(
            ResourceDictionary dictionary)
        {
            if (dictionary == null ||
                dictionary.Source == null)
            {
                return false;
            }

            string source =
                dictionary.Source
                    .OriginalString;

            if (string.IsNullOrWhiteSpace(
                source))
            {
                return false;
            }

            return source.StartsWith(
                "Resources/StringResources.",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeUiLanguage(
            string cultureCode)
        {
            if (string.IsNullOrWhiteSpace(
                cultureCode))
            {
                return "tr";
            }

            string normalized =
                cultureCode
                    .Trim()
                    .Replace(
                        '_',
                        '-')
                    .ToLowerInvariant();

            if (normalized.StartsWith(
                "en",
                StringComparison.Ordinal))
            {
                return "en";
            }

            if (normalized.StartsWith(
                "tr",
                StringComparison.Ordinal))
            {
                return "tr";
            }

            return "tr";
        }

        private static void TryLogError(
            string message,
            Exception ex)
        {
            try
            {
                ILogger logger =
                    ServiceContainer.GetService<ILogger>();

                if (logger != null)
                {
                    logger.LogError(
                        message,
                        ex);
                }
            }
            catch
            {
            }
        }

        private void LogStartupDiagnostics(
            ILogger logger)
        {
            try
            {
                if (logger == null)
                    return;

                var sb =
                    new StringBuilder();

                Action<string> log =
                    line =>
                    {
                        try
                        {
                            logger.LogInformation(
                                line);
                        }
                        catch
                        {
                        }

                        try
                        {
                            sb.AppendLine(
                                line);
                        }
                        catch
                        {
                        }
                    };

                log(
                    "== Startup diagnostic report ==");

                try
                {
                    log(
                        $"OS: {Environment.OSVersion}");
                }
                catch
                {
                }

                try
                {
                    log(
                        $"OS Description: {RuntimeInformation.OSDescription}");
                }
                catch
                {
                }

                log(
                    $"Process bitness: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");

                bool isElevated =
                    false;

                try
                {
                    using (WindowsIdentity identity =
                           WindowsIdentity.GetCurrent())
                    {
                        var principal =
                            new WindowsPrincipal(
                                identity);

                        isElevated =
                            principal.IsInRole(
                                WindowsBuiltInRole.Administrator);
                    }
                }
                catch
                {
                }

                log(
                    $"Is elevated (admin): {isElevated}");

                string entryPath =
                    null;

                try
                {
                    Assembly entryAssembly =
                        Assembly.GetEntryAssembly();

                    if (entryAssembly != null)
                    {
                        entryPath =
                            entryAssembly.Location;
                    }
                }
                catch
                {
                }

                if (string.IsNullOrWhiteSpace(
                    entryPath))
                {
                    try
                    {
                        using (Process process =
                               Process.GetCurrentProcess())
                        {
                            if (process.MainModule != null)
                            {
                                entryPath =
                                    process.MainModule.FileName;
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                if (string.IsNullOrWhiteSpace(
                    entryPath))
                {
                    entryPath =
                        "(unknown)";
                }

                log(
                    $"Entry assembly path: {entryPath}");

                log(
                    $"AppDomain.BaseDirectory: {AppDomain.CurrentDomain.BaseDirectory}");

                log(
                    $"Environment.CurrentDirectory: {Environment.CurrentDirectory}");

                string baseDir =
                    AppDomain.CurrentDomain.BaseDirectory;

                if (string.IsNullOrWhiteSpace(
                    baseDir))
                {
                    baseDir =
                        Environment.CurrentDirectory;
                }

                string entryDirectory =
                    Path.GetDirectoryName(
                        entryPath);

                if (string.IsNullOrWhiteSpace(
                    entryDirectory))
                {
                    entryDirectory =
                        baseDir;
                }

                IEnumerable<string> candidates =
                    new List<string>
                    {
                        Path.Combine(
                            baseDir,
                            "tessdata"),

                        Path.Combine(
                            Environment.CurrentDirectory,
                            "tessdata"),

                        Path.Combine(
                            entryDirectory,
                            "tessdata")
                    }
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase);

                foreach (string path
                         in candidates)
                {
                    if (!Directory.Exists(
                        path))
                    {
                        log(
                            $"tessdata not found at: {path}");

                        continue;
                    }

                    string[] files =
                        Directory.GetFiles(
                            path);

                    log(
                        $"tessdata found at: {path} (files: {files.Length})");

                    foreach (string file
                             in files.Take(10))
                    {
                        log(
                            $"  - {Path.GetFileName(file)}");
                    }

                    string eng =
                        files.FirstOrDefault(
                            file =>
                                string.Equals(
                                    Path.GetFileName(file),
                                    "eng.traineddata",
                                    StringComparison.OrdinalIgnoreCase));

                    if (eng == null)
                        continue;

                    try
                    {
                        using (FileStream stream =
                               File.Open(
                                   eng,
                                   FileMode.Open,
                                   FileAccess.Read,
                                   FileShare.Read))
                        {
                            log(
                                $"eng.traineddata is readable, length={stream.Length}");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            $"eng.traineddata exists but cannot be opened: {ex.Message}",
                            ex);

                        sb.AppendLine(
                            $"eng.traineddata exists but cannot be opened: {ex.Message}");
                    }
                }

                try
                {
                    string testPath =
                        Path.Combine(
                            baseDir,
                            "gt_diag_write_test.tmp");

                    File.WriteAllText(
                        testPath,
                        "ok");

                    File.Delete(
                        testPath);

                    log(
                        "BaseDirectory write test: OK");
                }
                catch (Exception ex)
                {
                    log(
                        $"BaseDirectory yazma izni yok veya hata: {ex.Message}");
                }

                log(
                    "== End of diagnostic report ==");

                try
                {
                    string summary =
                        sb.ToString();

                    if (string.IsNullOrWhiteSpace(
                        summary))
                    {
                        summary =
                            "No diagnostic information available.";
                    }

                    MessageBox.Show(
                        summary,
                        "Startup diagnostic",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch
                {
                }
            }
            catch (Exception ex)
            {
                try
                {
                    if (logger != null)
                    {
                        logger.LogError(
                            "Startup diagnostic failed.",
                            ex);
                    }
                }
                catch
                {
                }
            }
        }
    }
}