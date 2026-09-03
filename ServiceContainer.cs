using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http;
using System.Threading;

namespace GameTranslatorUltimate
{
    public static class ServiceContainer
    {
        private static readonly object SyncRoot = new object();
        private static ServiceProvider _serviceProvider;

        public static void Initialize()
        {
            lock (SyncRoot)
            {
                if (_serviceProvider != null)
                    return;

                ServiceProvider provider = null;

                try
                {
                    var services = new ServiceCollection();

                    RegisterCoreServices(services);
                    RegisterHttpServices(services);
                    RegisterTranslationServices(services);
                    RegisterOcrServices(services);
                    RegisterVideoServices(services);
                    RegisterMemoryServices(services);
                    RegisterUtilityServices(services);

                    provider = services.BuildServiceProvider();

                    provider.GetRequiredService<SettingsAutoSaveTimer>();

                    _serviceProvider = provider;
                }
                catch
                {
                    provider?.Dispose();
                    throw;
                }
            }
        }

        public static T GetService<T>() where T : class
        {
            lock (SyncRoot)
            {
                if (_serviceProvider == null)
                {
                    throw new InvalidOperationException(
                        "ServiceContainer.Initialize() has not been called.");
                }

                return _serviceProvider.GetRequiredService<T>();
            }
        }

        public static T TryGetService<T>() where T : class
        {
            lock (SyncRoot)
            {
                if (_serviceProvider == null)
                    return null;

                return _serviceProvider.GetService<T>();
            }
        }

        public static void Cleanup()
        {
            ServiceProvider provider;

            lock (SyncRoot)
            {
                provider = _serviceProvider;
                _serviceProvider = null;
            }

            if (provider == null)
                return;

            try
            {
                provider.Dispose();
            }
            catch
            {
            }
        }

        private static void RegisterCoreServices(
            IServiceCollection services)
        {
            services.AddSingleton<ILogger, ConsoleLogger>();

            services.AddSingleton<SettingsManager>(sp =>
                new SettingsManager(
                    sp.GetRequiredService<ILogger>()));

            services.AddSingleton<AppSettings>(sp =>
                sp.GetRequiredService<SettingsManager>()
                    .LoadSettings());

            services.AddSingleton<SettingsAutoSaveTimer>(sp =>
                new SettingsAutoSaveTimer(
                    sp.GetRequiredService<SettingsManager>(),
                    sp.GetRequiredService<AppSettings>(),
                    sp.GetRequiredService<ILogger>()));
        }

