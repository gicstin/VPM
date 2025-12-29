using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    /// <summary>
    /// Hub Overview panel functionality for MainWindow.
    /// Displays the Hub overview page for a selected package using WebView2.
    /// </summary>
    public partial class MainWindow
    {
        #region Hub Overview Fields
        
        private bool _hubOverviewWebViewInitialized = false;
        private string _currentHubResourceId = null;
        private string _currentHubPackageName = null;
        private string _currentHubOverviewUrl = null;
        private CancellationTokenSource _hubOverviewCts;
        private bool _isClearing = false; // Track when we're intentionally clearing the WebView
        // Note: _hubService is defined in MainWindow.PackageUpdates.cs and shared across partial classes
        
        #endregion
        
        #region Hub Overview Initialization
        
        /// <summary>
        /// Initialize WebView2 for Hub Overview panel
        /// </summary>
        private async Task InitializeHubOverviewWebViewAsync()
        {
            if (_hubOverviewWebViewInitialized) return;
            
            try
            {
                // Use the same user data folder as HubBrowserWindow to share cache, cookies, and login sessions
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VPM",
                    "WebView2",
                    "v1");

                Directory.CreateDirectory(userDataFolder);
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await HubOverviewWebView.EnsureCoreWebView2Async(env);
                
                // Configure WebView2 settings
                var settings = HubOverviewWebView.CoreWebView2.Settings;
                settings.IsStatusBarEnabled = false;
                settings.AreDefaultContextMenusEnabled = true;
                settings.IsZoomControlEnabled = true;
                settings.AreDevToolsEnabled = false;
                
                // Set dark theme preference
                HubOverviewWebView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
                
                // Add Hub consent cookie
                var cookieManager = HubOverviewWebView.CoreWebView2.CookieManager;
                var cookie = cookieManager.CreateCookie("vamhubconsent", "1", ".virtamate.com", "/");
                cookie.IsSecure = true;
                cookieManager.AddOrUpdateCookie(cookie);
                
                // Handle navigation events
                HubOverviewWebView.NavigationStarting += HubOverviewWebView_NavigationStarting;
                HubOverviewWebView.NavigationCompleted += HubOverviewWebView_NavigationCompleted;
                HubOverviewWebView.CoreWebView2.DOMContentLoaded += HubOverviewWebView_DOMContentLoaded;
                
                // Inject CSS to improve dark theme appearance (persistent script)
                await InjectHubOverviewDarkThemeStyles();

                _hubOverviewWebViewInitialized = true;
            }
            catch (Exception ex)
            {
                _hubOverviewWebViewInitialized = false;
                ShowHubOverviewError($"WebView2 initialization failed: {ex.Message}");
            }
        }
        
        private void HubOverviewWebView_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            HubOverviewLoadingOverlay.Visibility = Visibility.Visible;
            HubOverviewErrorPanel.Visibility = Visibility.Collapsed;
        }

        private void HubOverviewWebView_DOMContentLoaded(object sender, CoreWebView2DOMContentLoadedEventArgs e)
        {
            // Hide loading overlay as soon as DOM is ready (text is visible)
            // No need to wait for all images to load
            HubOverviewLoadingOverlay.Visibility = Visibility.Collapsed;
        }
        
        private void HubOverviewWebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {

            
            // Always hide loading overlay (fallback)
            HubOverviewLoadingOverlay.Visibility = Visibility.Collapsed;
            
            // Ignore navigation events when we're intentionally clearing the WebView
            if (_isClearing)
            {
                _isClearing = false;
                return;
            }
            
            if (!e.IsSuccess)
            {
                ShowHubOverviewError($"Failed to load page: {e.WebErrorStatus}");
            }
            else
            {
                HubOverviewErrorPanel.Visibility = Visibility.Collapsed;
                HubOverviewPlaceholder.Visibility = Visibility.Collapsed;
            }
        }
        
        private async Task InjectHubOverviewDarkThemeStyles()
        {
            try
            {
                // Inject custom CSS to enhance dark theme for Hub pages
                var css = @"
                    body { background-color: #1E1E1E !important; }
                    .p-body { background-color: #1E1E1E !important; }
                    .p-body-inner { background-color: #1E1E1E !important; }
                    .block { background-color: #2D2D2D !important; border-color: #3F3F3F !important; }
                    .block-container { background-color: #2D2D2D !important; }
                    .message { background-color: #2D2D2D !important; }
                    .message-inner { background-color: #2D2D2D !important; }
                ";
                
                var script = $@"
                    (function() {{
                        var style = document.createElement('style');
                        style.textContent = `{css}`;
                        document.head.appendChild(style);
                    }})();
                ";
                
                await HubOverviewWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
            }
            catch (Exception)
            {
                // Ignore CSS injection errors
            }
        }
        
        #endregion
        
        #region Hub Overview Navigation
        
        /// <summary>
        /// Update the Hub Overview tab visibility based on package selection.
        /// Shows the tab only when a single package is selected.
        /// Also restores preferred tab when switching from multi to single selection.
        /// </summary>
        private async void UpdateHubOverviewTabVisibility()
        {
            var selectedCount = PackageDataGrid?.SelectedItems?.Count ?? 0;
            
            if (selectedCount == 1)
            {
                // Show the Hub tab for single selection
                HubOverviewTab.Visibility = Visibility.Visible;
                
                // Restore preferred tab if it was Hub
                if (_settingsManager?.Settings?.PreferredImageAreaTab == "Hub")
                {
                    ImageAreaTabControl.SelectedItem = HubOverviewTab;
                }
                
                // If Hub tab is currently selected, update content for new selection
                if (ImageAreaTabControl.SelectedItem == HubOverviewTab)
                {
                    // Don't clear _currentHubPackageName here - let LoadHubOverviewForSelectedPackageAsync handle caching
                    await LoadHubOverviewForSelectedPackageAsync();
                }
            }
            else
            {
                // Hide the Hub tab for multi-selection or no selection
                HubOverviewTab.Visibility = Visibility.Collapsed;
                
                // If Hub tab was selected, switch to Images tab (but don't change preference)
                if (ImageAreaTabControl.SelectedItem == HubOverviewTab)
                {
                    ImageAreaTabControl.SelectedIndex = 0;
                }
                
                // Clear current Hub state
                _currentHubResourceId = null;
                _currentHubPackageName = null;
            }
        }
        
        /// <summary>
        /// Handle tab selection changes - save preference and load Hub content if needed
        /// </summary>
        private async void ImageAreaTabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Only process if this is the actual tab control change (not bubbled events)
            if (e.AddedItems.Count == 0) return;
            
            // Ignore if the added item is not a TabItem (bubbled event from child controls)
            if (!(e.AddedItems[0] is System.Windows.Controls.TabItem)) return;
            
            // Save the preferred tab when user manually selects
            if (ImageAreaTabControl.SelectedItem == HubOverviewTab)
            {
                if (_settingsManager?.Settings != null)
                {
                    _settingsManager.Settings.PreferredImageAreaTab = "Hub";
                }
                await LoadHubOverviewForSelectedPackageAsync();
            }
            else if (ImageAreaTabControl.SelectedItem == ImagesTab)
            {
                // Only save Images preference if Hub tab is visible (user made a choice)
                if (HubOverviewTab.Visibility == Visibility.Visible && _settingsManager?.Settings != null)
                {
                    _settingsManager.Settings.PreferredImageAreaTab = "Images";
                }
                
                // ALWAYS refresh images when switching to Images tab
                // This ensures images are loaded for the current selection, especially after:
                // - Switching from Hub tab
                // - Selection changes while Hub tab was active
                // - Any other scenario where images might be stale
                await RefreshSelectionDisplaysImmediate();
            }
        }
        
        /// <summary>
        /// Load Hub overview for the currently selected package
        /// </summary>
        private async Task LoadHubOverviewForSelectedPackageAsync()
        {
            // Cancel any pending operation
            _hubOverviewCts?.Cancel();
            _hubOverviewCts?.Dispose();
            _hubOverviewCts = new CancellationTokenSource();
            var token = _hubOverviewCts.Token;
            
            // Get the selected package
            if (PackageDataGrid?.SelectedItems?.Count != 1)
            {
                ShowHubOverviewPlaceholder("Select a single package to view Hub overview");
                return;
            }
            
            var selectedPackage = PackageDataGrid.SelectedItem as PackageItem;
            if (selectedPackage == null)
            {
                ShowHubOverviewPlaceholder("Select a single package to view Hub overview");
                return;
            }
            
            // Extract package group name (without version and .var extension)
            var packageGroupName = GetPackageGroupName(selectedPackage.Name);
            
            // Skip if same package is already loaded AND we have a valid resource
            if (_currentHubPackageName == packageGroupName && _currentHubResourceId != null)
            {

                return;
            }
            

            
            // Clear previous state when switching packages
            _currentHubPackageName = packageGroupName;
            _currentHubResourceId = null;
            
            // Show loading state
            ShowHubOverviewLoading();
            
            try
            {
                // Initialize HubService if needed
                _hubService ??= new HubService();
                
                // Look up the package on Hub by name
                var detail = await _hubService.GetResourceDetailAsync(packageGroupName, isPackageName: true, token);
                
                if (token.IsCancellationRequested) return;
                
                if (detail == null || string.IsNullOrEmpty(detail.ResourceId))
                {
                    ShowHubOverviewPlaceholder($"Hub page not available for:\n{packageGroupName}");
                    return;
                }
                
                // Validate that the returned resource actually matches our package
                if (!ValidateHubResourceMatch(detail, packageGroupName, selectedPackage.Name))
                {
                    ShowHubOverviewPlaceholder($"Hub page not available for:\n{packageGroupName}");
                    return;
                }
                
                _currentHubResourceId = detail.ResourceId;
                
                // Navigate to the Hub overview page
                await NavigateToHubOverviewAsync(detail.ResourceId);
            }
            catch (OperationCanceledException)
            {
                // Cancelled, ignore
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    ShowHubOverviewError($"Failed to load Hub info:\n{ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Validates that a Hub resource detail actually matches the requested package.
        /// The Hub API can return false positives (unrelated resources), so we verify:
        /// 1. Creator name matches
        /// 2. At least one HubFile has a matching package group name
        /// </summary>
        /// <param name="detail">The Hub resource detail returned by the API</param>
        /// <param name="packageGroupName">The package group name (Creator.PackageName without version)</param>
        /// <param name="fullPackageName">The full package name including version</param>
        /// <returns>True if the resource is a valid match for the package</returns>
        private static bool ValidateHubResourceMatch(Models.HubResourceDetail detail, string packageGroupName, string fullPackageName)
        {
            if (detail == null)
            {
                return false;
            }
            
            if (string.IsNullOrEmpty(packageGroupName))
            {
                return false;
            }
            
            // Extract creator from package group name (first segment before the dot)
            var packageCreator = ExtractCreatorFromPackageName(packageGroupName);
            
            if (string.IsNullOrEmpty(packageCreator))
            {
                return false;
            }
            
            // Rule 1: Creator must match (case-insensitive)
            var creatorMatch = string.Equals(detail.Creator, packageCreator, StringComparison.OrdinalIgnoreCase);
            
            if (string.IsNullOrEmpty(detail.Creator) || !creatorMatch)
            {
                return false;
            }
            
            // Rule 2: Check if any HubFile matches the package group name
            if (detail.HubFiles != null && detail.HubFiles.Count > 0)
            {
                foreach (var file in detail.HubFiles)
                {
                    if (string.IsNullOrEmpty(file.Filename))
                    {
                        continue;
                    }
                    
                    // Get the package group name from the Hub file
                    var hubFileGroupName = GetPackageGroupName(file.Filename);
                    
                    // Check for exact match of package group name
                    var fileMatch = string.Equals(hubFileGroupName, packageGroupName, StringComparison.OrdinalIgnoreCase);
                    
                    if (fileMatch)
                    {
                        return true;
                    }
                }
                
                // Has files but none match - this is a false positive
                return false;
            }
            
            // Rule 3: No HubFiles available (externally hosted?) - fall back to looser matching
            
            // Check if the package name (without creator prefix) appears in the title
            var packageNameWithoutCreator = ExtractPackageNameWithoutCreator(packageGroupName);
            
            if (!string.IsNullOrEmpty(packageNameWithoutCreator) && !string.IsNullOrEmpty(detail.Title))
            {
                // Normalize both strings for comparison (remove spaces, underscores, dashes)
                var normalizedTitle = NormalizeForComparison(detail.Title);
                var normalizedPackageName = NormalizeForComparison(packageNameWithoutCreator);
                
                // Title should contain the package name
                var titleContains = normalizedTitle.Contains(normalizedPackageName, StringComparison.OrdinalIgnoreCase);
                
                if (titleContains)
                {
                    return true;
                }
            }
            
            // No files and title doesn't match - reject
            return false;
        }
        
        /// <summary>
        /// Extract the creator name from a package name (first segment before the dot)
        /// </summary>
        private static string ExtractCreatorFromPackageName(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return null;
            
            var firstDot = packageName.IndexOf('.');
            if (firstDot > 0)
            {
                return packageName.Substring(0, firstDot);
            }
            
            return null;
        }
        
        /// <summary>
        /// Extract the package name without the creator prefix
        /// </summary>
        private static string ExtractPackageNameWithoutCreator(string packageGroupName)
        {
            if (string.IsNullOrEmpty(packageGroupName))
                return null;
            
            var firstDot = packageGroupName.IndexOf('.');
            if (firstDot > 0 && firstDot < packageGroupName.Length - 1)
            {
                return packageGroupName.Substring(firstDot + 1);
            }
            
            return null;
        }
        
        /// <summary>
        /// Normalize a string for fuzzy comparison by removing common separators
        /// </summary>
        private static string NormalizeForComparison(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;
            
            // Remove common separators and convert to lowercase
            return input
                .Replace(" ", "")
                .Replace("_", "")
                .Replace("-", "")
                .Replace(".", "")
                .ToLowerInvariant();
        }
        
        /// <summary>
        /// Navigate WebView2 to the Hub overview page for the given resource ID
        /// </summary>
        private async Task NavigateToHubOverviewAsync(string resourceId)
        {
            if (string.IsNullOrEmpty(resourceId))
            {
                ShowHubOverviewPlaceholder("No resource ID available");
                return;
            }
            
            // Initialize WebView2 if needed
            if (!_hubOverviewWebViewInitialized)
            {
                HubOverviewLoadingOverlay.Visibility = Visibility.Visible;
                await InitializeHubOverviewWebViewAsync();
                
                if (!_hubOverviewWebViewInitialized)
                {
                    ShowHubOverviewError("WebView2 is not available. Please install the WebView2 Runtime.");
                    return;
                }
            }
            
            // Build the URL
            var url = $"https://hub.virtamate.com/resources/{resourceId}/overview-panel";
            _currentHubOverviewUrl = url;
            
            try
            {
                // Hide placeholder, show loading
                HubOverviewPlaceholder.Visibility = Visibility.Collapsed;
                HubOverviewLoadingOverlay.Visibility = Visibility.Visible;
                HubOverviewErrorPanel.Visibility = Visibility.Collapsed;
                HubOverviewWebView.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                ShowHubOverviewError($"Navigation failed: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Hub Overview UI Helpers
        
        private void ShowHubOverviewPlaceholder(string message)
        {
            HubOverviewLoadingOverlay.Visibility = Visibility.Collapsed;
            HubOverviewErrorPanel.Visibility = Visibility.Collapsed;
            HubOverviewPlaceholderText.Text = message;
            HubOverviewPlaceholder.Visibility = Visibility.Visible;
            
            // Clear the WebView content so old page doesn't show behind placeholder
            ClearHubOverviewWebView();
        }
        
        private void ShowHubOverviewError(string message)
        {
            HubOverviewLoadingOverlay.Visibility = Visibility.Collapsed;
            HubOverviewPlaceholder.Visibility = Visibility.Collapsed;
            HubOverviewErrorText.Text = message;
            HubOverviewErrorPanel.Visibility = Visibility.Visible;
            
            // Clear the WebView content so old page doesn't show behind error
            ClearHubOverviewWebView();
        }
        
        private void ShowHubOverviewLoading()
        {
            HubOverviewLoadingOverlay.Visibility = Visibility.Visible;
            HubOverviewPlaceholder.Visibility = Visibility.Collapsed;
            HubOverviewErrorPanel.Visibility = Visibility.Collapsed;
        }
        
        /// <summary>
        /// Clear the WebView content by navigating to a blank page
        /// </summary>
        private void ClearHubOverviewWebView()
        {
            try
            {
                if (_hubOverviewWebViewInitialized && HubOverviewWebView?.CoreWebView2 != null)
                {
                    // Mark that we're clearing so we can ignore the navigation completion event
                    _isClearing = true;
                    // Use a data URL instead of NavigateToString to avoid connection issues
                    HubOverviewWebView.CoreWebView2.Navigate("data:text/html,<html><body style='background-color:#1E1E1E;'></body></html>");
                }
            }
            catch (Exception)
            {
                _isClearing = false;
                // Ignore errors when clearing WebView
            }
            
            _currentHubOverviewUrl = null;
            // Note: Don't clear _currentHubResourceId here - it's managed by LoadHubOverviewForSelectedPackageAsync
        }
        
        private void HubOverviewOpenInBrowser_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentHubOverviewUrl))
            {
                try
                {
                    // Convert panel URL to regular URL
                    var url = _currentHubOverviewUrl.Replace("-panel", "");
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // Ignore browser launch errors
                }
            }
            else if (!string.IsNullOrEmpty(_currentHubPackageName))
            {
                // Try to open a search for the package
                try
                {
                    var searchUrl = $"https://hub.virtamate.com/resources/?q={Uri.EscapeDataString(_currentHubPackageName)}";
                    Process.Start(new ProcessStartInfo(searchUrl) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // Ignore browser launch errors
                }
            }
        }
        
        #endregion
        
        #region Hub Overview Cleanup
        
        /// <summary>
        /// Cleanup Hub Overview resources when window closes
        /// </summary>
        private void CleanupHubOverview()
        {
            _hubOverviewCts?.Cancel();
            _hubOverviewCts?.Dispose();
            _hubOverviewCts = null;
            
            // Note: _hubService is shared and disposed elsewhere (MainWindow.PackageUpdates.cs)
        }
        
        #endregion
    }
}
