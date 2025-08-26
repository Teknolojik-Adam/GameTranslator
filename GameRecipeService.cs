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
        private readonly Dictionary<string, PathInfo> _recipeCache;

        public GameRecipeService(ILogger logger)
        {
            _logger = logger;
            _recipeCache = new Dictionary<string, PathInfo>(StringComparer.OrdinalIgnoreCase);
            LoadRecipesToCache();
        }

        private void LoadRecipesToCache()
        {
            try
            {
                if (!File.Exists(RecipesFileName))
                {
                    _logger.LogInformation($" '{RecipesFileName}' dosya bulunamadı. Önbellek boş olacak..");
                    return;
                }

                string jsonString = File.ReadAllText(RecipesFileName);
                var recipes = JsonSerializer.Deserialize<List<GameRecipe>>(jsonString);

                if (recipes == null) return;

                _recipeCache.Clear();
                foreach (var recipe in recipes)
                {
                    if (string.IsNullOrWhiteSpace(recipe.ProcessName) || recipe.PathInfo == null) continue;

                    var processKey = recipe.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? recipe.ProcessName.Substring(0, recipe.ProcessName.Length - 4)
                        : recipe.ProcessName;

                    if (!_recipeCache.ContainsKey(processKey))
                    {
                        _recipeCache.Add(processKey, recipe.PathInfo);
                    }
                }
                _logger.LogInformation($"{_recipeCache.Count} oyun önbelleğe yüklendi.");
            }
            catch (Exception ex)
            {
                _logger.LogError($" dosyası yüklenemiyor: {RecipesFileName}", ex);
            }
        }

        public void SaveOrUpdateRecipe(GameRecipe newRecipe)
        {
            if (newRecipe == null || string.IsNullOrWhiteSpace(newRecipe.ProcessName))
            {
                _logger.LogWarning("Geçersiz bir dosya kaydetmeye çalıştın.");
                return;
            }

            string jsonString = File.Exists(RecipesFileName) ? File.ReadAllText(RecipesFileName) : "[]";
            var recipes = JsonSerializer.Deserialize<List<GameRecipe>>(jsonString) ?? new List<GameRecipe>();

            var existingRecipe = recipes.FirstOrDefault(r => r.ProcessName.Equals(newRecipe.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (existingRecipe != null)
            {
                existingRecipe.PathInfo = newRecipe.PathInfo;
                _logger.LogInformation($"Güncellenmiş  '{newRecipe.ProcessName}'.");
            }
            else
            {
                recipes.Add(newRecipe);
                _logger.LogInformation($"eklendi '{newRecipe.ProcessName}'.");
            }

            var processKey = newRecipe.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? newRecipe.ProcessName.Substring(0, newRecipe.ProcessName.Length - 4)
                : newRecipe.ProcessName;
            _recipeCache[processKey] = newRecipe.PathInfo;

            SaveRecipesToFile(recipes);
        }

        private void SaveRecipesToFile(List<GameRecipe> recipes)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(recipes, options);
                File.WriteAllText(RecipesFileName, jsonString);
                _logger.LogInformation($"Recipes saved to '{RecipesFileName}' Başarili.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"dosya kaydedilemedi", ex);
            }
        }

        public Task<PathInfo> GetRecipeForProcessAsync(Process process)
        {
            if (process == null) return Task.FromResult<PathInfo>(null);

            if (_recipeCache.TryGetValue(process.ProcessName, out var pathInfo))
            {
                _logger.LogInformation($"Önbellekte bulundu '{process.ProcessName}'.");
                return Task.FromResult(pathInfo);
            }

            _logger.LogWarning($"bulunamadı '{process.ProcessName}'. Lütfen kontrol edin'{RecipesFileName}'.");
            return Task.FromResult<PathInfo>(null);
        }
    }
}