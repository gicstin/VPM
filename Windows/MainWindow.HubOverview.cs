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
        private bool _imagesNeedRefresh = false; // Track if images need to be loaded when switching to Images tab
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
                var userDataFolder = Path.Combine(Path.GetTempPath(), "VPM_WebView2");
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
        
        private void HubOverviewWebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            HubOverviewLoadingOverlay.Visibility = Visibility.Collapsed;
            
            if (!e.IsSuccess)
            {
                ShowHubOverviewError($"Failed to load page: {e.WebErrorStatus}");
            }
            else
            {
                HubOverviewErrorPanel.Visibility = Visibility.Collapsed;
                HubOverviewPlaceholder.Visibility = Visibility.Collapsed;
                
                // Inject CSS to improve dark theme appearance
                InjectHubOverviewDarkThemeStyles();
            }
        }
        
        private async void InjectHubOverviewDarkThemeStyles()
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
                
                await HubOverviewWebView.CoreWebView2.ExecuteScriptAsync(script);
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
                    // Mark that images need refresh when user switches to Images tab
                    _imagesNeedRefresh = true;
                    ImageAreaTabControl.SelectedItem = HubOverviewTab;
                }
                
                // If Hub tab is currently selected, update content for new selection
                if (ImageAreaTabControl.SelectedItem == HubOverviewTab)
                {
                    // Mark that images need refresh when user switches to Images tab
                    _imagesNeedRefresh = true;
                    // Force reload for new package (clear cached package name to trigger reload)
                    _currentHubPackageName = null;
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
                    // Mark that images need refresh since we're switching away from Hub
                    _imagesNeedRefresh = true;
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
                _imagesNeedRefresh = false;
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
            var packageName = GetPackageGroupName(selectedPackage.Name);
            
            // Skip if same package is already loaded
            if (_currentHubPackageName == packageName && _currentHubResourceId != null)
            {
                return;
            }
            
            _currentHubPackageName = packageName;
            
            // Show loading state
            HubOverviewLoadingOverlay.Visibility = Visibility.Visible;
            HubOverviewPlaceholder.Visibility = Visibility.Collapsed;
            HubOverviewErrorPanel.Visibility = Visibility.Collapsed;
            
            try
            {
                // Initialize HubService if needed
                _hubService ??= new HubService();
                
                // Look up the package on Hub by name
                var detail = await _hubService.GetResourceDetailAsync(packageName, isPackageName: true, token);
                
                if (token.IsCancellationRequested) return;
                
                if (detail == null || string.IsNullOrEmpty(detail.ResourceId))
                {
                    ShowHubOverviewPlaceholder($"Package not found on Hub:\n{packageName}");
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
        }
        
        private void ShowHubOverviewError(string message)
        {
            HubOverviewLoadingOverlay.Visibility = Visibility.Collapsed;
            HubOverviewPlaceholder.Visibility = Visibility.Collapsed;
            HubOverviewErrorText.Text = message;
            HubOverviewErrorPanel.Visibility = Visibility.Visible;
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
