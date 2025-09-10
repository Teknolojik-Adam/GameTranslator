using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http;

namespace P5S_ceviri
{
    public static class ServiceContainer
    {
        private static ServiceProvider _serviceProvider;

        public static void Initialize()
        {
            if (_serviceProvider != null) return;

            var services = new ServiceCollection();

            
            services.AddSingleton<ILogger, ConsoleLogger>();
            services.AddSingleton<AppSettings>();
            services.AddSingleton<SettingsManager>();

            
            services.AddSingleton<IProcessService, ProcessService>();
            services.AddSingleton<IMemoryService, MemoryService>();
            services.AddSingleton<IGameRecipeService, GameRecipeService>();
            services.AddSingleton<AdvancedTranslationService>();
            services.AddSingleton<ITranslationService>(sp =>
            {
                var settings = sp.GetRequiredService<AppSettings>();
                var logger = sp.GetRequiredService<ILogger>();
                var baseService = new AdvancedTranslationService(
                    sp.GetRequiredService<HttpClient>(),
                    logger);
                
                // Önbellek ayarlarını kullanarak performans servisini oluştur
                var optimizedService = new PerformanceOptimizedTranslationService(
                    baseService,
                    logger,
                    maxConcurrentTranslations: settings.MaxConcurrentTranslations,
                    batchSize: settings.TranslationBatchSize,
                    batchCollectionWindow: TimeSpan.FromMilliseconds(settings.BatchCollectionWindowMs),
                    enableBatchProcessing: settings.EnableBatchProcessing,
                    enableRealtimeBatchProcessing: settings.EnableRealtimeBatchProcessing,
                    realtimeBatchThresholdMs: settings.RealtimeBatchThresholdMs,
                    maxCacheSize: settings.CacheSizeLimit,
                    cacheCleanupIntervalMinutes: settings.CacheCleanupIntervalMinutes,
                    enableSmartCache: settings.EnableSmartCache,
                    cacheCleanupThreshold: settings.CacheCleanupThreshold);
                
                // Önbellek ayarlarını dinamik olarak güncellemek için event
                settings.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(settings.CacheSizeLimit) ||
                        e.PropertyName == nameof(settings.EnableSmartCache) ||
                        e.PropertyName == nameof(settings.CacheCleanupThreshold))
                    {
                        // Önbellek ayarları değiştiğinde log yaz
                        logger.LogInformation($"Önbellek ayarları güncellendi: CacheSize={settings.CacheSizeLimit}, " +
                            $"EnableSmartCache={settings.EnableSmartCache}, Threshold={settings.CacheCleanupThreshold}");
                    }
                };
                
                return optimizedService;
            });

            
            services.AddSingleton<IOcrEngine, WindowsOcrEngine>();
            services.AddSingleton<IOcrEngine, TesseractOcrEngine>();

            
            services.AddSingleton<IOcrService>(sp =>
                new OcrService(sp.GetRequiredService<ILogger>(), sp.GetRequiredService<AppSettings>()));

           
            services.AddSingleton<HttpClient>(sp =>
            {
                var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/108.0.0.0 Safari/537.36"
                );
                return client;
            });

            _serviceProvider = services.BuildServiceProvider();
        }

        public static T GetService<T>() where T : class
        {
            if (_serviceProvider == null)
            {
                throw new InvalidOperationException("ServiceContainer.Initialize() has not been called.");
            }
            return _serviceProvider.GetRequiredService<T>();
        }

        public static void Cleanup()
        {
            _serviceProvider?.Dispose();
            _serviceProvider = null;
        }
    }
}