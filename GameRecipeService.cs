using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public interface IGameRecipeService
    {
        Task<PathInfo> GetRecipeForProcessAsync(Process process);
        void SaveOrUpdateRecipe(GameRecipe newRecipe);
        void ReloadRecipes();
        void ClearCache();
    }

    public class GameRecipeService : IGameRecipeService, IDisposable
    {
        private readonly ILogger _logger;
        private const string RecipesFileName = "game_recipes.json";
        private readonly Dictionary<string, PathInfo> _recipeCache;
        private FileSystemWatcher _fileWatcher;

        public GameRecipeService(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _recipeCache = new Dictionary<string, PathInfo>(StringComparer.OrdinalIgnoreCase);
            LoadRecipesToCache();
            SetupFileWatcher();
        }

        private void LoadRecipesToCache()
        {
            try
            {
                if (!File.Exists(RecipesFileName))
                {
                    _logger.LogInformation($"'{RecipesFileName}' dosyası bulunamadı. Önbellek boş olacak.");
                    return;
                }

                string jsonString = File.ReadAllText(RecipesFileName);
                var recipes = JsonSerializer.Deserialize<List<GameRecipe>>(jsonString);

                if (recipes == null) return;

                _recipeCache.Clear();
                foreach (var recipe in recipes)
                {
                    if (string.IsNullOrWhiteSpace(recipe.ProcessName) || recipe.PathInfo == null) continue;

                    var processKey = NormalizeProcessName(recipe.ProcessName);
                    if (!_recipeCache.ContainsKey(processKey))
                    {
                        _recipeCache.Add(processKey, recipe.PathInfo);
                    }
                }
                _logger.LogInformation($"{_recipeCache.Count} oyun önbelleğe yüklendi.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"'{RecipesFileName}' dosyası yüklenirken hata oluştu", ex);
            }
        }

        private string NormalizeProcessName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return processName;

            return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName.Substring(0, processName.Length - 4)
                : processName;
        }

        public void SaveOrUpdateRecipe(GameRecipe newRecipe)
        {
            if (newRecipe == null || string.IsNullOrWhiteSpace(newRecipe.ProcessName) || newRecipe.PathInfo == null)
            {
                _logger.LogWarning("Geçersiz bir öneri kaydetmeye çalışıldı.");
                return;
            }

            try
            {
                string jsonString = File.Exists(RecipesFileName) ? File.ReadAllText(RecipesFileName) : "[]";
                var recipes = JsonSerializer.Deserialize<List<GameRecipe>>(jsonString) ?? new List<GameRecipe>();

                var existingRecipe = recipes.FirstOrDefault(r => r.ProcessName.Equals(newRecipe.ProcessName, StringComparison.OrdinalIgnoreCase));
                if (existingRecipe != null)
                {
                    existingRecipe.PathInfo = newRecipe.PathInfo;
                    _logger.LogInformation($"'{newRecipe.ProcessName}' öneri güncellendi.");
                }
                else
                {
                    recipes.Add(newRecipe);
                    _logger.LogInformation($"'{newRecipe.ProcessName}' öneri eklendi.");
                }

                var processKey = NormalizeProcessName(newRecipe.ProcessName);
                _recipeCache[processKey] = newRecipe.PathInfo;

                SaveRecipesToFile(recipes);
            }
            catch (Exception ex)
            {
                _logger.LogError($"öneri kaydedilirken hata oluştu", ex);
            }
        }

        private void SaveRecipesToFile(List<GameRecipe> recipes)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(recipes, options);
                File.WriteAllText(RecipesFileName, jsonString);
                _logger.LogInformation($"öneri '{RecipesFileName}' dosyasına başarıyla kaydedildi.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"öneri '{RecipesFileName}' dosyasına kaydedilirken hata oluştu", ex);
            }
        }

        public Task<PathInfo> GetRecipeForProcessAsync(Process process)
        {
            if (process == null) return Task.FromResult<PathInfo>(null);

            if (_recipeCache.TryGetValue(NormalizeProcessName(process.ProcessName), out var pathInfo))
            {
                _logger.LogInformation($"'{process.ProcessName}' öneri önbellekte bulundu.");
                return Task.FromResult(pathInfo);
            }

            _logger.LogWarning($"'{process.ProcessName}' öneri bulunamadı. Lütfen kontrol edin '{RecipesFileName}'.");
            return Task.FromResult<PathInfo>(null);
        }

        private void SetupFileWatcher()
        {
            try
            {
                if (!File.Exists(RecipesFileName))
                {
                    _logger.LogWarning($"'{RecipesFileName}' dosyası bulunamadı, dosya izleyici ayarlanamadı.");
                    return;
                }

                string fullPath = Path.GetFullPath(RecipesFileName);
                string directory = Path.GetDirectoryName(fullPath);
                string fileName = Path.GetFileName(fullPath);

                _fileWatcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite
                };
                _fileWatcher.Changed += OnRecipesFileChanged;
                _fileWatcher.EnableRaisingEvents = true;
                _logger.LogInformation($"'{RecipesFileName}' dosyası izleniyor.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Dosya izleyici ayarlanırken hata oluştu", ex);
            }
        }

        private void OnRecipesFileChanged(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType == WatcherChangeTypes.Changed)
            {
                _logger.LogInformation($"'{RecipesFileName}' dosyası değişti, önbellek yenileniyor...");
                System.Threading.Thread.Sleep(100); // Dosya yazımının tamamlanmasını bekle
                LoadRecipesToCache();
            }
        }

        public void ReloadRecipes()
        {
            _logger.LogInformation($"'{RecipesFileName}' dosyası elle yenileniyor...");
            LoadRecipesToCache();
        }

        public void ClearCache()
        {
            lock (_recipeCache)
            {
                _recipeCache.Clear();
                _logger.LogInformation("Öneri önbelleği temizlendi.");
            }
        }

        public void Dispose()
        {
            if (_fileWatcher != null)
            {
                _fileWatcher.Changed -= OnRecipesFileChanged;
                _fileWatcher.Dispose();
                _fileWatcher = null;
            }
            _logger.LogInformation("Dosya izleyici durduruldu ve kaynaklar serbest bırakıldı.");
        }
    }
}