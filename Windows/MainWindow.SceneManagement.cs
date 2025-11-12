using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    /// <summary>
    /// Scene management functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>
        /// Loads all user scenes from the Saves/scene folder
        /// </summary>
        public async Task LoadScenesAsync()
        {
            if (_sceneScanner == null)
                return;

            try
            {
                await Task.Run(() =>
                {
                    var scenes = _sceneScanner.ScanLocalScenes();
                    
                    // Check if each scene has been optimized, marked as favorite, or hidden
                    foreach (var scene in scenes)
                    {
                        scene.IsOptimized = IsSceneOptimized(scene.FilePath);
                        if (_sceneFavoritesManager != null)
                        {
                            scene.IsFavorite = _sceneFavoritesManager.IsFavorite(scene.FilePath);
                        }
                        if (_sceneHideManager != null)
                        {
                            scene.IsHidden = _sceneHideManager.IsHidden(scene.FilePath);
                        }
                    }
                    
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        Scenes.ReplaceAll(scenes);
                        
                        // Populate scene filters if in scene mode
                        if (_currentContentMode == "Scenes")
                        {
                            PopulateSceneTypeFilter();
                            PopulateSceneCreatorFilter();
                            PopulateSceneSourceFilter();
                            PopulateSceneDateFilter();
                            PopulateSceneFileSizeFilter();
                            PopulateSceneStatusFilter();
                        }
                    });
                });
            }
            catch
            {
                // Error loading scenes - silently handled
            }
        }

        /// <summary>
        /// Refreshes the scene list
        /// </summary>
        public async Task RefreshScenesAsync()
        {
            await LoadScenesAsync();
        }

        /// <summary>
        /// Gets the total count of scenes
        /// </summary>
        public int GetSceneCount()
        {
            return Scenes?.Count ?? 0;
        }

        /// <summary>
        /// Filters scenes based on search text
        /// </summary>
        public void FilterScenes(string searchText)
        {
            if (ScenesView == null)
                return;

            if (string.IsNullOrWhiteSpace(searchText))
            {
                ScenesView.Filter = null;
            }
            else
            {
                // Use ContainsSearch for partial term matching across all searchboxes
                // No string allocations - StringComparison.OrdinalIgnoreCase is faster than ToLowerInvariant()
                ScenesView.Filter = obj =>
                {
                    if (obj is SceneItem scene)
                    {
                        return VPM.Services.SearchHelper.ContainsSearch(scene.DisplayName, searchText) ||
                               VPM.Services.SearchHelper.ContainsSearch(scene.Creator, searchText) ||
                               VPM.Services.SearchHelper.ContainsSearch(scene.SceneType, searchText);
                    }
                    return true;
                };
            }
        }

        /// <summary>
        /// Filters scenes by creator
        /// </summary>
        public void FilterScenesByCreator(string creator)
        {
            if (ScenesView == null)
                return;

            if (string.IsNullOrWhiteSpace(creator))
            {
                ScenesView.Filter = null;
            }
            else
            {
                ScenesView.Filter = obj =>
                {
                    if (obj is SceneItem scene)
                    {
                        return scene.Creator.Equals(creator, StringComparison.OrdinalIgnoreCase);
                    }
                    return true;
                };
            }
        }

        /// <summary>
        /// Filters scenes by type
        /// </summary>
        public void FilterScenesByType(string sceneType)
        {
            if (ScenesView == null)
                return;

            if (string.IsNullOrWhiteSpace(sceneType))
            {
                ScenesView.Filter = null;
            }
            else
            {
                ScenesView.Filter = obj =>
                {
                    if (obj is SceneItem scene)
                    {
                        return scene.SceneType.Equals(sceneType, StringComparison.OrdinalIgnoreCase);
                    }
                    return true;
                };
            }
        }

        /// <summary>
        /// Gets unique creators from all scenes
        /// </summary>
        public List<string> GetSceneCreators()
        {
            return Scenes
                .Where(s => !string.IsNullOrEmpty(s.Creator))
                .Select(s => s.Creator)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c)
                .ToList();
        }

        /// <summary>
        /// Gets unique scene types from all scenes
        /// </summary>
        public List<string> GetSceneTypes()
        {
            return Scenes
                .Where(s => !string.IsNullOrEmpty(s.SceneType))
                .Select(s => s.SceneType)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t)
                .ToList();
        }
    }
}

