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
                    _logger.LogInformation($"Recipe file '{RecipesFileName}' not found. Cache will be empty.");
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
                _logger.LogInformation($"{_recipeCache.Count} game recipes loaded into cache.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading recipe file: {RecipesFileName}", ex);
            }
        }

        public void SaveOrUpdateRecipe(GameRecipe newRecipe)
        {
            if (newRecipe == null || string.IsNullOrWhiteSpace(newRecipe.ProcessName))
            {
                _logger.LogWarning("Attempted to save an invalid recipe.");
                return;
            }

            string jsonString = File.Exists(RecipesFileName) ? File.ReadAllText(RecipesFileName) : "[]";
            var recipes = JsonSerializer.Deserialize<List<GameRecipe>>(jsonString) ?? new List<GameRecipe>();

            var existingRecipe = recipes.FirstOrDefault(r => r.ProcessName.Equals(newRecipe.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (existingRecipe != null)
            {
                existingRecipe.PathInfo = newRecipe.PathInfo;
                _logger.LogInformation($"Updated recipe for '{newRecipe.ProcessName}'.");
            }
            else
            {
                recipes.Add(newRecipe);
                _logger.LogInformation($"Added new recipe for '{newRecipe.ProcessName}'.");
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
                _logger.LogInformation($"Recipes saved to '{RecipesFileName}' successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to save recipes file.", ex);
            }
        }

        public Task<PathInfo> GetRecipeForProcessAsync(Process process)
        {
            if (process == null) return Task.FromResult<PathInfo>(null);

            if (_recipeCache.TryGetValue(process.ProcessName, out var pathInfo))
            {
                _logger.LogInformation($"Recipe found in cache for '{process.ProcessName}'.");
                return Task.FromResult(pathInfo);
            }

            _logger.LogWarning($"Recipe not found for '{process.ProcessName}'. Please check '{RecipesFileName}'.");
            return Task.FromResult<PathInfo>(null);
        }
    }
}