using System;
using System.Collections.Generic;
using System.IO;

namespace VPM.Services
{
    /// <summary>
    /// Generic manager for file-based markers (.fav, .hide files)
    /// Consolidates SceneFavoritesManager and SceneHideManager functionality
    /// </summary>
    public class FileMarkerManager
    {
        private readonly string _baseDirectory;
        private readonly string _markerExtension;
        private HashSet<string> _markedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler MarkersChanged;

        public FileMarkerManager(string baseDirectory, string markerExtension)
        {
            _baseDirectory = baseDirectory;
            _markerExtension = markerExtension.StartsWith(".") ? markerExtension : "." + markerExtension;
        }

        /// <summary>
        /// Loads all markers from the base directory
        /// </summary>
        public void LoadMarkers()
        {
            _markedPaths.Clear();

            try
            {
                if (!Directory.Exists(_baseDirectory))
                    return;

                var markerFiles = SymlinkSafeFileSystem.EnumerateFilesSafe(_baseDirectory, "*" + _markerExtension, true);
                foreach (var markerFile in markerFiles)
                {
                    // The marker file marks the original file (e.g., scene1.json.fav marks scene1.json)
                    string originalFilePath = markerFile.Substring(0, markerFile.Length - _markerExtension.Length);
                    _markedPaths.Add(originalFilePath);
                }
            }
            catch (Exception)
            {
                // Silently handle errors
            }
        }

        /// <summary>
        /// Checks if a file is marked
        /// </summary>
        public bool IsMarked(string filePath)
        {
            return _markedPaths.Contains(filePath);
        }

        /// <summary>
        /// Adds a marker for a file
        /// </summary>
        public void AddMarker(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return;

                string markerFilePath = filePath + _markerExtension;

                if (!File.Exists(markerFilePath))
                {
                    File.Create(markerFilePath).Dispose();
                    _markedPaths.Add(filePath);
                    MarkersChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception)
            {
                // Silently handle errors
            }
        }

        /// <summary>
        /// Removes a marker from a file
        /// </summary>
        public void RemoveMarker(string filePath)
        {
            try
            {
                string markerFilePath = filePath + _markerExtension;

                if (File.Exists(markerFilePath))
                {
                    File.Delete(markerFilePath);
                    _markedPaths.Remove(filePath);
                    MarkersChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception)
            {
                // Silently handle errors
            }
        }

        /// <summary>
        /// Toggles a marker for a file
        /// </summary>
        public void ToggleMarker(string filePath)
        {
            if (IsMarked(filePath))
                RemoveMarker(filePath);
            else
                AddMarker(filePath);
        }

        /// <summary>
        /// Adds markers for multiple files
        /// </summary>
        public void AddMarkerBatch(IEnumerable<string> filePaths)
        {
            foreach (var filePath in filePaths)
            {
                AddMarker(filePath);
            }
        }

        /// <summary>
        /// Removes markers from multiple files
        /// </summary>
        public void RemoveMarkerBatch(IEnumerable<string> filePaths)
        {
            foreach (var filePath in filePaths)
            {
                RemoveMarker(filePath);
            }
        }

        /// <summary>
        /// Gets all marked file paths
        /// </summary>
        public HashSet<string> GetAllMarked()
        {
            return new HashSet<string>(_markedPaths, StringComparer.OrdinalIgnoreCase);
        }
    }
}
