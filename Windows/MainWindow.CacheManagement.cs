using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace VPM
{
    public partial class MainWindow
    {
        /// <summary>
        /// Opens the cache folder in Windows Explorer
        /// Contains both PackageMetadata.cache and PackageImages.cache
        /// </summary>
        private void OpenCacheFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cacheDir = _packageManager.GetCacheDirectory();
                
                if (!Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }
                
                Process.Start(new ProcessStartInfo
                {
                    FileName = cacheDir,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error opening cache folder: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Clears all metadata cache and reloads packages
        /// </summary>
        private void ClearAllMetadataCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show(
                    "Clear all metadata cache? This will force all packages to be re-parsed on next load.\n\nThe application will need to be restarted for changes to take effect.",
                    "Clear Metadata Cache",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.OK)
                {
                    _packageManager.ClearMetadataCache();
                    MessageBox.Show(
                        "Metadata cache cleared successfully.\n\nPlease restart the application to reload packages with fresh metadata.",
                        "Cache Cleared",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error clearing metadata cache: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}

