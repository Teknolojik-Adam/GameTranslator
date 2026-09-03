using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public interface IGameRecipeService
    {
        Task<PathInfo> GetRecipeForProcessAsync(
            Process process);

        void SaveOrUpdateRecipe(
            GameRecipe newRecipe);

        void ReloadRecipes();

        void ClearCache();
    }

    public sealed class GameRecipeService :
        IGameRecipeService,
        IDisposable
    {
        private const string RecipesFileName =
            "game_recipes.json";

        private readonly ILogger _logger;
        private readonly Dictionary<string, PathInfo> _recipeCache;
        private readonly object _cacheLock;
        private readonly object _fileLock;

        private FileSystemWatcher _fileWatcher;
        private Timer _reloadTimer;
        private bool _disposed;

        public GameRecipeService(
            ILogger logger)
        {
            if (logger == null)
            {
                throw new ArgumentNullException(
                    nameof(logger));
            }

            _logger =
                logger;

            _recipeCache =
                new Dictionary<string, PathInfo>(
                    StringComparer.OrdinalIgnoreCase);

            _cacheLock =
                new object();

            _fileLock =
                new object();

            LoadRecipesToCache();

            SetupFileWatcher();
        }

        public Task<PathInfo> GetRecipeForProcessAsync(
            Process process)
        {
            if (_disposed)
            {
                return Task.FromResult<PathInfo>(
                    null);
            }

            if (process == null)
            {
                return Task.FromResult<PathInfo>(
                    null);
            }

            string processName;

            try
            {
                processName =
                    process.ProcessName;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Process adı alınamadı.",
                    ex);

                return Task.FromResult<PathInfo>(
                    null);
            }

            string processKey =
                NormalizeProcessName(
                    processName);

            if (string.IsNullOrWhiteSpace(
                processKey))
            {
                return Task.FromResult<PathInfo>(
                    null);
            }

            lock (_cacheLock)
            {
                PathInfo pathInfo;

                if (_recipeCache.TryGetValue(
                    processKey,
                    out pathInfo))
                {
                    _logger.LogInformation(
                        $"'{processName}' önerisi önbellekte bulundu.");

                    return Task.FromResult(
                        pathInfo);
                }
            }

            _logger.LogWarning(
                $"'{processName}' için öneri bulunamadı. '{RecipesFileName}' dosyasını kontrol edin.");

            return Task.FromResult<PathInfo>(
                null);
        }

        public void SaveOrUpdateRecipe(
            GameRecipe newRecipe)
        {
            if (_disposed)
            {
                _logger.LogWarning(
                    "GameRecipeService dispose edilmiş durumda.");

                return;
            }

            if (newRecipe == null ||
                string.IsNullOrWhiteSpace(
                    newRecipe.ProcessName) ||
                newRecipe.PathInfo == null)
            {
                _logger.LogWarning(
                    "Geçersiz bir oyun önerisi kaydedilmeye çalışıldı.");

                return;
            }

            string normalizedName =
                NormalizeProcessName(
                    newRecipe.ProcessName);

            if (string.IsNullOrWhiteSpace(
                normalizedName))
            {
                _logger.LogWarning(
                    "Geçersiz process adı.");

                return;
            }

            try
            {
                lock (_fileLock)
                {
                    List<GameRecipe> recipes =
                        ReadRecipesFromFile();

                    GameRecipe existingRecipe =
                        recipes.FirstOrDefault(
                            r =>
                                r != null &&
                                string.Equals(
                                    NormalizeProcessName(
                                        r.ProcessName),
                                    normalizedName,
                                    StringComparison.OrdinalIgnoreCase));

                    if (existingRecipe != null)
                    {
                        existingRecipe.ProcessName =
                            newRecipe.ProcessName.Trim();

                        existingRecipe.PathInfo =
                            newRecipe.PathInfo;

                        _logger.LogInformation(
                            $"'{newRecipe.ProcessName}' önerisi güncellendi.");
                    }
                    else
                    {
                        recipes.Add(
                            new GameRecipe
                            {
                                ProcessName =
                                    newRecipe.ProcessName.Trim(),

                                PathInfo =
                                    newRecipe.PathInfo
                            });

                        _logger.LogInformation(
                            $"'{newRecipe.ProcessName}' önerisi eklendi.");
                    }

                    SaveRecipesToFile(
                        recipes);
                }

                lock (_cacheLock)
                {
                    _recipeCache[normalizedName] =
                        newRecipe.PathInfo;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Oyun önerisi kaydedilirken hata oluştu.",
                    ex);
            }
        }

        public void ReloadRecipes()
        {
            if (_disposed)
                return;

            _logger.LogInformation(
                $"'{RecipesFileName}' dosyası elle yenileniyor...");

            LoadRecipesToCache();
        }

        public void ClearCache()
        {
            if (_disposed)
                return;

            lock (_cacheLock)
            {
                _recipeCache.Clear();
            }

            _logger.LogInformation(
                "Öneri önbelleği temizlendi.");
        }

        private void LoadRecipesToCache()
        {
            if (_disposed)
                return;

            try
            {
                List<GameRecipe> recipes;

                lock (_fileLock)
                {
                    if (!File.Exists(
                        GetRecipesFullPath()))
                    {
                        lock (_cacheLock)
                        {
                            _recipeCache.Clear();
                        }

                        _logger.LogInformation(
                            $"'{RecipesFileName}' dosyası bulunamadı. Önbellek boş.");

                        return;
                    }

                    recipes =
                        ReadRecipesFromFile();
                }

                var newCache =
                    new Dictionary<string, PathInfo>(
                        StringComparer.OrdinalIgnoreCase);

                for (int i = 0;
                     i < recipes.Count;
                     i++)
                {
                    GameRecipe recipe =
                        recipes[i];

                    if (recipe == null ||
                        string.IsNullOrWhiteSpace(
                            recipe.ProcessName) ||
                        recipe.PathInfo == null)
                    {
                        continue;
                    }

                    string processKey =
                        NormalizeProcessName(
                            recipe.ProcessName);

                    if (string.IsNullOrWhiteSpace(
                        processKey))
                    {
                        continue;
                    }

                    newCache[processKey] =
                        recipe.PathInfo;
                }

                lock (_cacheLock)
                {
                    _recipeCache.Clear();

                    foreach (KeyValuePair<string, PathInfo> pair
                             in newCache)
                    {
                        _recipeCache[pair.Key] =
                            pair.Value;
                    }
                }

                _logger.LogInformation(
                    $"{newCache.Count} oyun önerisi önbelleğe yüklendi.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    $"'{RecipesFileName}' dosyası yüklenirken hata oluştu.",
                    ex);
            }
        }

        private List<GameRecipe> ReadRecipesFromFile()
        {
            string fullPath =
                GetRecipesFullPath();

            if (!File.Exists(
                fullPath))
            {
                return new List<GameRecipe>();
            }

            string jsonString =
                ReadFileWithRetry(
                    fullPath);

            if (string.IsNullOrWhiteSpace(
                jsonString))
            {
                return new List<GameRecipe>();
            }

            var options =
                new JsonSerializerOptions
                {
                    AllowTrailingCommas =
                        true,

                    ReadCommentHandling =
                        JsonCommentHandling.Skip,

                    PropertyNameCaseInsensitive =
                        true
                };

            List<GameRecipe> recipes =
                JsonSerializer.Deserialize<List<GameRecipe>>(
                    jsonString,
                    options);

            return recipes ??
                   new List<GameRecipe>();
        }

        private void SaveRecipesToFile(
            List<GameRecipe> recipes)
        {
            if (recipes == null)
            {
                recipes =
                    new List<GameRecipe>();
            }

            string fullPath =
                GetRecipesFullPath();

            string directory =
                Path.GetDirectoryName(
                    fullPath);

            if (!string.IsNullOrWhiteSpace(
                    directory) &&
                !Directory.Exists(
                    directory))
            {
                Directory.CreateDirectory(
                    directory);
            }

            var options =
                new JsonSerializerOptions
                {
                    WriteIndented =
                        true
                };

            string jsonString =
                JsonSerializer.Serialize(
                    recipes,
                    options);

            string tempPath =
                fullPath + ".tmp";

            string backupPath =
                fullPath + ".bak";

            File.WriteAllText(
                tempPath,
                jsonString,
                new UTF8Encoding(false));

            if (File.Exists(
                fullPath))
            {
                try
                {
                    File.Replace(
                        tempPath,
                        fullPath,
                        backupPath,
                        true);
                }
                catch
                {
                    File.Copy(
                        tempPath,
                        fullPath,
                        true);

                    File.Delete(
                        tempPath);
                }
            }
            else
            {
                File.Move(
                    tempPath,
                    fullPath);
            }

            _logger.LogInformation(
                $"Öneriler '{RecipesFileName}' dosyasına kaydedildi.");
        }

        private void SetupFileWatcher()
        {
            try
            {
                string fullPath =
                    GetRecipesFullPath();

                string directory =
                    Path.GetDirectoryName(
                        fullPath);

                string fileName =
                    Path.GetFileName(
                        fullPath);

                if (string.IsNullOrWhiteSpace(
                    directory))
                {
                    directory =
                        AppDomain.CurrentDomain.BaseDirectory;
                }

                if (!Directory.Exists(
                    directory))
                {
                    Directory.CreateDirectory(
                        directory);
                }

                _fileWatcher =
                    new FileSystemWatcher(
                        directory,
                        fileName);

                _fileWatcher.NotifyFilter =
                    NotifyFilters.LastWrite |
                    NotifyFilters.FileName |
                    NotifyFilters.Size |
                    NotifyFilters.CreationTime;

                _fileWatcher.Changed +=
                    OnRecipesFileChanged;

                _fileWatcher.Created +=
                    OnRecipesFileChanged;

                _fileWatcher.Deleted +=
                    OnRecipesFileChanged;

                _fileWatcher.Renamed +=
                    OnRecipesFileRenamed;

                _fileWatcher.EnableRaisingEvents =
                    true;

                _logger.LogInformation(
                    $"'{RecipesFileName}' dosyası izleniyor.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Dosya izleyici ayarlanırken hata oluştu.",
                    ex);
            }
        }

        private void OnRecipesFileChanged(
            object sender,
            FileSystemEventArgs e)
        {
            ScheduleReload();
        }

        private void OnRecipesFileRenamed(
            object sender,
            RenamedEventArgs e)
        {
            ScheduleReload();
        }

        private void ScheduleReload()
        {
            if (_disposed)
                return;

            lock (_fileLock)
            {
                if (_reloadTimer == null)
                {
                    _reloadTimer =
                        new Timer(
                            ReloadTimerCallback,
                            null,
                            250,
                            Timeout.Infinite);
                }
                else
                {
                    _reloadTimer.Change(
                        250,
                        Timeout.Infinite);
                }
            }
        }

        private void ReloadTimerCallback(
            object state)
        {
            if (_disposed)
                return;

            try
            {
                LoadRecipesToCache();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Öneri dosyası otomatik yenilenirken hata oluştu.",
                    ex);
            }
        }

        private static string ReadFileWithRetry(
            string path)
        {
            const int attempts =
                5;

            for (int i = 0;
                 i < attempts;
                 i++)
            {
                try
                {
                    using (var stream =
                           new FileStream(
                               path,
                               FileMode.Open,
                               FileAccess.Read,
                               FileShare.ReadWrite |
                               FileShare.Delete))
                    using (var reader =
                           new StreamReader(
                               stream,
                               Encoding.UTF8,
                               true))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (IOException)
                {
                    if (i == attempts - 1)
                    {
                        throw;
                    }

                    Thread.Sleep(
                        50);
                }
            }

            return string.Empty;
        }

        private static string NormalizeProcessName(
            string processName)
        {
            if (string.IsNullOrWhiteSpace(
                processName))
            {
                return string.Empty;
            }

            string normalized =
                processName.Trim();

            if (normalized.EndsWith(
                ".exe",
                StringComparison.OrdinalIgnoreCase))
            {
                normalized =
                    normalized.Substring(
                        0,
                        normalized.Length - 4);
            }

            return normalized.Trim();
        }

        private static string GetRecipesFullPath()
        {
            return Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                RecipesFileName);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed =
                true;

            if (_fileWatcher != null)
            {
                _fileWatcher.EnableRaisingEvents =
                    false;

                _fileWatcher.Changed -=
                    OnRecipesFileChanged;

                _fileWatcher.Created -=
                    OnRecipesFileChanged;

                _fileWatcher.Deleted -=
                    OnRecipesFileChanged;

                _fileWatcher.Renamed -=
                    OnRecipesFileRenamed;

                _fileWatcher.Dispose();

                _fileWatcher =
                    null;
            }

            lock (_fileLock)
            {
                if (_reloadTimer != null)
                {
                    _reloadTimer.Dispose();
                    _reloadTimer =
                        null;
                }
            }

            _logger.LogInformation(
                "GameRecipeService kapatıldı.");
        }
    }
}