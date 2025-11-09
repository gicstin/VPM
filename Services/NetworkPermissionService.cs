using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Service for managing network access permissions
    /// Provides a centralized way to check and request network permissions
    /// </summary>
    public class NetworkPermissionService
    {
        private readonly SettingsManager _settingsManager;
        private readonly SemaphoreSlim _dialogSemaphore = new SemaphoreSlim(1, 1);
        private Task<(bool granted, bool updateDatabase)> _pendingDialogTask;
        private bool _databaseUpdateOfferedThisSession = false;
        private readonly string _localDbPath;

        public NetworkPermissionService(SettingsManager settingsManager)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _localDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VAMPackageDatabase.bin");
        }

        /// <summary>
        /// Checks if network access is allowed. If permission hasn't been asked,
        /// shows a confirmation dialog to the user.
        /// </summary>
        /// <returns>True if network access is granted, false otherwise</returns>
        public async Task<bool> RequestNetworkAccessAsync()
        {
            var result = await RequestNetworkAccessWithOptionsAsync();
            return result.granted;
        }
        
        /// <summary>
        /// Checks if network access is allowed and returns additional options.
        /// Shows dialog unless "Never show again" was checked with granted permission.
        /// </summary>
        /// <param name="offerDatabaseUpdate">If true, shows the "Update database" checkbox (only once per session)</param>
        /// <returns>Tuple with (granted, updateDatabase)</returns>
        public async Task<(bool granted, bool updateDatabase)> RequestNetworkAccessWithOptionsAsync(bool offerDatabaseUpdate = false)
        {
            // Check for offline mode first - if .bin file exists, skip network permission popup
            if (IsOfflineModeAvailable())
            {
                Console.WriteLine("[NetworkPermission] Offline database detected, skipping network permission popup");
                return (true, false); // Grant access without showing popup (offline mode)
            }
            
            // Use semaphore to ensure only one dialog is shown at a time
            // This must be BEFORE checking settings to prevent race conditions
            await _dialogSemaphore.WaitAsync();
            try
            {
                // Re-check settings inside the lock to get latest values
                var settings = _settingsManager.Settings;
                
                // If "Never show again" is set AND permission was granted
                if (settings.NeverShowNetworkPermissionDialog && settings.NetworkPermissionGranted)
                {
                    // If offering database update for the first time, auto-approve it
                    if (offerDatabaseUpdate && !_databaseUpdateOfferedThisSession)
                    {
                        _databaseUpdateOfferedThisSession = true;
                        return (true, true); // Auto-approve database update
                    }
                    // Otherwise just return granted without update
                    return (true, false);
                }
                
                // If "Never show again" is set but permission was denied, still return denied
                if (settings.NeverShowNetworkPermissionDialog && !settings.NetworkPermissionGranted)
                {
                    return (false, false);
                }
                
                // If permission is already granted and we're not offering database update, return immediately
                if (settings.NetworkPermissionGranted && !offerDatabaseUpdate)
                {
                    return (true, false);
                }
                
                // If permission is granted and database update was already offered this session, return immediately
                if (settings.NetworkPermissionGranted && offerDatabaseUpdate && _databaseUpdateOfferedThisSession)
                {
                    return (true, false);
                }
                
                // Check if there's already a pending dialog task
                if (_pendingDialogTask != null && !_pendingDialogTask.IsCompleted)
                {
                    // Wait for the existing dialog to complete and return its result
                    return await _pendingDialogTask;
                }
                
                // Create new dialog task
                _pendingDialogTask = ShowNetworkPermissionDialogAsync();
                var result = await _pendingDialogTask;
                
                // Mark that database update was offered this session
                if (offerDatabaseUpdate)
                {
                    _databaseUpdateOfferedThisSession = true;
                }
                
                // Clear the pending task after completion
                _pendingDialogTask = null;
                
                return result;
            }
            finally
            {
                _dialogSemaphore.Release();
            }
        }

        /// <summary>
        /// Shows the network permission dialog and processes the user's response
        /// </summary>
        /// <returns>Tuple with (granted, updateDatabase)</returns>
        private async Task<(bool granted, bool updateDatabase)> ShowNetworkPermissionDialogAsync()
        {
            bool permissionGranted = false;
            bool neverShowAgain = false;
            bool updateDatabase = false;

            // Show dialog on UI thread
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new NetworkPermissionDialog();
                var result = dialog.ShowDialog();

                if (result == true)
                {
                    permissionGranted = true;
                    neverShowAgain = dialog.NeverShowAgain;
                    updateDatabase = dialog.UpdateDatabase;
                }
                else
                {
                    permissionGranted = false;
                    neverShowAgain = dialog.NeverShowAgain;
                    updateDatabase = false;
                }
            });

            // Save the user's choice
            _settingsManager.Settings.NetworkPermissionGranted = permissionGranted;
            
            // Only allow "Never show again" if permission was granted
            if (permissionGranted && neverShowAgain)
            {
                _settingsManager.Settings.NeverShowNetworkPermissionDialog = true;
            }
            else
            {
                _settingsManager.Settings.NeverShowNetworkPermissionDialog = false;
            }
            
            _settingsManager.SaveSettingsImmediate();

            return (permissionGranted, updateDatabase);
        }

        /// <summary>
        /// Checks if network access is currently allowed without showing any dialogs
        /// </summary>
        /// <returns>True if network access is granted, false otherwise</returns>
        public bool IsNetworkAccessAllowed()
        {
            var settings = _settingsManager.Settings;
            return settings.NetworkPermissionGranted;
        }

        /// <summary>
        /// Resets network permission settings (useful for testing or user preference reset)
        /// </summary>
        public void ResetNetworkPermission()
        {
            _settingsManager.Settings.NetworkPermissionGranted = false;
            _settingsManager.Settings.NeverShowNetworkPermissionDialog = false;
            _settingsManager.SaveSettingsImmediate();
        }

        /// <summary>
        /// Checks if offline mode is available by detecting the presence of the local database file
        /// </summary>
        /// <returns>True if the .bin file exists in the app folder, false otherwise</returns>
        private bool IsOfflineModeAvailable()
        {
            return File.Exists(_localDbPath);
        }
    }
}

