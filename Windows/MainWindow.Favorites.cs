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

