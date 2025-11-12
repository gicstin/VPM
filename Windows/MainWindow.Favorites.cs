using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    public partial class MainWindow
    {
        private FavoritesManager _favoritesManager;
        private SceneFavoritesManager _sceneFavoritesManager;
        private SceneHideManager _sceneHideManager;

        private void InitializeFavoritesManager()
        {
            if (!string.IsNullOrEmpty(_settingsManager.Settings.SelectedFolder))
            {
                _favoritesManager = new FavoritesManager(_settingsManager.Settings.SelectedFolder);
                _favoritesManager.LoadFavorites();
                
                // Subscribe to favorites changes (when game modifies the file)
                _favoritesManager.FavoritesChanged += OnFavoritesChanged;
                
                if (_filterManager != null)
                {
                    _filterManager.FavoritesManager = _favoritesManager;
                }
                
                // Initialize scene favorites manager
                string savesPath = Path.Combine(_settingsManager.Settings.SelectedFolder, "Saves");
                _sceneFavoritesManager = new SceneFavoritesManager(savesPath);
                _sceneFavoritesManager.LoadFavorites();
                
                // Initialize scene hide manager
                _sceneHideManager = new SceneHideManager(savesPath);
                _sceneHideManager.LoadHidden();
                
                UpdateFavoritesInPackages();
            }
        }

        private void OnFavoritesChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateFavoritesInPackages();
                
                if (_reactiveFilterManager != null)
                {
                    UpdateFilterCountsLive();
                }
            }));
        }

        private void UpdateFavoritesInPackages()
        {
            if (_favoritesManager == null) return;

            foreach (var package in Packages)
            {
                package.IsFavorite = _favoritesManager.IsFavorite(package.Name);
            }
        }

        private void FavoriteToggleButton_Click(object sender, RoutedEventArgs e)
        {
            // Handle presets favorites
            if (_currentContentMode == "Presets")
            {
                var selectedItems = CustomAtomDataGrid?.SelectedItems?.Cast<CustomAtomItem>()?.ToList() ?? new List<CustomAtomItem>();
                if (selectedItems.Count == 0)
                    return;

                foreach (var item in selectedItems)
                {
                    var favPath = item.FilePath + ".fav";
                    if (!File.Exists(favPath))
                    {
                        File.Create(favPath).Dispose();
                        item.IsFavorite = true;
                    }
                }

                SetStatus($"Added {selectedItems.Count} custom atom item(s) to favorites");
                return;
            }

            // Handle scene favorites
            if (_currentContentMode == "Scenes")
            {
                if (_sceneFavoritesManager == null)
                    return;

                var selectedScenes = ScenesDataGrid.SelectedItems.Cast<SceneItem>().ToList();
                if (selectedScenes.Count == 0)
                    return;

                if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                {
                    // Could open a favorites folder for scenes if needed
                    return;
                }

                _sceneFavoritesManager.AddFavoriteBatch(selectedScenes.Select(s => s.FilePath));
                
                foreach (var scene in selectedScenes)
                {
                    scene.IsFavorite = true;
                }

                SetStatus($"Added {selectedScenes.Count} scene(s) to favorites");
                return;
            }

            // Handle package favorites
            if (_favoritesManager == null)
            {
                return;
            }

            var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>().ToList();
            if (selectedPackages.Count == 0)
            {
                return;
            }

            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                OpenFavoritesFile();
                return;
            }

            ExecuteWithPreservedSelections(() =>
            {
                _favoritesManager.AddFavoriteBatch(selectedPackages.Select(p => p.Name));
                
                foreach (var package in selectedPackages)
                {
                    package.IsFavorite = true;
                }

                bool favoritesFilterActive = _filterManager?.SelectedFavoriteStatuses?.Count > 0;
                
                if (favoritesFilterActive)
                {
                    RefreshFilterLists();
                    ApplyFilters();
                }
                else
                {
                    if (_reactiveFilterManager != null)
                    {
                        UpdateFilterCountsLive();
                    }
                }
            });
        }

        private void FavoriteToggleButton_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Handle presets favorites removal
            if (_currentContentMode == "Presets")
            {
                var selectedItems = CustomAtomDataGrid?.SelectedItems?.Cast<CustomAtomItem>()?.ToList() ?? new List<CustomAtomItem>();
                if (selectedItems.Count == 0)
                    return;

                foreach (var item in selectedItems)
                {
                    var favPath = item.FilePath + ".fav";
                    if (File.Exists(favPath))
                    {
                        File.Delete(favPath);
                        item.IsFavorite = false;
                    }
                }

                SetStatus($"Removed {selectedItems.Count} custom atom item(s) from favorites");
                e.Handled = true;
                return;
            }

            // Handle scene favorites removal
            if (_currentContentMode == "Scenes")
            {
                if (_sceneFavoritesManager == null)
                    return;

                var selectedScenes = ScenesDataGrid.SelectedItems.Cast<SceneItem>().ToList();
                if (selectedScenes.Count == 0)
                    return;

                _sceneFavoritesManager.RemoveFavoriteBatch(selectedScenes.Select(s => s.FilePath));
                
                foreach (var scene in selectedScenes)
                {
                    scene.IsFavorite = false;
                }

                SetStatus($"Removed {selectedScenes.Count} scene(s) from favorites");
                e.Handled = true;
                return;
            }

            // Handle package favorites removal
            if (_favoritesManager == null)
            {
                return;
            }

            var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>().ToList();
            if (selectedPackages.Count == 0)
            {
                return;
            }

            ExecuteWithPreservedSelections(() =>
            {
                _favoritesManager.RemoveFavoriteBatch(selectedPackages.Select(p => p.Name));
                
                foreach (var package in selectedPackages)
                {
                    package.IsFavorite = false;
                }

                bool favoritesFilterActive = _filterManager?.SelectedFavoriteStatuses?.Count > 0;
                
                if (favoritesFilterActive)
                {
                    RefreshFilterLists();
                    ApplyFilters();
                }
                else
                {
                    if (_reactiveFilterManager != null)
                    {
                        UpdateFilterCountsLive();
                    }
                }
            });

            e.Handled = true;
        }

        private void HideToggleButton_Click(object sender, RoutedEventArgs e)
        {
            // Handle presets hide
            if (_currentContentMode == "Presets")
            {
                var selectedItems = CustomAtomDataGrid?.SelectedItems?.Cast<CustomAtomItem>()?.ToList() ?? new List<CustomAtomItem>();
                if (selectedItems.Count == 0)
                    return;

                foreach (var item in selectedItems)
                {
                    var hidePath = item.FilePath + ".hide";
                    if (!File.Exists(hidePath))
                    {
                        File.Create(hidePath).Dispose();
                        item.IsHidden = true;
                    }
                }

                SetStatus($"Hidden {selectedItems.Count} custom atom item(s)");
                return;
            }

            // Handle scene hide
            if (_currentContentMode == "Scenes")
            {
                if (_sceneHideManager == null)
                    return;

                var selectedScenes = ScenesDataGrid.SelectedItems.Cast<SceneItem>().ToList();
                if (selectedScenes.Count == 0)
                    return;

                _sceneHideManager.AddHiddenBatch(selectedScenes.Select(s => s.FilePath));
                
                foreach (var scene in selectedScenes)
                {
                    scene.IsHidden = true;
                }

                SetStatus($"Hidden {selectedScenes.Count} scene(s)");
                return;
            }
        }

        private void HideToggleButton_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Handle presets hide removal
            if (_currentContentMode == "Presets")
            {
                var selectedItems = CustomAtomDataGrid?.SelectedItems?.Cast<CustomAtomItem>()?.ToList() ?? new List<CustomAtomItem>();
                if (selectedItems.Count == 0)
                    return;

                foreach (var item in selectedItems)
                {
                    var hidePath = item.FilePath + ".hide";
                    if (File.Exists(hidePath))
                    {
                        File.Delete(hidePath);
                        item.IsHidden = false;
                    }
                }

                SetStatus($"Unhidden {selectedItems.Count} custom atom item(s)");
                e.Handled = true;
                return;
            }

            // Handle scene hide removal
            if (_currentContentMode == "Scenes")
            {
                if (_sceneHideManager == null)
                    return;

                var selectedScenes = ScenesDataGrid.SelectedItems.Cast<SceneItem>().ToList();
                if (selectedScenes.Count == 0)
                    return;

                _sceneHideManager.RemoveHiddenBatch(selectedScenes.Select(s => s.FilePath));
                
                foreach (var scene in selectedScenes)
                {
                    scene.IsHidden = false;
                }

                SetStatus($"Unhidden {selectedScenes.Count} scene(s)");
                e.Handled = true;
                return;
            }
        }

        private void OpenFavoritesFile()
        {
            try
            {
                string favoritesPath = Path.Combine(_settingsManager.Settings.SelectedFolder, "Custom", "PluginData", "sfishere", "Favorites.txt");
                
                if (File.Exists(favoritesPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select, \"{favoritesPath}\"",
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception)
            {
            }
        }

        private void OptimizeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            OptimizeSelectedToolbar_Click(sender, e);
        }

        private void UpdateOptimizeCounter()
        {
            if (OptimizeCountText == null) return;

            int optimizeableCount = 0;
            
            // Check if we're in scene mode or package mode
            if (_currentContentMode == "Scenes")
            {
                // Count selected scenes
                if (ScenesDataGrid?.SelectedItems != null)
                {
                    foreach (var item in ScenesDataGrid.SelectedItems)
                    {
                        if (item is SceneItem scene)
                        {
                            optimizeableCount++;
                        }
                    }
                }
            }
            else
            {
                // Count selected packages
                if (PackageDataGrid?.SelectedItems != null)
                {
                    foreach (var item in PackageDataGrid.SelectedItems)
                    {
                        if (item is PackageItem package)
                        {
                            optimizeableCount++;
                        }
                    }
                }
            }

            if (optimizeableCount > 0)
            {
                OptimizeCountText.Text = $"({optimizeableCount})";
            }
            else
            {
                OptimizeCountText.Text = "";
            }
        }
    }
}

