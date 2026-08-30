using System;
using System.Linq;
using System.Windows;
using VPM.Models;
using VPM.Windows;

namespace VPM
{
    /// <summary>
    /// Hub-related functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        private HubBrowserWindow _hubBrowserWindow;

        /// <summary>
        /// Opens the Hub Browser window
        /// </summary>
        private void HubBrowser_Click(object sender, RoutedEventArgs e)
        {
            OpenHubBrowser(HubWorkView.Browse);
        }

        internal void OpenHubBrowser(HubWorkView view)
        {
            try
            {
                var destinationFolder = GetHubDownloadFolder();
                var localPackagePaths = BuildHubLocalPackagePaths();

                if (_hubBrowserWindow != null)
                {
                    _hubBrowserWindow.SyncLocalPackages(localPackagePaths);
                    _hubBrowserWindow.ShowWorkView(view);
                    return;
                }

                _hubBrowserWindow = new HubBrowserWindow(destinationFolder, localPackagePaths, _packageManager, _settingsManager, _imageManager);
                _hubBrowserWindow.Owner = this;
                _hubBrowserWindow.LibraryRefreshNeeded += HubBrowser_LibraryRefreshNeeded;
                _hubBrowserWindow.Closed += HubBrowser_Closed;
                _hubBrowserWindow.Show();
                if (view != HubWorkView.Browse)
                    _hubBrowserWindow.ShowWorkView(view);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open Hub Browser:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private System.Collections.Generic.Dictionary<string, string> BuildHubLocalPackagePaths()
        {
            var localPackagePaths = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_packageManager?.PackageMetadata != null)
            {
                foreach (var metadata in _packageManager.PackageMetadata.Values)
                {
                    if (metadata.Status != "Loaded" && metadata.Status != "Available")
                        continue;

                    if (!string.IsNullOrEmpty(metadata.FilePath))
                    {
                        var name = System.IO.Path.GetFileNameWithoutExtension(metadata.FilePath);
                        if (!string.IsNullOrEmpty(name) && !localPackagePaths.ContainsKey(name))
                            localPackagePaths[name] = metadata.FilePath;
                    }
                }
            }

            return localPackagePaths;
        }

        private void HubBrowser_Closed(object sender, EventArgs e)
        {
            if (_hubBrowserWindow != null)
            {
                _hubBrowserWindow.LibraryRefreshNeeded -= HubBrowser_LibraryRefreshNeeded;
                _hubBrowserWindow.Closed -= HubBrowser_Closed;
            }
            _hubBrowserWindow = null;
            RefreshPackagesAfterHubDownload();
        }

        private void HubBrowser_LibraryRefreshNeeded(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(RefreshPackagesAfterHubDownload), System.Windows.Threading.DispatcherPriority.Background);
        }

        internal void CloseHubBrowserForExit()
        {
            if (_hubBrowserWindow == null)
                return;

            _hubBrowserWindow.PrepareForAppExit();
            _hubBrowserWindow.Close();
            _hubBrowserWindow = null;
        }

        private void HubCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            OpenHubBrowser(HubWorkView.Updates);
        }

        /// <summary>
        /// Find and download missing dependencies from Hub
        /// </summary>
        private void HubMissingDeps_Click(object sender, RoutedEventArgs e)
        {
            OpenHubBrowser(HubWorkView.Missing);
        }

        /// <summary>
        /// Get the folder where Hub downloads should be saved
        /// </summary>
        private string GetHubDownloadFolder()
        {
            if (!string.IsNullOrEmpty(_settingsManager?.Settings?.SelectedFolder))
            {
                var addonPackages = System.IO.Path.Combine(_settingsManager.Settings.SelectedFolder, "AddonPackages");
                if (System.IO.Directory.Exists(addonPackages))
                    return addonPackages;

                var allPackages = System.IO.Path.Combine(_settingsManager.Settings.SelectedFolder, "AllPackages");
                if (System.IO.Directory.Exists(allPackages))
                    return allPackages;

                System.IO.Directory.CreateDirectory(addonPackages);
                return addonPackages;
            }

            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
        }

        /// <summary>
        /// Refresh packages after downloading from Hub
        /// </summary>
        private void RefreshPackagesAfterHubDownload()
        {
            try
            {
                SetStatus("Refreshing packages after Hub download...");
                RefreshPackages();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow.Hub] Error refreshing after Hub download: {ex.Message}");
                SetStatus("Ready - refresh to see new packages");
            }
        }

        /// <summary>
        /// Extract version number from package name
        /// </summary>
        private static int ExtractVersion(string packageName)
        {
            var name = packageName?.Replace(".var", "") ?? "";

            for (int i = name.Length - 1; i >= 0; i--)
            {
                if (name[i] == '.')
                {
                    if (i + 1 < name.Length)
                    {
                        var afterDot = name.Substring(i + 1);
                        if (int.TryParse(afterDot, out var version))
                            return version;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// Get package group name (without version)
        /// </summary>
        private static string GetPackageGroupName(string packageName)
        {
            var name = packageName?.Replace(".var", "") ?? "";

            for (int i = name.Length - 1; i >= 0; i--)
            {
                if (name[i] == '.')
                {
                    if (i + 1 < name.Length)
                    {
                        var afterDot = name.Substring(i + 1);
                        if (int.TryParse(afterDot, out _))
                            return name.Substring(0, i);
                    }
                }
            }

            return name;
        }
    }
}
