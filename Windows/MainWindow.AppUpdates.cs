using System;
using System.Threading.Tasks;
using System.Windows;
using VPM.Services;

namespace VPM
{
    public partial class MainWindow
    {
        private AppUpdateChecker _appUpdateChecker;

        /// <summary>
        /// Checks for application updates from GitHub
        /// </summary>
        public async Task CheckForAppUpdatesAsync(bool force = false)
        {
            try
            {
                // Check if updates are enabled in settings (unless forced)
                if (!force && _settingsManager?.Settings?.CheckForAppUpdates != true)
                {
                    return;
                }

                if (_appUpdateChecker == null)
                {
                    _appUpdateChecker = new AppUpdateChecker();
                }

                // Run checks in background
                var vpmTask = Task.Run(() => _appUpdateChecker.CheckForUpdatesAsync());
                
                Task<VpbPluginCheckResult> vpbTask = null;
                if (!string.IsNullOrEmpty(_selectedFolder))
                {
                    var branch = _settingsManager?.Settings?.VpbPreferredBranch is { Length: > 0 } b ? b : "main";
                    vpbTask = Task.Run(async () =>
                    {
                        using var checker = new VpbPluginChecker();
                        return await checker.CheckAsync(_selectedFolder, branch);
                    });
                }

                await Task.WhenAll(new Task[] { vpmTask, vpbTask ?? Task.CompletedTask });
                
                var vpmResult = await vpmTask;
                var vpbResult = vpbTask != null ? await vpbTask : new VpbPluginCheckResult { IsInstalled = false };

                // Logic to decide if we show the window
                // Show if forced, or if ANY update is available
                bool showWindow = force || vpmResult.IsUpdateAvailable || (vpbResult != null && vpbResult.IsUpdateAvailable);

                if (showWindow)
                {
                    // Update UI on main thread
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var overview = new VPM.Windows.UpdateOverviewWindow
                        {
                            Owner = this
                        };

                        // Bind overview UI to the same settings instance as the main window
                        // so the checkbox stays in sync with Update Settings menu.
                        overview.DataContext = _settingsManager?.Settings;
                        
                        overview.SetVpmStatus(vpmResult);
                        
                        // Only set VPB status if we actually checked it (folder selected)
                        // If we didn't check (vpbTask was null), IsInstalled is false, so it shows "Not Installed"
                        // This is acceptable behavior
                        overview.SetVpbStatus(vpbResult);
                        
                        overview.ShowDialog();
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking for updates: {ex.Message}");
                if (force)
                {
                    Dispatcher.Invoke(() =>
                    {
                        CustomMessageBox.Show(
                            $"Failed to check for updates: {ex.Message}",
                            "Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    });
                }
            }
        }

        private async void ForceCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            await CheckForAppUpdatesAsync(true);
        }
    }
}
