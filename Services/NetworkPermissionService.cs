using System;
using System.IO;
using System.Threading.Tasks;
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
        private bool _databaseUpdateOfferedThisSession = false;
        private readonly string _localDbPath;

        public NetworkPermissionService(SettingsManager settingsManager)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            
            // Check for VPM.bin first, then fall back to VAMPackageDatabase.bin
            string vpmBinPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VPM.bin");
            string legacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VAMPackageDatabase.bin");
            
            _localDbPath = File.Exists(vpmBinPath) ? vpmBinPath : legacyPath;
        }

        /// <summary>
        /// Checks if network access is allowed. Always grants access silently without showing dialogs.
        /// </summary>
        /// <param name="forceShowDialog">Deprecated - kept for compatibility but ignored</param>
        /// <returns>True if network access is granted (always true)</returns>
        public async Task<bool> RequestNetworkAccessAsync(bool forceShowDialog = false)
        {
            var result = await RequestNetworkAccessWithOptionsAsync(forceShowDialog: forceShowDialog);
            return result.granted;
        }
        
        /// <summary>
        /// Checks if network access is allowed and returns additional options.
        /// Always grants access silently without showing any dialogs.
        /// </summary>
        /// <param name="offerDatabaseUpdate">If true, auto-approves database update</param>
        /// <param name="forceShowDialog">Deprecated - kept for compatibility but ignored</param>
        /// <returns>Tuple with (granted=true, updateDatabase)</returns>
        public async Task<(bool granted, bool updateDatabase)> RequestNetworkAccessWithOptionsAsync(bool offerDatabaseUpdate = false, bool forceShowDialog = false)
        {
            // Always grant network access silently - no dialogs shown
            
            // Auto-approve database update if offered (only once per session)
            bool updateDatabase = false;
            if (offerDatabaseUpdate && !_databaseUpdateOfferedThisSession)
            {
                _databaseUpdateOfferedThisSession = true;
                updateDatabase = true;
            }
            
            // Update settings to reflect that network permission was granted
            _settingsManager.Settings.NetworkPermissionGranted = true;
            _settingsManager.Settings.NeverShowNetworkPermissionDialog = true;
            _settingsManager.SaveSettingsImmediate();
            
            return await Task.FromResult((true, updateDatabase));
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