        private static void RegisterHttpServices(
            IServiceCollection services)
        {
            services.AddSingleton<HttpClient>(sp =>
            {
                var client = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };

                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 (KHTML, like Gecko) " +
                    "Chrome/108.0.0.0 Safari/537.36");

                return client;
            });
        }

        private static void RegisterTranslationServices(
            IServiceCollection services)
        {
            services.AddSingleton<AdvancedTranslationService>(sp =>
                new AdvancedTranslationService(
                    sp.GetRequiredService<HttpClient>(),
                    sp.GetRequiredService<ILogger>()));

            services.AddSingleton<ITranslationService>(sp =>
            {
                AppSettings settings =
                    sp.GetRequiredService<AppSettings>();

                ILogger logger =
                    sp.GetRequiredService<ILogger>();

                AdvancedTranslationService baseService =
                    sp.GetRequiredService<AdvancedTranslationService>();

                return new PerformanceOptimizedTranslationService(
                    baseService,
                    logger,
                    maxConcurrentTranslations:
                        NormalizePositive(
                            settings.MaxConcurrentTranslations,
                            4),
                    batchSize:
                        NormalizePositive(
                            settings.TranslationBatchSize,
                            10),
                    batchCollectionWindow:
                        TimeSpan.FromMilliseconds(
                            NormalizePositive(
                                settings.BatchCollectionWindowMs,
                                100)),
                    enableBatchProcessing:
                        settings.EnableBatchProcessing,
                    enableRealtimeBatchProcessing:
                        settings.EnableRealtimeBatchProcessing,
                    realtimeBatchThresholdMs:
                        NormalizePositive(
                            settings.RealtimeBatchThresholdMs,
                            100),
                    maxCacheSize:
                        NormalizePositive(
                            settings.CacheSizeLimit,
                            5000),
                    cacheCleanupIntervalMinutes:
                        NormalizePositive(
                            settings.CacheCleanupIntervalMinutes,
                            30),
                    enableSmartCache:
                        settings.EnableSmartCache,
                    cacheCleanupThreshold:
                        NormalizeCacheThreshold(
                            settings.CacheCleanupThreshold));
            });
        }

        private static void RegisterOcrServices(
            IServiceCollection services)
        {
            services.AddSingleton<WindowsOcrService>(sp =>
                new WindowsOcrService(
                    sp.GetRequiredService<ILogger>()));

            services.AddSingleton<IOcrService>(sp =>
                new OcrService(
                    sp.GetRequiredService<ILogger>(),
                    sp.GetRequiredService<AppSettings>()));
        }

        private static void RegisterVideoServices(
            IServiceCollection services)
        {
            services.AddSingleton<IVideoCaptureService>(sp =>
                new VideoCaptureService(
                    sp.GetRequiredService<ILogger>()));

            services.AddSingleton<IOcrComparisonService>(sp =>
                new OcrComparisonService(
                    sp.GetRequiredService<ILogger>(),
                    sp.GetRequiredService<AppSettings>()));

            services.AddSingleton<IOcrAccuracyService>(sp =>
                new OcrAccuracyService(
                    sp.GetRequiredService<ILogger>()));

            services.AddSingleton<IRealtimeVideoOcrService>(sp =>
                new RealtimeVideoOcrService(
                    sp.GetRequiredService<ILogger>(),
                    sp.GetRequiredService<IVideoCaptureService>(),
                    sp.GetRequiredService<IOcrComparisonService>(),
                    sp.GetRequiredService<IOcrAccuracyService>(),
                    sp.GetRequiredService<IOcrService>(),
                    sp.GetRequiredService<AppSettings>()));
        }

        private static void RegisterMemoryServices(
            IServiceCollection services)
        {
            services.AddSingleton<IProcessService>(sp =>
                new ProcessService(
                    sp.GetRequiredService<ILogger>()));

            services.AddSingleton<IMemoryService>(sp =>
                new MemoryService(
                    sp.GetRequiredService<ILogger>(),
                    sp.GetRequiredService<AppSettings>()));

            services.AddSingleton<EnhancedMemoryService>(sp =>
                new EnhancedMemoryService(
                    sp.GetRequiredService<ILogger>(),
                    sp.GetRequiredService<AppSettings>()));

            services.AddSingleton<IGameRecipeService, GameRecipeService>();

            services.AddSingleton<PointerValidationService>(sp =>
                new PointerValidationService(
                    sp.GetRequiredService<IMemoryService>(),
                    sp.GetRequiredService<ILogger>(),
                    sp.GetRequiredService<AppSettings>()));
        }

        private static void RegisterUtilityServices(
            IServiceCollection services)
        {
            services.AddSingleton<AnomalyDetector>(sp =>
                new AnomalyDetector(
                    sp.GetRequiredService<ILogger>(),
                    sp.GetRequiredService<AppSettings>()));

            services.AddSingleton<MLTextProcessor>(sp =>
                new MLTextProcessor(
                    sp.GetRequiredService<ILogger>(),
                    sp.GetRequiredService<AppSettings>()));
        }

        private static int NormalizePositive(
            int value,
            int fallback)
        {
            return value > 0
                ? value
                : fallback;
        }

        private static double NormalizeCacheThreshold(
            double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value))
            {
                return 0.8;
            }

            if (value < 0)
                return 0;

            if (value > 1)
                return 1;

            return value;
        }
    }

    public sealed class SettingsAutoSaveTimer : IDisposable
    {
        private readonly SettingsManager _settingsManager;
        private readonly AppSettings _appSettings;
        private readonly ILogger _logger;

        private Timer _timer;

        private int _saving;
        private int _disposed;

        public SettingsAutoSaveTimer(
            SettingsManager settingsManager,
            AppSettings appSettings,
            ILogger logger)
        {
            _settingsManager =
                settingsManager ??
                throw new ArgumentNullException(
                    nameof(settingsManager));

            _appSettings =
                appSettings ??
                throw new ArgumentNullException(
                    nameof(appSettings));

            _logger =
                logger ??
                throw new ArgumentNullException(
                    nameof(logger));

            _timer =
                new Timer(
                    AutoSaveCallback,
                    null,
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(1));
        }

        private void AutoSaveCallback(object state)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (Interlocked.CompareExchange(
                    ref _saving,
                    1,
                    0) != 0)
            {
                return;
            }

            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;

                _settingsManager.SaveSettings(
                    _appSettings);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Ayarların otomatik kaydedilmesi sırasında hata oluştu.",
                    ex);
            }
            finally
            {
                Interlocked.Exchange(
                    ref _saving,
                    0);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) != 0)
            {
                return;
            }

            Timer timer =
                Interlocked.Exchange(
                    ref _timer,
                    null);

            if (timer != null)
            {
                try
                {
                    timer.Change(
                        Timeout.Infinite,
                        Timeout.Infinite);
                }
                catch
                {
                }

                timer.Dispose();
            }

            try
            {
                _settingsManager.SaveSettings(
                    _appSettings);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Kapanış sırasında ayarlar kaydedilemedi.",
                    ex);
            }
        }
    }
}