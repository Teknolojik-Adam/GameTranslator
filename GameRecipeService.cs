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
    }

    public class GameRecipeService : IGameRecipeService
    {
        private readonly ILogger _logger;
        private const string RecipesFileName = "game_recipes.json";
        private Dictionary<string, PathInfo> _recipeCache;

        public GameRecipeService(ILogger logger)
        {
            _logger = logger;
            LoadRecipes();
        }

        private void LoadRecipes()
        {
            _recipeCache = new Dictionary<string, PathInfo>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(RecipesFileName))
            {
                _logger.LogWarning($"json dosyası bulunamadı: '{RecipesFileName}'. Örnek bir dosya oluşturuluyor.");
                CreateSampleRecipeFile();
                return;
            }

            try
            {
                string jsonString = File.ReadAllText(RecipesFileName);
                var recipes = JsonSerializer.Deserialize<List<GameRecipe>>(jsonString);

                if (recipes == null)
                {
                    _logger.LogWarning("json dosyası okunamadı veya boş.");
                    return;
                }

                foreach (var recipe in recipes)
                {
                    if (!string.IsNullOrWhiteSpace(recipe.ProcessName) && recipe.PathInfo != null)
                    {
                        var processKey = recipe.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            ? recipe.ProcessName.Substring(0, recipe.ProcessName.Length - 4)
                            : recipe.ProcessName;

                        _recipeCache[processKey] = recipe.PathInfo;
                    }
                }
                _logger.LogInformation($"{_recipeCache.Count} adet oyun başarıyla yüklendi.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"json dosyası okunurken hata oluştu: '{RecipesFileName}'", ex);
            }
        }

        public void SaveOrUpdateRecipe(GameRecipe newRecipe)
        {
            if (newRecipe == null || string.IsNullOrWhiteSpace(newRecipe.ProcessName))
            {
                _logger.LogWarning("Kaydedilecek json geçersiz.");
                return;
            }

            string jsonString = File.Exists(RecipesFileName) ? File.ReadAllText(RecipesFileName) : "[]";
            var recipes = JsonSerializer.Deserialize<List<GameRecipe>>(jsonString) ?? new List<GameRecipe>();

            var existingRecipe = recipes.FirstOrDefault(r => r.ProcessName.Equals(newRecipe.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (existingRecipe != null)
            {
                existingRecipe.PathInfo = newRecipe.PathInfo;
                _logger.LogInformation($"'{newRecipe.ProcessName}' için mevcut json güncellendi.");
            }
            else
            {
                recipes.Add(newRecipe);
                _logger.LogInformation($"'{newRecipe.ProcessName}' için yeni json eklendi.");
            }

            var processKey = newRecipe.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? newRecipe.ProcessName.Substring(0, newRecipe.ProcessName.Length - 4)
                : newRecipe.ProcessName;
            _recipeCache[processKey] = newRecipe.PathInfo;

            SaveRecipesToFile(recipes);
        }

        private void CreateSampleRecipeFile()
        {
            var sampleRecipes = new List<GameRecipe>
            {
                new GameRecipe
                {
                    ProcessName = "",
                    PathInfo = new PathInfo
                    {
                        BaseAddressModule = ".exe",
                        BaseAddressOffset = 0x1A2B3C,
                        PointerOffsets = new List<int> { 0x40, 0x1F8, 0x10 }
                    }
                }
            };

            SaveRecipesToFile(sampleRecipes, true);
        }

        private void SaveRecipesToFile(List<GameRecipe> recipes, bool isSampleFile = false)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(recipes, options);
                File.WriteAllText(RecipesFileName, jsonString);

                if (isSampleFile)
                {
                    _logger.LogInformation($"Örnek json dosyası '{RecipesFileName}' oluşturuldu.");
                }
                else
                {
                    _logger.LogInformation($"json dosyası '{RecipesFileName}' başarıyla kaydedildi.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"json dosyası kaydedilemedi.", ex);
            }
        }

        public Task<PathInfo> GetRecipeForProcessAsync(Process process)
        {
            if (process == null) return Task.FromResult<PathInfo>(null);

            if (_recipeCache.TryGetValue(process.ProcessName, out var recipe))
            {
                _logger.LogInformation($"'{process.ProcessName}' için json bulundu.");
                return Task.FromResult(recipe);
            }

            _logger.LogWarning($"'{process.ProcessName}' için json bulunamadı. Lütfen '{RecipesFileName}' dosyasını kontrol edin.");
            return Task.FromResult<PathInfo>(null);
        }
    }
}