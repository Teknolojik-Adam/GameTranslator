using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http;
using System.Threading;

namespace GameTranslatorUltimate
{
    public static class ServiceContainer
    {
        private static ServiceProvider _serviceProvider;

        public static void Initialize()
        {
            if (_serviceProvider != null) return;

            var services = new ServiceCollection();
            
            // Temel servisler
            services.AddSingleton<ILogger, ConsoleLogger>();
            services.AddSingleton<SettingsManager>(sp =>
                new SettingsManager(sp.GetRequiredService<ILogger>()));
            services.AddSingleton<AppSettings>(sp =>
                sp.GetRequiredService<SettingsManager>().LoadSettings());

            // İşlem ve bellek servisleri
            services.AddSingleton<IProcessService>(sp =>
                new ProcessService(sp.GetRequiredService<ILogger>()));
            services.AddSingleton<IMemoryService>(sp =>
                new MemoryService(sp.GetRequiredService<ILogger>(), sp.GetRequiredService<AppSettings>()));
            services.AddSingleton<EnhancedMemoryService>(sp =>
                new EnhancedMemoryService(sp.GetRequiredService<ILogger>(), sp.GetRequiredService<AppSettings>()));
            services.AddSingleton<IGameRecipeService, GameRecipeService>();

            // Çeviri servisleri
            services.AddSingleton<AdvancedTranslationService>();
            services.AddSingleton<ITranslationService>(sp =>
            {
                var settings = sp.GetRequiredService<AppSettings>();
                var logger = sp.GetRequiredService<ILogger>();

                var baseService = new AdvancedTranslationService(
                    sp.GetRequiredService<HttpClient>(),
                    logger);

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

                return optimizedService;
            });

            // OCR servisleri
            services.AddSingleton<WindowsOcrService>(sp =>
                new WindowsOcrService(sp.GetRequiredService<ILogger>()));
            services.AddSingleton<IOcrService>(sp =>
                new OcrService(sp.GetRequiredService<ILogger>(), sp.GetRequiredService<AppSettings>()));

            // Video OCR servisleri
            services.AddSingleton<IVideoCaptureService>(sp =>
                new VideoCaptureService(sp.GetRequiredService<ILogger>()));
            services.AddSingleton<IOcrComparisonService>(sp =>
                new OcrComparisonService(sp.GetRequiredService<ILogger>(), sp.GetRequiredService<AppSettings>()));
            services.AddSingleton<IOcrAccuracyService>(sp =>
                new OcrAccuracyService(sp.GetRequiredService<ILogger>()));
            services.AddSingleton<IRealtimeVideoOcrService>(sp =>
                new RealtimeVideoOcrService(
                    sp.GetRequiredService<ILogger>(),
                    sp.GetRequiredService<IVideoCaptureService>(),
                    sp.GetRequiredService<IOcrComparisonService>(),
                    sp.GetRequiredService<IOcrAccuracyService>(),
                    sp.GetRequiredService<AppSettings>()));

            // Anomali tespit servisi
            services.AddSingleton<AnomalyDetector>(sp =>
                new AnomalyDetector(sp.GetRequiredService<ILogger>(), sp.GetRequiredService<AppSettings>()));

            // Pointer validation servisi
            services.AddSingleton<PointerValidationService>(sp =>
                new PointerValidationService(sp.GetRequiredService<IMemoryService>(), sp.GetRequiredService<ILogger>(), sp.GetRequiredService<AppSettings>()));

            // ML metin işleme servisi
            services.AddSingleton<MLTextProcessor>(sp =>
                new MLTextProcessor(sp.GetRequiredService<ILogger>(), sp.GetRequiredService<AppSettings>()));

            // HTTP Client
            services.AddSingleton<HttpClient>(sp =>
            {
                var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/108.0.0.0 Safari/537.36"
                );
                return client;
            });

            services.AddSingleton<SettingsAutoSaveTimer>(sp =>
                new SettingsAutoSaveTimer(
                    sp.GetRequiredService<SettingsManager>(),
                    sp.GetRequiredService<AppSettings>(),
                    sp.GetRequiredService<ILogger>() 
                ));

            _serviceProvider = services.BuildServiceProvider();

            _serviceProvider.GetRequiredService<SettingsAutoSaveTimer>();
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
            // IDisposable olan tüm singleton servislerin (Timer, HttpClient )
            // kaynaklarını serbest bırakması için.
            _serviceProvider?.Dispose();
            _serviceProvider = null;
        }
    }

    public class SettingsAutoSaveTimer : IDisposable
    {
        private readonly SettingsManager _settingsManager;
        private readonly AppSettings _appSettings;
        private readonly ILogger _logger;
        private Timer _timer;

        public SettingsAutoSaveTimer(SettingsManager settingsManager, AppSettings appSettings, ILogger logger)
        {
            _settingsManager = settingsManager;
            _appSettings = appSettings;
            _logger = logger;
            _timer = new Timer(AutoSaveCallback, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        private void AutoSaveCallback(object state)
        {
            try
            {
                // Değişiklikleri kaydet.
                _settingsManager.SaveSettings(_appSettings);
            }
            catch (Exception ex)
            {
                _logger.LogError("Ayarların otomatik kaydedilmesi sırasında bir hata oluştu.", ex);
            }
        }

        public void Dispose()
        {
            // Uygulama kapanırken Timer'ı düzgün bir şekilde sonlandırmak için.
            _timer?.Dispose();
        }
    }
}
