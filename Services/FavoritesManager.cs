using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VPM.Models;

namespace VPM.Services
{
    public class FavoritesManager
    {
        private readonly string _favoritesFilePath;
        private readonly string _shadowFilePath;
        private HashSet<string> _favoriteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _shadowChanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _shadowRemovals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _isLoaded = false;
        private DateTime _lastMainFileWriteTime = DateTime.MinValue;
        private FileSystemWatcher _fileWatcher;

        public event EventHandler FavoritesChanged;

        public FavoritesManager(string vamFolderPath)
        {
            _favoritesFilePath = Path.Combine(vamFolderPath, "Custom", "PluginData", "sfishere", "Favorites.txt");
            _shadowFilePath = Path.Combine(vamFolderPath, "Custom", "PluginData", "sfishere", "Favorites.shadow.txt");
            
            SetupFileWatcher();
        }

        private void SetupFileWatcher()
        {
            try
            {
                string directory = Path.GetDirectoryName(_favoritesFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _fileWatcher = new FileSystemWatcher(directory)
                {
                    Filter = Path.GetFileName(_favoritesFilePath),
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _fileWatcher.Changed += OnFavoritesFileChanged;
            }
            catch (Exception)
            {
            }
        }

        private void OnFavoritesFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                // Debounce multiple events
                var currentWriteTime = File.GetLastWriteTime(_favoritesFilePath);
                if (currentWriteTime <= _lastMainFileWriteTime)
                    return;

                _lastMainFileWriteTime = currentWriteTime;

                // Small delay to ensure file is fully written
                System.Threading.Thread.Sleep(100);

                // Reload favorites
                ReloadFavorites();

                // Notify listeners
                FavoritesChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            _fileWatcher?.Dispose();
        }

        public bool IsFavorite(string packageName)
        {
            if (!_isLoaded)
                LoadFavorites();

            return _favoriteNames.Contains(packageName);
        }

        public void AddFavorite(string packageName)
        {
            if (!_isLoaded)
                LoadFavorites();

            if (_favoriteNames.Add(packageName))
            {
                SaveFavorites();
            }
        }

        public void RemoveFavorite(string packageName)
        {
            if (!_isLoaded)
                LoadFavorites();

            if (_favoriteNames.Remove(packageName))
            {
                SaveFavorites();
            }
        }

        public void AddFavoriteBatch(IEnumerable<string> packageNames)
        {
            if (!_isLoaded)
                LoadFavorites();

            bool changed = false;
            foreach (var packageName in packageNames)
            {
                if (_favoriteNames.Add(packageName))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                SaveFavorites();
            }
        }

        public void RemoveFavoriteBatch(IEnumerable<string> packageNames)
        {
            if (!_isLoaded)
                LoadFavorites();

            bool changed = false;
            foreach (var packageName in packageNames)
            {
                if (_favoriteNames.Remove(packageName))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                SaveFavorites();
            }
        }

        public void ToggleFavorite(string packageName)
        {
            if (IsFavorite(packageName))
                RemoveFavorite(packageName);
            else
                AddFavorite(packageName);
        }

        public HashSet<string> GetAllFavorites()
        {
            if (!_isLoaded)
                LoadFavorites();

            return new HashSet<string>(_favoriteNames, StringComparer.OrdinalIgnoreCase);
        }

        public void LoadFavorites()
        {
            _favoriteNames.Clear();
            _shadowChanges.Clear();
            _shadowRemovals.Clear();
            _isLoaded = true;

            try
            {
                // Load main favorites file
                if (File.Exists(_favoritesFilePath))
                {
                    string json = File.ReadAllText(_favoritesFilePath);
                    var data = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.FavoritesData);

                    if (data?.FavoriteNames != null)
                    {
                        foreach (var name in data.FavoriteNames)
                        {
                            _favoriteNames.Add(name);
                        }
                    }
                }

                // Load and merge shadow file if it exists
                if (File.Exists(_shadowFilePath))
                {
                    string shadowJson = File.ReadAllText(_shadowFilePath);
                    var shadowData = JsonSerializer.Deserialize(shadowJson, JsonSourceGenerationContext.Default.ShadowFavoritesData);

                    if (shadowData != null)
                    {
                        // Apply additions from shadow
                        if (shadowData.Additions != null)
                        {
                            foreach (var name in shadowData.Additions)
                            {
                                _favoriteNames.Add(name);
                                _shadowChanges.Add(name);
                            }
                        }

                        // Apply removals from shadow
                        if (shadowData.Removals != null)
                        {
                            foreach (var name in shadowData.Removals)
                            {
                                _favoriteNames.Remove(name);
                                _shadowRemovals.Add(name);
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public void SaveFavorites()
        {
            try
            {
                string directory = Path.GetDirectoryName(_favoritesFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Try to write to main file first
                if (TryWriteToMainFile())
                {
                    // Success! Try to merge and delete shadow file if it exists
                    TryMergeShadowFile();
                }
                else
                {
                    // Main file is locked, use shadow file
                    WriteShadowFile();
                }
            }
            catch (Exception)
            {
            }
        }

        private bool TryWriteToMainFile()
        {
            try
            {
                var data = new FavoritesData
                {
                    FavoriteNames = _favoriteNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                };

                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = JsonSourceGenerationContext.Default });
                
                // Try to open file with exclusive access
                using (var fileStream = new FileStream(_favoritesFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fileStream))
                {
                    writer.Write(json);
                }
                
                return true;
            }
            catch (IOException)
            {
                // File is locked by another process (game)
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void WriteShadowFile()
        {
            try
            {
                // Load current main file to determine what changed
                var mainFavorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(_favoritesFilePath))
                {
                    try
                    {
                        string json = File.ReadAllText(_favoritesFilePath);
                        var data = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.FavoritesData);
                        if (data?.FavoriteNames != null)
                        {
                            foreach (var name in data.FavoriteNames)
                            {
                                mainFavorites.Add(name);
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }

                // Determine additions and removals
                var additions = new HashSet<string>(_favoriteNames.Except(mainFavorites), StringComparer.OrdinalIgnoreCase);
                var removals = new HashSet<string>(mainFavorites.Except(_favoriteNames), StringComparer.OrdinalIgnoreCase);

                // Merge with existing shadow changes
                additions.UnionWith(_shadowChanges);
                removals.UnionWith(_shadowRemovals);

                var shadowData = new ShadowFavoritesData
                {
                    Additions = additions.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                    Removals = removals.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                };

                string shadowJson = JsonSerializer.Serialize(shadowData, new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = JsonSourceGenerationContext.Default });
                File.WriteAllText(_shadowFilePath, shadowJson);

                _shadowChanges = additions;
                _shadowRemovals = removals;
            }
            catch (Exception)
            {
            }
        }

        private void TryMergeShadowFile()
        {
            try
            {
                if (File.Exists(_shadowFilePath))
                {
                    // Shadow file exists and main file is now writable, delete shadow
                    File.Delete(_shadowFilePath);
                    _shadowChanges.Clear();
                    _shadowRemovals.Clear();
                }
            }
            catch (Exception)
            {
            }
        }

        public void ReloadFavorites()
        {
            _isLoaded = false;
            LoadFavorites();
        }
    }
}

