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

            // Core services
            services.AddSingleton<ILogger, ConsoleLogger>();
            services.AddSingleton<AppSettings>();
            services.AddSingleton<SettingsManager>();

            // Process and memory services
            services.AddSingleton<IProcessService, ProcessService>();
            services.AddSingleton<IMemoryService, MemoryService>();
            services.AddSingleton<IGameRecipeService, GameRecipeService>();
            services.AddSingleton<ITranslationService, AdvancedTranslationService>();

            // OCR engines
            services.AddSingleton<IOcrEngine, WindowsOcrEngine>();
            services.AddSingleton<IOcrEngine, TesseractOcrEngine>();

            // OCR service
            services.AddSingleton<IOcrService>(sp =>
                new OcrService(sp.GetRequiredService<ILogger>(), sp.GetRequiredService<AppSettings>()));

            // HttpClient service
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