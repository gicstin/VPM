using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using VPM.Models;
using VPM.Services;

namespace VPM.Windows
{
    /// <summary>
    /// Hub Browser Window - Browse and download packages from VaM Hub
    /// </summary>
    public partial class HubBrowserWindow : Window
    {
        // Windows API for dark title bar
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private readonly HubService _hubService;
        private readonly string _destinationFolder;
        private readonly string _vamFolder;  // Root VaM folder for searching packages
        private readonly Dictionary<string, string> _localPackagePaths;  // Package name -> file path
        private readonly PackageManager _packageManager;  // For accessing dependency graph and missing deps
        private SettingsManager _settingsManager;  // For persisting old version handling setting
        
        private CancellationTokenSource _searchCts;
        private int _currentPage = 1;
        private int _totalPages = 1;
        private int _totalResources = 0;
        
        // Side panel state
        private bool _isPanelExpanded = false;
        private const double PanelWidth = 480;  // Wider panel for WebView
        private HubResourceDetail _currentDetail;
        private HubResource _currentResource;  // Track the resource being viewed
        private ObservableCollection<HubFileViewModel> _currentFiles;
        private ObservableCollection<HubFileViewModel> _currentDependencies;
        
        // WebView2 state
        private bool _webViewInitialized = false;
        private string _currentWebViewUrl = null;
        private string _currentResourceId = null;
        
        // Overview panel state
        private bool _isOverviewPanelVisible = false;
        private const double DefaultOverviewPanelWidth = 500;
        private double _lastOverviewPanelWidth = DefaultOverviewPanelWidth;  // Remember user-set width
        
        // Creator filter state
        private List<string> _allCreators = new List<string>();
        private string _selectedCreator = "All";
        private bool _isCreatorFilterUpdating = false;
        
        // Maps normalized creator names (no spaces, lowercase) to Hub API names (with spaces)
        private Dictionary<string, string> _creatorNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        // Tags filter state
        private List<string> _allTags = new List<string>();
        private List<string> _selectedTags = new List<string>();
        private bool _isTagsFilterUpdating = false;
        
        // Download queue state
        private ObservableCollection<QueuedDownload> _downloadQueue = new ObservableCollection<QueuedDownload>();
        
        // Download progress tracking for the detail panel
        private int _totalDownloadsInBatch = 0;
        private int _completedDownloadsInBatch = 0;
        private string _currentDownloadingPackage = "";
        
        // Auto-search debounce
        private System.Windows.Threading.DispatcherTimer _searchDebounceTimer;
        private const int SearchDebounceDelayMs = 500;
        
        // Stack-based detail navigation
        private Stack<DetailStackEntry> _detailStack = new Stack<DetailStackEntry>();
        private Dictionary<string, DetailStackEntry> _savedDownloadingDetails = new Dictionary<string, DetailStackEntry>();
        
        // Updates panel debounce
        private bool _isUpdatesCheckInProgress = false;
        
        // Old version handling option
        private string _oldVersionHandling = "No Change";
        
        // Pre-computed lookups for fast library status checking
        private HashSet<string> _localPackageNames;  // All package names (without .var)
        private Dictionary<string, int> _localPackageVersions;  // Package group -> highest local version

        public HubBrowserWindow(string destinationFolder, Dictionary<string, string> localPackagePaths = null, PackageManager packageManager = null, SettingsManager settingsManager = null)
        {
            InitializeComponent();
            
            _hubService = new HubService();
            _destinationFolder = destinationFolder;
            _localPackagePaths = localPackagePaths ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _packageManager = packageManager;
            _settingsManager = settingsManager ?? new SettingsManager();
            
            // Pre-compute lookups for fast library status checking
            BuildLocalPackageLookups();
            
            // Initialize the cached image converter with the HubService
            CachedHubImageConverter.SetHubService(_hubService);
            
            // Derive VaM folder from destination folder
            // Look for the parent that contains known VaM folders (Custom, Saves, etc.)
            _vamFolder = DeriveVamFolder(destinationFolder);
            
            _currentFiles = new ObservableCollection<HubFileViewModel>();
            _currentDependencies = new ObservableCollection<HubFileViewModel>();

            _hubService.StatusChanged += (s, status) => 
            {
                // Use BeginInvoke to prevent UI blocking
                Dispatcher.BeginInvoke(() => StatusText.Text = status);
            };
            
            // Subscribe to download queue events
            _hubService.DownloadQueued += HubService_DownloadQueued;
            _hubService.DownloadStarted += HubService_DownloadStarted;
            _hubService.DownloadCompleted += HubService_DownloadCompleted;

            SourceInitialized += HubBrowserWindow_SourceInitialized;
            Loaded += HubBrowserWindow_Loaded;
            Closed += HubBrowserWindow_Closed;
            
            // Initialize download queue list binding
            DownloadQueueList.ItemsSource = _downloadQueue;
            
            // Creator filter will be populated after packages.json loads
            
            // Initialize auto-search debounce timer
            _searchDebounceTimer = new System.Windows.Threading.DispatcherTimer();
            _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(SearchDebounceDelayMs);
            _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
            
            // Note: OldVersionHandlingDropdown event handler is set in XAML code-behind after InitializeComponent
        }

        private void HubBrowserWindow_SourceInitialized(object sender, EventArgs e)
        {
            ApplyDarkTitleBar();
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int value = 1;
                    if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref value, sizeof(int));
                    }
                }
            }
            catch
            {
                // Silently fail if dark title bar is not supported
            }
        }

        private async void HubBrowserWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Hook up old version handling dropdown (sync, fast)
            if (OldVersionHandlingDropdown != null)
            {
                // Load saved setting from settings manager
                _oldVersionHandling = _settingsManager.GetSetting("OldVersionHandling", "No Change");
                
                // Set dropdown to saved value BEFORE subscribing to event to avoid unnecessary save
                foreach (ComboBoxItem item in OldVersionHandlingDropdown.Items)
                {
                    if (item.Content?.ToString() == _oldVersionHandling)
                    {
                        OldVersionHandlingDropdown.SelectedItem = item;
                        break;
                    }
                }
                
                // Subscribe to event AFTER setting initial value
                OldVersionHandlingDropdown.SelectionChanged += OldVersionHandlingDropdown_SelectionChanged;
            }
            
            // Start all async operations in parallel for faster startup
            var packagesTask = _hubService.LoadPackagesJsonAsync();
            var searchTask = SearchAsync();
            
            // Wait for all tasks in parallel
            await Task.WhenAll(packagesTask, searchTask);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Ctrl+Shift+P to open Performance Monitor
            if (e.Key == Key.P && 
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                ShowPerformanceMonitor();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }
        
        private void OldVersionHandlingDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (OldVersionHandlingDropdown.SelectedItem is ComboBoxItem item)
            {
                _oldVersionHandling = item.Content?.ToString() ?? "No Change";
                
                // Save to settings immediately
                _settingsManager.UpdateSetting("OldVersionHandling", _oldVersionHandling);
            }
        }

        private void HubBrowserWindow_Closed(object sender, EventArgs e)
        {
            _searchCts?.Cancel();
            _hubService?.Dispose();
            
            // Dispose WebView2 - wrap in try-catch as it can throw if browser process has terminated
            try
            {
                if (OverviewWebView != null)
                {
                    OverviewWebView.Dispose();
                }
            }
            catch (Exception)
            {
            }
        }
        
        /// <summary>
        /// Initialize WebView2 asynchronously
        /// </summary>
        private async Task InitializeWebViewAsync()
        {
            if (_webViewInitialized) return;
            
            try
            {
                // Create a user data folder in temp for WebView2
                var userDataFolder = Path.Combine(Path.GetTempPath(), "VPM_WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await OverviewWebView.EnsureCoreWebView2Async(env);
                
                // Configure WebView2 settings for dark theme and Hub compatibility
                var settings = OverviewWebView.CoreWebView2.Settings;
                settings.IsStatusBarEnabled = false;
                settings.AreDefaultContextMenusEnabled = true;
                settings.IsZoomControlEnabled = true;
                settings.AreDevToolsEnabled = false;
                
                // Set dark theme preference
                OverviewWebView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
                
                // Add Hub consent cookie
                var cookieManager = OverviewWebView.CoreWebView2.CookieManager;
                var cookie = cookieManager.CreateCookie("vamhubconsent", "1", ".virtamate.com", "/");
                cookie.IsSecure = true;
                cookieManager.AddOrUpdateCookie(cookie);
                
                // Handle navigation events
                OverviewWebView.NavigationStarting += WebView_NavigationStarting;
                OverviewWebView.NavigationCompleted += WebView_NavigationCompleted;
                
                _webViewInitialized = true;
            }
            catch (Exception ex)
            {
                _webViewInitialized = false;
                ShowWebViewError($"WebView2 initialization failed: {ex.Message}");
            }
        }
        
        private void WebView_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            WebViewLoadingOverlay.Visibility = Visibility.Visible;
            WebViewErrorPanel.Visibility = Visibility.Collapsed;
        }
        
        private void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            WebViewLoadingOverlay.Visibility = Visibility.Collapsed;
            
            if (!e.IsSuccess)
            {
                ShowWebViewError($"Failed to load page: {e.WebErrorStatus}");
            }
            else
            {
                WebViewErrorPanel.Visibility = Visibility.Collapsed;
                
                // Inject CSS to improve dark theme appearance
                InjectDarkThemeStyles();
            }
        }
        
        private async void InjectDarkThemeStyles()
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
                
                await OverviewWebView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception)
            {
                // Handle exception
            }
        }
        
        private void ShowWebViewError(string message)
        {
            WebViewErrorText.Text = message;
            WebViewLoadingOverlay.Visibility = Visibility.Collapsed;
            WebViewErrorPanel.Visibility = Visibility.Visible;
        }
        
        /// <summary>
        /// Open the performance monitoring window
        /// </summary>
        public void ShowPerformanceMonitor()
        {
            var perfWindow = new PerformanceWindow(_hubService.PerformanceMonitor);
            perfWindow.Owner = this;
            perfWindow.Show();
        }

        private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentWebViewUrl))
            {
                try
                {
                    // Convert panel URL to regular URL
                    var url = _currentWebViewUrl.Replace("-panel", "");
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // Handle exception
                }
            }
        }
        
        private void SupportCreator_Click(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock textBlock && textBlock.Tag is string url && !string.IsNullOrEmpty(url))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // Handle exception
                }
            }
        }
        
        private void DetailCreator_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock textBlock && textBlock.Tag is string creatorName && !string.IsNullOrEmpty(creatorName))
            {
                // Set the creator filter
                FilterByCreator(creatorName);
            }
        }
        
        private void DetailCategory_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock textBlock && textBlock.Tag is string categoryName && !string.IsNullOrEmpty(categoryName))
            {
                // Set the category filter
                FilterByCategory(categoryName);
            }
        }
        
        private void FilterByCategory(string categoryName)
        {
            // Find the category in the CategoryFilter ComboBox and select it
            for (int i = 0; i < CategoryFilter.Items.Count; i++)
            {
                var item = CategoryFilter.Items[i] as ComboBoxItem;
                if (item != null && item.Content?.ToString()?.Equals(categoryName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    CategoryFilter.SelectedIndex = i;
                    // Trigger search with new filter
                    _ = SearchAsync();
                    return;
                }
            }
        }
        
        private void FilterByCreator(string creatorName)
        {
            // Set the selected creator first (this is what SearchAsync uses)
            _selectedCreator = creatorName;
            
            // Find the creator in the list box and select it
            for (int i = 0; i < CreatorListBox.Items.Count; i++)
            {
                var item = CreatorListBox.Items[i]?.ToString();
                if (item != null && item.Equals(creatorName, StringComparison.OrdinalIgnoreCase))
                {
                    CreatorListBox.SelectedIndex = i;
                    // Update the display text
                    SelectedCreatorText.Text = creatorName;
                    ClearCreatorButton.Visibility = Visibility.Visible;
                    // Trigger search with new filter
                    _ = SearchAsync();
                    return;
                }
            }
            
            // If creator not in list, add it temporarily and select
            // Must modify the ItemsSource collection directly, not Items
            if (CreatorListBox.ItemsSource is List<string> creators)
            {
                if (!creators.Contains(creatorName, StringComparer.OrdinalIgnoreCase))
                {
                    creators.Add(creatorName);
                }
                CreatorListBox.SelectedItem = creatorName;
            }
            SelectedCreatorText.Text = creatorName;
            ClearCreatorButton.Visibility = Visibility.Visible;
            _ = SearchAsync();
        }
        
        private void DetailTag_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Hyperlink hyperlink && hyperlink.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
            {
                try
                {
                    // Apply this tag as the active tags filter
                    _selectedTags.Clear();
                    _selectedTags.Add(tag);
                    UpdateTagsDisplay();

                    // Start a new search from page 1
                    _currentPage = 1;
                    _ = SearchAsync();
                }
                catch (Exception)
                {
                    // Handle exception
                }
            }
        }
        
        #region Overview Panel
        
        /// <summary>
        /// Toggle the Overview panel visibility
        /// </summary>
        private void ToggleOverviewPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_isOverviewPanelVisible)
            {
                CollapseOverviewPanel();
            }
            else if (!string.IsNullOrEmpty(_currentResourceId))
            {
                _ = ExpandOverviewPanelAsync();
            }
        }
        
        private async Task ExpandOverviewPanelAsync()
        {
            if (string.IsNullOrEmpty(_currentResourceId))
                return;
            
            // Show the panel with remembered width
            OverviewPanelColumn.Width = new GridLength(_lastOverviewPanelWidth);
            OverviewPanelColumn.MinWidth = 300;
            OverviewSplitter.Visibility = Visibility.Visible;
            _isOverviewPanelVisible = true;
            
            // Navigate to overview
            await NavigateToHubPage("TabOverview");
        }
        
        private void CollapseOverviewPanel()
        {
            // Save current width before collapsing
            if (OverviewPanelColumn.Width.Value > 0)
            {
                _lastOverviewPanelWidth = OverviewPanelColumn.Width.Value;
            }
            
            OverviewPanelColumn.Width = new GridLength(0);
            OverviewPanelColumn.MinWidth = 0;
            OverviewSplitter.Visibility = Visibility.Collapsed;
            _isOverviewPanelVisible = false;
        }
        
        /// <summary>
        /// Handle Overview tab navigation clicks
        /// </summary>
        private async void OverviewTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton tab || string.IsNullOrEmpty(_currentResourceId))
                return;
            
            await NavigateToHubPage(tab.Name);
        }
        
        #endregion
        
        #region Adaptive Grid
        
        private const double MinCardWidth = 200;
        private const double MaxCardWidth = 280;
        private const double CardMargin = 8; // 4px margin on each side
        
        private void ResourcesItemsControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateGridColumns(e.NewSize.Width);
        }
        
        private void UpdateGridColumns(double availableWidth)
        {
            if (availableWidth <= 0)
                return;
            
            // Calculate optimal number of columns
            // Each card needs MinCardWidth + margins
            int maxColumns = Math.Max(1, (int)(availableWidth / (MinCardWidth + CardMargin)));
            int minColumns = Math.Max(1, (int)(availableWidth / (MaxCardWidth + CardMargin)));
            
            // Use a column count that gives us cards between min and max width
            int columns = Math.Max(minColumns, Math.Min(maxColumns, 6)); // Cap at 6 columns
            
            // Find the UniformGrid and update columns
            if (ResourcesItemsControl.ItemsPanel?.LoadContent() is UniformGrid templateGrid)
            {
                // We need to find the actual panel, not the template
                var panel = FindVisualChild<UniformGrid>(ResourcesItemsControl);
                if (panel != null)
                {
                    panel.Columns = columns;
                }
            }
        }
        
        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    return typedChild;
                
                var result = FindVisualChild<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }
        
        #endregion

        #region Search & Filtering

        private async Task SearchAsync()
        {
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();

            try
            {
                using (var timer = _hubService.PerformanceMonitor.StartOperation("SearchAsync_Total"))
                {
                    LoadingPanel.Visibility = Visibility.Visible;
                    ResourcesItemsControl.ItemsSource = null;

                    var searchParams = BuildSearchParams();
                    var response = await _hubService.SearchResourcesAsync(searchParams, _searchCts.Token);

                    if (response?.IsSuccess == true)
                    {
                        using (var uiTimer = _hubService.PerformanceMonitor.StartOperation("SearchAsync_UIUpdate"))
                        {
                            _totalResources = response.Pagination?.TotalFound ?? 0;
                            _totalPages = response.Pagination?.TotalPages ?? 1;

                            // Mark resources that are in library or have updates
                            using (var libTimer = _hubService.PerformanceMonitor.StartOperation("SearchAsync_CheckLibraryStatus"))
                            {
                                foreach (var resource in response.Resources ?? Enumerable.Empty<HubResource>())
                                {
                                    CheckLibraryStatus(resource);
                                }
                            }

                            ResourcesItemsControl.ItemsSource = response.Resources;
                            UpdatePaginationUI();
                            
                            // Learn Hub API creator names from results (for name mapping)
                            LearnCreatorNamesFromResults(response.Resources);
                            
                            StatusText.Text = $"Found {_totalResources} resources";
                        }
                        
                        // Prefetch adjacent pages in background for faster navigation
                        _ = PrefetchAdjacentPagesAsync(searchParams);
                    }
                    else
                    {
                        StatusText.Text = $"Error: {response?.Error ?? "Unknown error"}";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Search was cancelled
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }
        
        /// <summary>
        /// Prefetch adjacent pages in background to make pagination feel instant
        /// </summary>
        private async Task PrefetchAdjacentPagesAsync(HubSearchParams currentParams)
        {
            try
            {
                var prefetchTasks = new List<Task>();
                
                // Prefetch next page if not at end
                if (_currentPage < _totalPages)
                {
                    var nextParams = CloneSearchParams(currentParams);
                    nextParams.Page = _currentPage + 1;
                    prefetchTasks.Add(_hubService.SearchResourcesAsync(nextParams));
                }
                
                // Prefetch previous page if not at start
                if (_currentPage > 1)
                {
                    var prevParams = CloneSearchParams(currentParams);
                    prevParams.Page = _currentPage - 1;
                    prefetchTasks.Add(_hubService.SearchResourcesAsync(prevParams));
                }
                
                // Fire and forget - results will be cached
                if (prefetchTasks.Count > 0)
                {
                    await Task.WhenAll(prefetchTasks);
                }
            }
            catch (Exception)
            {
                // Handle exception
            }
        }
        
        private static HubSearchParams CloneSearchParams(HubSearchParams p)
        {
            return new HubSearchParams
            {
                Page = p.Page,
                PerPage = p.PerPage,
                Location = p.Location,
                Search = p.Search,
                PayType = p.PayType,
                Category = p.Category,
                Creator = p.Creator,
                Tags = p.Tags,
                Sort = p.Sort,
                SortSecondary = p.SortSecondary,
                OnlyDownloadable = p.OnlyDownloadable
            };
        }

        private HubSearchParams BuildSearchParams()
        {
            var sort = (SortFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Last Update";
            var sortSecondary = (SortSecondaryFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "None";
            var payType = (PayTypeFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Free";
            var category = (CategoryFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
            
            // Join multiple selected tags with comma
            var tags = _selectedTags.Count == 0 ? "All" : string.Join(",", _selectedTags);
            
            return new HubSearchParams
            {
                Page = _currentPage,
                PerPage = 48,
                Search = SearchBox.Text?.Trim(),
                Location = "Hub And Dependencies",
                Category = category,
                Creator = _selectedCreator ?? "All",
                PayType = payType,
                Tags = tags,
                Sort = sort,
                SortSecondary = sortSecondary,
                OnlyDownloadable = OnlyDownloadableCheck.IsChecked == true
            };
        }

        /// <summary>
        /// Build pre-computed lookups for fast library status checking.
        /// Called once at startup to avoid repeated dictionary iterations.
        /// </summary>
        private void BuildLocalPackageLookups()
        {
            _localPackageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _localPackageVersions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var pkg in _localPackagePaths.Keys)
            {
                var name = pkg.Replace(".var", "");
                _localPackageNames.Add(name);
                
                // Extract group name and version
                var groupName = GetPackageGroupName(name);
                var lastDot = name.LastIndexOf('.');
                if (lastDot > 0)
                {
                    var versionPart = name.Substring(lastDot + 1);
                    if (int.TryParse(versionPart, out var version))
                    {
                        if (!_localPackageVersions.TryGetValue(groupName, out var existing) || version > existing)
                        {
                            _localPackageVersions[groupName] = version;
                        }
                    }
                }
            }
        }
        
        private void CheckLibraryStatus(HubResource resource)
        {
            if (resource.HubFiles == null || !resource.HubFiles.Any())
                return;

            foreach (var file in resource.HubFiles)
            {
                var packageName = file.PackageName;
                if (string.IsNullOrEmpty(packageName))
                    continue;

                var cleanName = packageName.Replace(".var", "");
                
                // Fast check using pre-computed HashSet
                if (_localPackageNames.Contains(cleanName))
                {
                    resource.InLibrary = true;
                }
                else
                {
                    // Check for any version of this package (for .latest)
                    var groupName = GetPackageGroupName(cleanName);
                    if (_localPackageVersions.ContainsKey(groupName))
                    {
                        resource.InLibrary = true;
                    }
                }

                // Check for updates using pre-computed version lookup
                var pkgGroupName = GetPackageGroupName(cleanName);
                if (_localPackageVersions.TryGetValue(pkgGroupName, out var localVersion) && 
                    localVersion > 0 && 
                    _hubService.HasUpdate(pkgGroupName, localVersion))
                {
                    resource.UpdateAvailable = true;
                    resource.UpdateMessage = "Update available";
                }
            }
        }

        private string GetPackageGroupName(string packageName)
        {
            var name = packageName;
            
            // Remove .var extension
            if (name.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            
            // Remove .latest suffix
            if (name.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 7);
            
            // Remove version number (digits at the end)
            var lastDot = name.LastIndexOf('.');
            if (lastDot > 0)
            {
                var afterDot = name.Substring(lastDot + 1);
                if (int.TryParse(afterDot, out _))
                {
                    return name.Substring(0, lastDot);
                }
            }
            return name;
        }

        private int GetHighestLocalVersion(string groupName)
        {
            // Use pre-computed lookup (O(1) instead of O(n))
            return _localPackageVersions.TryGetValue(groupName, out var version) ? version : 0;
        }
        
        /// <summary>
        /// Find a local package by name. Trusts _localPackagePaths without File.Exists checks
        /// since it was populated from actual files on disk at startup.
        /// Supports finding any version of a package (for .latest dependencies).
        /// </summary>
        /// <param name="packageName">Package name to find</param>
        /// <returns>Full path to the package file if found, null otherwise</returns>
        private string FindLocalPackage(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return null;
            
            var cleanName = packageName.Replace(".var", "");
            
            // First, try exact match in our known packages (trust the dictionary - files were verified at startup)
            if (_localPackagePaths.TryGetValue(cleanName, out var exactPath))
                return exactPath;
            
            // Try with .var extension
            if (_localPackagePaths.TryGetValue(cleanName + ".var", out exactPath))
                return exactPath;
            
            // For .latest or version-flexible matching, find any version of this package
            var basePackage = GetBasePackageName(cleanName);
            var basePackagePrefix = basePackage + ".";
            
            // Find matching packages - must be basePackage.{version} where version is numeric
            // Optimized: single pass through dictionary
            string bestPath = null;
            int bestVersion = -1;
            
            foreach (var kvp in _localPackagePaths)
            {
                var name = kvp.Key.Replace(".var", "");
                if (!name.StartsWith(basePackagePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                    
                var suffix = name.Substring(basePackagePrefix.Length);
                if (int.TryParse(suffix, out var version) && version > bestVersion)
                {
                    bestVersion = version;
                    bestPath = kvp.Value;
                }
            }
            
            return bestPath;
        }

        #endregion

        #region UI Event Handlers

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            _currentPage = 1;
            await SearchAsync();
        }

        private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                // Stop debounce timer if user presses Enter
                _searchDebounceTimer.Stop();
                _currentPage = 1;
                await SearchAsync();
            }
        }
        
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Reset and start the debounce timer
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }
        
        private async void SearchDebounceTimer_Tick(object sender, EventArgs e)
        {
            _searchDebounceTimer.Stop();
            _currentPage = 1;
            await SearchAsync();
        }

        private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            
            try
            {
                _currentPage = 1;
                await SearchAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }
        
        private async void CheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            
            _currentPage = 1;
            await SearchAsync();
        }
        
        #region Creator Filter
        
        /// <summary>
        /// Load creator list from packages.json (instant, complete list)
        /// </summary>
        private void LoadCreatorListFromPackages()
        {
            var creators = _hubService.GetAllCreators();
            
            _allCreators = new List<string> { "All" };
            _allCreators.AddRange(creators);
            
            CreatorCountText.Text = $"{creators.Count} creators";
        }
        
        /// <summary>
        /// Load dynamic filter options from Hub API
        /// </summary>
        private async Task LoadFilterOptionsAsync()
        {
            try
            {
                StatusText.Text = "Loading filter options...";
                var options = await _hubService.GetFilterOptionsAsync();
                
                if (options != null)
                {
                    // Update Category filter
                    if (options.Types != null && options.Types.Count > 0)
                    {
                        CategoryFilter.Items.Clear();
                        CategoryFilter.Items.Add(new ComboBoxItem { Content = "All", IsSelected = true });
                        foreach (var type in options.Types)
                        {
                            CategoryFilter.Items.Add(new ComboBoxItem { Content = type });
                        }
                    }
                    
                    // Update Sort filter
                    if (options.SortOptions != null && options.SortOptions.Count > 0)
                    {
                        SortFilter.Items.Clear();
                        bool isFirst = true;
                        foreach (var sort in options.SortOptions)
                        {
                            var item = new ComboBoxItem { Content = sort };
                            if (isFirst)
                            {
                                item.IsSelected = true;
                                isFirst = false;
                            }
                            SortFilter.Items.Add(item);
                        }
                    }
                    
                    // Update Secondary Sort filter
                    if (options.SortOptions != null && options.SortOptions.Count > 0)
                    {
                        SortSecondaryFilter.Items.Clear();
                        SortSecondaryFilter.Items.Add(new ComboBoxItem { Content = "None", IsSelected = true });
                        foreach (var sort in options.SortOptions)
                        {
                            SortSecondaryFilter.Items.Add(new ComboBoxItem { Content = sort });
                        }
                    }
                    
                    // Update Tags filter
                    if (options.Tags != null && options.Tags.Count > 0)
                    {
                        _allTags = options.Tags.Keys.OrderBy(t => t).ToList();
                        PopulateTagsListBox("");
                    }
                    
                    StatusText.Text = "Ready";
                }
                else
                {
                    StatusText.Text = "Failed to load filter options";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error loading filter options: {ex.Message}";
            }
        }
        
        /// <summary>
        /// Learn Hub API creator names from search results and cache the mapping
        /// </summary>
        private void LearnCreatorNamesFromResults(IEnumerable<HubResource> resources)
        {
            if (resources == null) return;
            
            foreach (var resource in resources)
            {
                if (!string.IsNullOrEmpty(resource.Creator))
                {
                    // Normalize: remove spaces and lowercase for matching
                    var normalized = resource.Creator.Replace(" ", "").ToLowerInvariant();
                    
                    // Store the Hub API name (with proper spacing)
                    if (!_creatorNameMap.ContainsKey(normalized))
                    {
                        _creatorNameMap[normalized] = resource.Creator;
                    }
                }
            }
        }
        
        /// <summary>
        /// Get the Hub API creator name for a given creator (resolves name mapping)
        /// Uses partial search to find the correct Hub API name with spaces
        /// </summary>
        private async Task<string> ResolveCreatorNameAsync(string creator)
        {
            if (creator == "All") return "All";
            
            // Normalize the selected creator name
            var normalized = creator.Replace(" ", "").ToLowerInvariant();
            
            // Check if we already know the Hub API name
            if (_creatorNameMap.TryGetValue(normalized, out var hubName))
            {
                return hubName;
            }
            
            // Search using first 3-4 characters to find packages by this creator
            // This works because "Aci" will match "Acid Bubbles" packages
            try
            {
                // Use first few characters for search (handles CamelCase like "AcidBubble")
                var searchPrefix = GetSearchPrefix(creator);
                
                var searchParams = new HubSearchParams
                {
                    Page = 1,
                    PerPage = 48,
                    Search = searchPrefix,
                    PayType = "Free",
                    OnlyDownloadable = true
                };
                
                var response = await _hubService.SearchResourcesAsync(searchParams);
                
                if (response?.IsSuccess == true && response.Resources != null)
                {
                    // Find the creator whose normalized name matches ours
                    foreach (var resource in response.Resources)
                    {
                        if (!string.IsNullOrEmpty(resource.Creator))
                        {
                            var resourceNormalized = resource.Creator.Replace(" ", "").ToLowerInvariant();
                            if (resourceNormalized == normalized)
                            {
                                // Found it! Cache and return
                                _creatorNameMap[normalized] = resource.Creator;
                                return resource.Creator;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Handle exception
            }
            
            // Fallback to original name
            return creator;
        }
        
        /// <summary>
        /// Get a search prefix from a creator name (first word or first few chars)
        /// "AcidBubble" -> "Acid", "MacGruber" -> "MacG"
        /// </summary>
        private string GetSearchPrefix(string creator)
        {
            if (string.IsNullOrEmpty(creator)) return "";
            
            // Try to split on CamelCase boundaries
            var result = new StringBuilder();
            bool foundUpper = false;
            
            foreach (char c in creator)
            {
                if (result.Length > 0 && char.IsUpper(c))
                {
                    // Found second capital letter, we have first word
                    foundUpper = true;
                    break;
                }
                result.Append(c);
            }
            
            // If we found a CamelCase split and have at least 3 chars, use it
            if (foundUpper && result.Length >= 3)
            {
                return result.ToString();
            }
            
            // Otherwise use first 4 characters minimum
            return creator.Substring(0, Math.Min(4, creator.Length));
        }
        
        private void CreatorFilterToggle_Checked(object sender, RoutedEventArgs e)
        {
            // Clear search and show all when opening
            CreatorSearchBox.Text = "";
            FilterCreatorList("");
            
            // Focus the search box
            Dispatcher.BeginInvoke(new Action(() => 
            {
                CreatorSearchBox.Focus();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
        
        private void CreatorFilterToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            // Nothing special needed when closing
        }
        
        private void TagsFilterToggle_Checked(object sender, RoutedEventArgs e)
        {
            // Clear search and show all when opening
            TagsSearchBox.Text = "";
            PopulateTagsListBox("");
            
            // Focus the search box
            Dispatcher.BeginInvoke(new Action(() => 
            {
                TagsSearchBox.Focus();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
        
        private void TagsFilterToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            // Nothing special needed when closing
        }
        
        private void TagsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var searchText = TagsSearchBox.Text?.ToLowerInvariant() ?? "";
            PopulateTagsListBox(searchText);
        }
        
        private void PopulateTagsListBox(string searchText)
        {
            if (_isTagsFilterUpdating) return;
            
            _isTagsFilterUpdating = true;
            try
            {
                TagsListBox.Items.Clear();
                
                var filteredTags = string.IsNullOrEmpty(searchText)
                    ? _allTags
                    : _allTags.Where(t => t.ToLowerInvariant().Contains(searchText)).ToList();
                
                foreach (var tag in filteredTags)
                {
                    var item = new ListBoxItem { Content = tag };
                    if (_selectedTags.Contains(tag))
                    {
                        item.IsSelected = true;
                    }
                    TagsListBox.Items.Add(item);
                }
            }
            finally
            {
                _isTagsFilterUpdating = false;
            }
        }
        
        private void TagsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isTagsFilterUpdating) return;
            
            _selectedTags.Clear();
            foreach (ListBoxItem item in TagsListBox.SelectedItems)
            {
                _selectedTags.Add(item.Content.ToString());
            }
            
            UpdateTagsDisplay();
            
            // Trigger search
            _currentPage = 1;
            _ = SearchAsync();
        }
        
        private void UpdateTagsDisplay()
        {
            if (_selectedTags.Count == 0)
            {
                TagsDisplayText.Text = "All";
            }
            else if (_selectedTags.Count == 1)
            {
                TagsDisplayText.Text = _selectedTags[0];
            }
            else
            {
                TagsDisplayText.Text = $"{_selectedTags.Count} tags";
            }
        }
        
        private void ClearTagsFilter_Click(object sender, RoutedEventArgs e)
        {
            _selectedTags.Clear();
            TagsListBox.SelectedItems.Clear();
            UpdateTagsDisplay();
            TagsFilterToggle.IsChecked = false;
            
            // Trigger search
            _currentPage = 1;
            _ = SearchAsync();
        }
        
        private void CreatorSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var searchText = CreatorSearchBox.Text?.Trim() ?? "";
            FilterCreatorList(searchText);
            
            // Update placeholder visibility
            CreatorSearchPlaceholder.Visibility = string.IsNullOrEmpty(CreatorSearchBox.Text) 
                ? Visibility.Visible 
                : Visibility.Collapsed;
        }
        
        private void FilterCreatorList(string searchText)
        {
            if (_isCreatorFilterUpdating) return;
            
            _isCreatorFilterUpdating = true;
            try
            {
                IEnumerable<string> filtered;
                
                if (string.IsNullOrEmpty(searchText))
                {
                    filtered = _allCreators;
                }
                else
                {
                    filtered = _allCreators.Where(c => 
                        c.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                
                // Limit to 100 items for performance
                var items = filtered.Take(100).ToList();
                
                CreatorListBox.ItemsSource = null;
                CreatorListBox.ItemsSource = items;
                
                // Update count
                var totalMatching = filtered.Count();
                CreatorCountText.Text = totalMatching > 100 
                    ? $"Showing 100 of {totalMatching} creators" 
                    : $"{totalMatching} creators";
            }
            finally
            {
                _isCreatorFilterUpdating = false;
            }
        }
        
        private async void CreatorListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isCreatorFilterUpdating) return;
            
            var selected = CreatorListBox.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selected)) return;
            
            // Close the popup
            CreatorFilterToggle.IsChecked = false;
            
            // Resolve the Hub API name (handles "AcidBubble" -> "Acid Bubbles" mapping)
            var resolvedName = await ResolveCreatorNameAsync(selected);
            _selectedCreator = resolvedName;
            
            // Update UI with resolved name
            UpdateCreatorFilterUI();
            
            _currentPage = 1;
            await SearchAsync();
        }
        
        private async void ClearCreatorFilter_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // Prevent toggle from opening
            
            _selectedCreator = "All";
            UpdateCreatorFilterUI();
            
            _currentPage = 1;
            await SearchAsync();
        }
        
        private void UpdateCreatorFilterUI()
        {
            // Update the displayed text
            SelectedCreatorText.Text = _selectedCreator;
            
            // Update clear button visibility
            ClearCreatorButton.Visibility = _selectedCreator != "All" 
                ? Visibility.Visible 
                : Visibility.Collapsed;
        }
        
        #endregion

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await _hubService.LoadPackagesJsonAsync(forceRefresh: true);
            LoadCreatorListFromPackages();
            await SearchAsync();
        }

        private async void FirstPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage = 1;
                await SearchAsync();
            }
        }

        private async void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                await SearchAsync();
            }
        }

        private async void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < _totalPages)
            {
                _currentPage++;
                await SearchAsync();
            }
        }

        private void ResourceCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is HubResource resource)
            {
                ShowResourceDetail(resource);
            }
        }

        private void ResourcesScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Increase scrolling sensitivity by multiplying the delta
            const double scrollMultiplier = 100;
            
            if (sender is ScrollViewer scrollViewer)
            {
                double newOffset = scrollViewer.VerticalOffset - (e.Delta * scrollMultiplier / 120.0);
                scrollViewer.ScrollToVerticalOffset(newOffset);
                e.Handled = true;
            }
        }

        private void UpdatePaginationUI()
        {
            PageInfoText.Text = $"Page {_currentPage} of {_totalPages}";
            TotalCountText.Text = $"Total: {_totalResources}";
            
            FirstPageButton.IsEnabled = _currentPage > 1;
            PrevPageButton.IsEnabled = _currentPage > 1;
            NextPageButton.IsEnabled = _currentPage < _totalPages;
        }

        #endregion

        #region Resource Detail Side Panel

        private string _currentImageUrl;
        
        private async void LoadDetailImageAsync(string imageUrl)
        {
            _currentImageUrl = imageUrl;
            
            if (string.IsNullOrEmpty(imageUrl))
            {
                DetailImage.Source = null;
                return;
            }
            
            try
            {
                // Clear while loading
                DetailImage.Source = null;
                
                // Use HubService cached image method instead of direct download
                var bitmap = await _hubService.GetCachedImageAsync(imageUrl);
                
                // Check if still current before setting on UI thread
                if (_currentImageUrl != imageUrl)
                {
                    return;
                }
                
                if (bitmap != null)
                {
                    DetailImage.Source = bitmap;
                }
                else
                {
                    DetailImage.Source = null;
                }
            }
            catch (Exception)
            {
                if (_currentImageUrl == imageUrl)
                {
                    DetailImage.Source = null;
                }
            }
        }

        private async void ShowResourceDetail(HubResource resource)
        {
            try
            {
                StatusText.Text = $"Loading details for {resource.Title}...";
                
                // Check if this resource is in saved downloading details
                if (_savedDownloadingDetails.TryGetValue(resource.ResourceId, out var savedEntry))
                {
                    // Restore from saved state
                    _savedDownloadingDetails.Remove(resource.ResourceId);
                    _detailStack.Push(savedEntry);
                    RestoreDetailFromStack(savedEntry);
                    ExpandPanel();
                    UpdateDetailStackUI();
                    StatusText.Text = "Ready";
                    return;
                }
                
                var detail = await _hubService.GetResourceDetailAsync(resource.ResourceId);
                
                if (detail != null)
                {
                    // Preserve tags from search result if the detail call doesn't provide them
                    try
                    {
                        if ((detail.TagsDict == null || detail.TagsDict.Count == 0) &&
                            resource.TagsDict != null && resource.TagsDict.Count > 0)
                        {
                            detail.TagsDict = resource.TagsDict;
                        }
                    }
                    catch (Exception)
                    {
                        // Handle exception
                    }
                    // Push current state to stack before showing new resource (if there is one)
                    // This saves the previous resource so we can go back to it
                    if (_currentDetail != null && _currentResource != null && _detailStack.Count > 0)
                    {
                        // Update the top of stack with current state before navigating away
                        // (in case files/dependencies changed during viewing)
                        var currentTop = _detailStack.Pop();
                        currentTop.Files = new ObservableCollection<HubFileViewModel>(_currentFiles);
                        currentTop.Dependencies = new ObservableCollection<HubFileViewModel>(_currentDependencies);
                        _detailStack.Push(currentTop);
                    }
                    
                    _currentDetail = detail;
                    _currentResource = resource;  // Store the resource for later updates
                    _currentResourceId = resource.ResourceId;  // Store for WebView navigation
                    
                    PopulateDetailPanel(detail);
                    ExpandPanel();
                    
                    // Push new state to stack (this is the new current item)
                    PushToDetailStack(detail, resource,
                        new ObservableCollection<HubFileViewModel>(_currentFiles),
                        new ObservableCollection<HubFileViewModel>(_currentDependencies));
                    
                    // Open Overview panel if not already visible, or just navigate to new content
                    TabOverview.IsChecked = true;
                    if (_isOverviewPanelVisible)
                    {
                        // Just navigate to new resource, keep current width
                        await NavigateToHubPage("TabOverview");
                    }
                    else
                    {
                        // First time opening, use default or last width
                        await ExpandOverviewPanelAsync();
                    }
                    
                    StatusText.Text = "Ready";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error loading details: {ex.Message}";
            }
        }

        private void PopulateDetailPanel(HubResourceDetail detail)
        {
            // Set basic info
            DetailTitle.Text = detail.Title ?? "";
            DetailCreator.Text = detail.Creator ?? "Unknown";
            DetailCreator.Tag = detail.Creator;  // Store creator name for filter click
            
            // Restore blue styling for normal package details (for user filtering)
            DetailCreator.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4A90D9"));
            DetailCreator.TextDecorations = TextDecorations.Underline;
            DetailCreator.Cursor = Cursors.Hand;  // Clickable
            DetailCreator.ToolTip = "Click to filter by this creator";
            
            // Category (Type) with filter link
            if (!string.IsNullOrEmpty(detail.Type))
            {
                DetailCategory.Text = detail.Type;
                DetailCategory.Tag = detail.Type;  // Store for filter click
                DetailCategory.Visibility = Visibility.Visible;
            }
            else
            {
                DetailCategory.Visibility = Visibility.Collapsed;
            }
            
            // Tag line
            if (!string.IsNullOrEmpty(detail.TagLine))
            {
                DetailTagLine.Text = detail.TagLine;
                DetailTagLine.Visibility = Visibility.Visible;
            }
            else
            {
                DetailTagLine.Visibility = Visibility.Collapsed;
            }
            
            // Creator icon
            if (!string.IsNullOrEmpty(detail.IconUrl))
            {
                try
                {
                    DetailCreatorIconBrush.ImageSource = new BitmapImage(new Uri(detail.IconUrl));
                    DetailCreatorIcon.Visibility = Visibility.Visible;
                }
                catch (Exception)
                {
                    // Handle exception
                }
            }
            else
            {
                DetailCreatorIcon.Visibility = Visibility.Collapsed;
            }
            
            // Stats
            DetailDownloads.Text = $"⬇ {detail.DownloadCount}";
            DetailRating.Text = $"⭐ {detail.RatingDisplay}";
            
            // Dependencies
            if (detail.HasDependencies)
            {
                DetailDependencies.Text = $"📦 {detail.DependencyDisplay}";
                DetailDependencies.Visibility = Visibility.Visible;
            }
            else
            {
                DetailDependencies.Visibility = Visibility.Collapsed;
            }
            
            // File size
            if (!string.IsNullOrEmpty(detail.FileSizeDisplay))
            {
                DetailFileSize.Text = $"📁 {detail.FileSizeDisplay}";
                DetailFileSize.Visibility = Visibility.Visible;
            }
            else
            {
                DetailFileSize.Visibility = Visibility.Collapsed;
            }
            
            // Last update
            if (!string.IsNullOrEmpty(detail.LastUpdateDisplay))
            {
                DetailLastUpdate.Text = $"🕐 {detail.LastUpdateDisplay}";
                DetailLastUpdate.Visibility = Visibility.Visible;
            }
            else
            {
                DetailLastUpdate.Visibility = Visibility.Collapsed;
            }
            
            
            // Tags (populate if available from API) - single row, comma-separated with clickable hyperlinks
            try
            {
                if (detail.HasTags && detail.TagsList.Count > 0)
                {
                    DetailTagsPanel.Visibility = Visibility.Visible;
                    DetailTagsPanel.Inlines.Clear();
                    DetailTagsPanel.Inlines.Add(new Run("Tags: "));
                    
                    for (int i = 0; i < detail.TagsList.Count; i++)
                    {
                        var tag = detail.TagsList[i];
                        if (!string.IsNullOrWhiteSpace(tag))
                        {
                            // Create a clickable hyperlink for the tag
                            var hyperlink = new Hyperlink(new Run(tag))
                            {
                                Foreground = new SolidColorBrush(Color.FromRgb(74, 144, 226)),  // #4A90E2
                                TextDecorations = TextDecorations.Underline,
                                Tag = tag
                            };
                            hyperlink.Click += (s, e) => DetailTag_Click(s, new RoutedEventArgs());
                            DetailTagsPanel.Inlines.Add(hyperlink);
                            
                            // Add comma separator if not the last tag
                            if (i < detail.TagsList.Count - 1)
                            {
                                DetailTagsPanel.Inlines.Add(new Run(", "));
                            }
                        }
                    }
                }
                else
                {
                    DetailTagsPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
                DetailTagsPanel.Visibility = Visibility.Collapsed;
            }
            
            // Badges
            DetailInLibraryBadge.Visibility = detail.InLibrary ? Visibility.Visible : Visibility.Collapsed;
            DetailUpdateBadge.Visibility = detail.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
            DetailExternalBadge.Visibility = detail.IsExternallyHosted ? Visibility.Visible : Visibility.Collapsed;
            
            // Show/hide promotional link button
            var hasPromoLink = !string.IsNullOrEmpty(detail.PromotionalLink) && 
                               detail.PromotionalLink != "null";
            SupportCreatorButton.Visibility = hasPromoLink ? Visibility.Visible : Visibility.Collapsed;
            SupportCreatorButton.Tag = hasPromoLink ? detail.PromotionalLink : null;
            
            // Show image border for regular packages
            DetailImageBorder.Visibility = Visibility.Visible;
            
            // Load image asynchronously for fast UI response
            LoadDetailImageAsync(detail.ImageUrl);
            
            // Build files list
            _currentFiles.Clear();
            
            // Main package files
            if (detail.HubFiles != null)
            {
                foreach (var file in detail.HubFiles)
                {
                    // Skip files with null or empty filenames
                    if (!string.IsNullOrEmpty(file.Filename))
                    {
                        _currentFiles.Add(CreateFileViewModel(file, false));
                    }
                }
            }
            
            // Dependencies
            _currentDependencies.Clear();
            if (detail.Dependencies != null)
            {
                foreach (var depGroup in detail.Dependencies.Values)
                {
                    foreach (var file in depGroup)
                    {
                        // Skip files with null or empty filenames
                        if (!string.IsNullOrEmpty(file.Filename))
                        {
                            _currentDependencies.Add(CreateFileViewModel(file, true));
                        }
                    }
                }
            }
            
            DetailFilesControl.ItemsSource = _currentFiles;
            
            if (_currentDependencies.Any())
            {
                DependenciesHeader.Visibility = Visibility.Visible;
                DetailDependenciesControl.ItemsSource = _currentDependencies;
            }
            else
            {
                DependenciesHeader.Visibility = Visibility.Collapsed;
                DetailDependenciesControl.ItemsSource = null;
            }
            
            UpdateDownloadAllButton();
            UpdateCancelAllButtonVisibility();
        }

        private HubFileViewModel CreateFileViewModel(HubFile file, bool isDependency)
        {
            // For .latest dependencies, resolve to actual latest version
            var filename = file.Filename;
            var downloadUrl = file.EffectiveDownloadUrl;
            
            
            // Check for .latest at the end or .latest. in the middle
            if (filename.Contains(".latest"))
            {
                // Try to get version from LatestVersion property first
                var latestVersion = file.LatestVersion;
                
                // If not available, try to extract from LatestUrl
                if (string.IsNullOrEmpty(latestVersion) && !string.IsNullOrEmpty(file.LatestUrl))
                {
                    latestVersion = ExtractVersionFromUrl(file.LatestUrl, file.Filename);
                }
                
                // If still not available, try to extract from downloadUrl
                if (string.IsNullOrEmpty(latestVersion) && !string.IsNullOrEmpty(downloadUrl) && downloadUrl != "null")
                {
                    latestVersion = ExtractVersionFromUrl(downloadUrl, file.Filename);
                }
                
                // If we found a version, replace .latest with actual version
                if (!string.IsNullOrEmpty(latestVersion))
                {
                    // Handle both .latest. (middle) and .latest (end) patterns
                    if (filename.Contains(".latest."))
                    {
                        filename = filename.Replace(".latest.", $".{latestVersion}.");
                    }
                    else
                    {
                        // Replace .latest at the end
                        filename = filename.Replace(".latest", $".{latestVersion}");
                    }
                    
                    // Use LatestUrl if available, otherwise keep downloadUrl
                    if (!string.IsNullOrEmpty(file.LatestUrl) && file.LatestUrl != "null")
                    {
                        downloadUrl = file.LatestUrl;
                    }
                }
                else
                {
                    // Could not resolve .latest version - keep original filename
                    // but ensure downloadUrl is consistent (use LatestUrl if available)
                    if (!string.IsNullOrEmpty(file.LatestUrl) && file.LatestUrl != "null")
                    {
                        downloadUrl = file.LatestUrl;
                    }
                }
            }
            
            var vm = new HubFileViewModel
            {
                Filename = filename,
                FileSize = file.FileSize,
                DownloadUrl = downloadUrl,
                LatestUrl = file.LatestUrl,
                IsDependency = isDependency,
                HubFile = file
            };
            
            // Check if already downloaded - use FindLocalPackage which verifies file existence
            var packageName = filename.Replace(".var", "");
            var originalPackageName = file.PackageName;
            
            
            // Find local path if installed - try resolved name first, then original
            var localPath = FindLocalPackage(packageName);
            if (localPath == null && packageName != originalPackageName)
            {
                localPath = FindLocalPackage(originalPackageName);
            }
            
            if (localPath != null)
            {
                vm.IsInstalled = true;
                vm.LocalPath = localPath;
                
                // Check if there's an update available
                // CRITICAL: Use the pre-computed _localPackageVersions dictionary
                // which already contains the HIGHEST version for each base package name.
                // This was built in BuildLocalPackageLookups() at startup.
                var localPackageName = Path.GetFileNameWithoutExtension(localPath);
                var basePackageName = GetBasePackageName(localPackageName);
                
                // Get the highest local version from our pre-computed lookup
                var localVersion = _localPackageVersions.TryGetValue(basePackageName, out var highestVersion) 
                    ? highestVersion 
                    : ExtractVersionNumber(localPackageName);
                
                // Get latest version from Hub API
                // Try multiple sources: LatestVersion property, Version property, or extract from filename
                int hubLatestVersion = -1;
                
                // 1. Try LatestVersion property (used for dependencies)
                if (!string.IsNullOrEmpty(file.LatestVersion) && int.TryParse(file.LatestVersion, out var parsedLatest))
                {
                    hubLatestVersion = parsedLatest;
                }
                // 2. Try Version property (used for main package files)
                else if (!string.IsNullOrEmpty(file.Version) && int.TryParse(file.Version, out var parsedVersion))
                {
                    hubLatestVersion = parsedVersion;
                }
                // 3. Extract from the Hub filename (the filename on Hub represents the latest version)
                else
                {
                    hubLatestVersion = ExtractVersionNumber(file.Filename);
                }
                
                
                if (hubLatestVersion > 0 && localVersion > 0 && hubLatestVersion > localVersion)
                {
                    // Update available!
                    vm.Status = $"Update {localVersion} → {hubLatestVersion}";
                    vm.StatusColor = new SolidColorBrush(Colors.Orange);
                    vm.CanDownload = true;
                    vm.ButtonText = "⬆";
                    vm.HasUpdate = true;
                }
                else
                {
                    vm.Status = "✓ In Library";
                    vm.StatusColor = new SolidColorBrush(Colors.LimeGreen);
                    vm.CanDownload = false;
                    vm.ButtonText = "✓";
                }
            }
            else if (string.IsNullOrEmpty(vm.DownloadUrl))
            {
                vm.Status = "Not available";
                vm.StatusColor = new SolidColorBrush(Colors.Gray);
                vm.CanDownload = false;
                vm.ButtonText = "N/A";
            }
            else
            {
                vm.Status = "Ready to download";
                vm.StatusColor = new SolidColorBrush(Colors.White);
                vm.CanDownload = true;
                vm.ButtonText = "⬇";
            }
            
            return vm;
        }
        
        /// <summary>
        /// Extracts the version number from a package name
        /// </summary>
        private int ExtractVersionNumber(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return -1;
            
            var name = packageName;
            
            // Remove .var extension if present
            if (name.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            
            // Handle .latest - no numeric version
            if (name.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                return -1;
            
            // Get version number from the end
            var lastDot = name.LastIndexOf('.');
            if (lastDot > 0)
            {
                var afterDot = name.Substring(lastDot + 1);
                if (int.TryParse(afterDot, out var version))
                {
                    return version;
                }
            }
            
            return -1;
        }
        
        /// <summary>
        /// Gets the base package name without version (Creator.PackageName)
        /// Uses the same logic as VB's PackageIDToPackageGroupID:
        /// - Removes .{version} (digits) from the end
        /// - Removes .latest from the end
        /// </summary>
        private string GetBasePackageName(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return packageName;
                
            var name = packageName;
            
            // Remove .var extension if present
            if (name.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            
            // Remove .latest suffix if present
            if (name.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 7);
            
            // Remove version number (digits at the end after last dot)
            var lastDot = name.LastIndexOf('.');
            if (lastDot > 0)
            {
                var afterDot = name.Substring(lastDot + 1);
                if (int.TryParse(afterDot, out _))
                {
                    return name.Substring(0, lastDot);
                }
            }
            
            return name;
        }
        
        /// <summary>
        /// Derives the VaM root folder from a destination folder path.
        /// Walks up the directory tree looking for a folder that contains known VaM subfolders.
        /// </summary>
        private static string DeriveVamFolder(string destinationFolder)
        {
            if (string.IsNullOrEmpty(destinationFolder))
                return destinationFolder;
            
            // Known VaM folder names that indicate we're at the root
            var vamIndicators = new[] { "Custom", "Saves", "VaM.exe", "VaM_Data" };
            
            var current = destinationFolder;
            
            // Walk up the directory tree
            while (!string.IsNullOrEmpty(current))
            {
                var parent = Path.GetDirectoryName(current);
                
                if (string.IsNullOrEmpty(parent))
                    break;
                
                // Check if parent contains VaM indicators
                try
                {
                    foreach (var indicator in vamIndicators)
                    {
                        var testPath = Path.Combine(parent, indicator);
                        if (Directory.Exists(testPath) || File.Exists(testPath))
                        {
                            // Found VaM root
                            return parent;
                        }
                    }
                }
                catch (Exception)
                {
                    // Handle exception
                }
                
                current = parent;
            }
            
            // Fallback: just go up one level from destination
            return Path.GetDirectoryName(destinationFolder) ?? destinationFolder;
        }
        
        /// <summary>
        /// Extracts version number from a download URL
        /// URL format typically: .../Creator.PackageName.Version.var
        /// </summary>
        private string ExtractVersionFromUrl(string url, string originalFilename)
        {
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }
            
            try
            {
                // Get the filename from URL
                var uri = new Uri(url);
                var urlFilename = Path.GetFileName(uri.LocalPath);
                
                if (string.IsNullOrEmpty(urlFilename))
                {
                    return null;
                }
                
                // Remove .var extension
                urlFilename = urlFilename.Replace(".var", "");
                
                // Get base package name (Creator.PackageName)
                var baseName = GetBasePackageName(originalFilename.Replace(".var", "").Replace(".latest", ""));
                
                // Extract version - everything after the base name
                if (urlFilename.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    var version = urlFilename.Substring(baseName.Length + 1);
                    
                    // Validate it looks like a version (numeric)
                    if (!string.IsNullOrEmpty(version) && char.IsDigit(version[0]))
                    {
                        return version;
                    }
                }
            }
            catch (Exception)
            {
                // Handle exception
            }
            return null;
        }

        private void UpdateDownloadAllButton()
        {
            // Don't update if batch download is in progress
            if (_totalDownloadsInBatch > 0 && _completedDownloadsInBatch < _totalDownloadsInBatch)
                return;
                
            var downloadableFiles = _currentFiles.Count(f => f.CanDownload);
            var downloadableDeps = _currentDependencies.Count(f => f.CanDownload);
            var totalDownloadable = downloadableFiles + downloadableDeps;
            
            // Make sure button is visible and progress is hidden
            DownloadAllButton.Visibility = Visibility.Visible;
            DownloadProgressContainer.Visibility = Visibility.Collapsed;
            
            DownloadAllButton.IsEnabled = totalDownloadable > 0;
            DownloadAllButton.Content = totalDownloadable > 0 
                ? $"⬇ Download All ({totalDownloadable})" 
                : "✓ All Installed";
        }

        private void ExpandPanel()
        {
            if (!_isPanelExpanded)
            {
                DetailPanelColumn.Width = new GridLength(PanelWidth);
                DetailPanelSplitter.Visibility = Visibility.Visible;
                TogglePanelButton.Content = "▶";
                TogglePanelButton.ToolTip = "Hide details panel";
                _isPanelExpanded = true;
            }
        }

        private void CollapsePanel()
        {
            if (_isPanelExpanded)
            {
                DetailPanelColumn.Width = new GridLength(0);
                DetailPanelSplitter.Visibility = Visibility.Collapsed;
                TogglePanelButton.Content = "◀";
                TogglePanelButton.ToolTip = "Show details panel";
                _isPanelExpanded = false;
            }
        }

        private void ClosePanel_Click(object sender, RoutedEventArgs e)
        {
            CollapsePanel();
        }

        private void TogglePanel_Click(object sender, RoutedEventArgs e)
        {
            if (_isPanelExpanded)
            {
                CollapsePanel();
            }
            else if (_currentDetail != null)
            {
                ExpandPanel();
            }
        }
        
        /// <summary>
        /// Navigate WebView2 to the appropriate Hub page
        /// </summary>
        private async Task NavigateToHubPage(string tabName)
        {
            if (string.IsNullOrEmpty(_currentResourceId))
                return;
            
            // Initialize WebView2 if needed
            if (!_webViewInitialized)
            {
                WebViewLoadingOverlay.Visibility = Visibility.Visible;
                await InitializeWebViewAsync();
                
                if (!_webViewInitialized)
                {
                    ShowWebViewError("WebView2 is not available. Please install the WebView2 Runtime.");
                    return;
                }
            }
            
            // Build the URL based on tab
            string url = tabName switch
            {
                "TabOverview" => $"https://hub.virtamate.com/resources/{_currentResourceId}/overview-panel",
                "TabUpdates" => $"https://hub.virtamate.com/resources/{_currentResourceId}/updates-panel",
                "TabReviews" => $"https://hub.virtamate.com/resources/{_currentResourceId}/review-panel",
                "TabDiscussion" => GetDiscussionUrl(),
                _ => null
            };
            
            if (string.IsNullOrEmpty(url))
            {
                ShowWebViewError("Unable to determine page URL");
                return;
            }
            
            _currentWebViewUrl = url;
            
            try
            {
                // Hide placeholder, show loading
                OverviewPlaceholder.Visibility = Visibility.Collapsed;
                WebViewLoadingOverlay.Visibility = Visibility.Visible;
                WebViewErrorPanel.Visibility = Visibility.Collapsed;
                OverviewWebView.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                ShowWebViewError($"Navigation failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get the discussion thread URL for the current resource
        /// </summary>
        private string GetDiscussionUrl()
        {
            // Discussion uses thread ID, not resource ID
            // For now, use the resource page which has a link to discussion
            if (!string.IsNullOrEmpty(_currentDetail?.DiscussionThreadId))
            {
                return $"https://hub.virtamate.com/threads/{_currentDetail.DiscussionThreadId}/discussion-panel";
            }
            
            // Fallback to resource overview
            return $"https://hub.virtamate.com/resources/{_currentResourceId}/overview-panel";
        }

        private void DownloadFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is HubFileViewModel file)
            {
                // If downloading, cancel the download
                if (file.IsDownloading)
                {
                    CancelFileDownload(file);
                    return;
                }
                
                // If has update available, download the update
                if (file.HasUpdate && file.CanDownload)
                {
                    QueueFileForDownload(file);
                    return;
                }
                
                // If already installed (no update), open in Explorer
                if (file.IsInstalled && !file.HasUpdate)
                {
                    if (!string.IsNullOrEmpty(file.LocalPath) && File.Exists(file.LocalPath))
                    {
                        try
                        {
                            Process.Start("explorer.exe", $"/select,\"{file.LocalPath}\"");
                        }
                        catch (Exception)
                        {
                            // Handle exception
                        }
                    }
                    else
                    {
                        // Try to find the file in destination folder
                        var possiblePath = Path.Combine(_destinationFolder, file.Filename);
                        if (File.Exists(possiblePath))
                        {
                            Process.Start("explorer.exe", $"/select,\"{possiblePath}\"");
                        }
                        else
                        {
                            // Just open the destination folder
                            Process.Start("explorer.exe", _destinationFolder);
                        }
                    }
                    return;
                }
                
                // Skip if not downloadable (N/A items)
                if (!file.CanDownload || string.IsNullOrEmpty(file.DownloadUrl))
                    return;
                
                QueueFileForDownload(file);
            }
        }

        private void DownloadAll_Click(object sender, RoutedEventArgs e)
        {
            // Collect all files to download
            var toDownload = _currentFiles.Where(f => f.CanDownload).ToList();
            var depsToDownload = _currentDependencies.Where(f => f.CanDownload).ToList();
            var allToDownload = toDownload.Concat(depsToDownload).ToList();
            
            if (allToDownload.Count == 0)
                return;
            
            // Initialize batch progress tracking
            _totalDownloadsInBatch = allToDownload.Count;
            _completedDownloadsInBatch = 0;
            _currentDownloadingPackage = "";
            
            // Show progress bar (which includes cancel button), hide download button
            DownloadAllButton.Visibility = Visibility.Collapsed;
            DownloadProgressContainer.Visibility = Visibility.Visible;
            UpdateBatchProgressUI();
            
            // Queue all files
            foreach (var file in allToDownload)
            {
                QueueFileForDownload(file);
            }
        }
        
        private void CancelAllDetailDownloads_Click(object sender, RoutedEventArgs e)
        {
            // Cancel all downloads in the current detail panel
            foreach (var file in _currentFiles.Where(f => f.IsDownloading))
            {
                CancelFileDownload(file);
            }
            foreach (var file in _currentDependencies.Where(f => f.IsDownloading))
            {
                CancelFileDownload(file);
            }
            
            // Also cancel any queued downloads
            _hubService.CancelAllDownloads();
            
            // Reset batch progress
            _totalDownloadsInBatch = 0;
            _completedDownloadsInBatch = 0;
            
            // Hide progress bar (which includes cancel button), show download button
            DownloadAllButton.Visibility = Visibility.Visible;
            DownloadProgressContainer.Visibility = Visibility.Collapsed;
            
            UpdateDownloadQueueUI();
            UpdateDownloadAllButton();
        }
        
        /// <summary>
        /// Update the Cancel All button visibility based on active downloads
        /// </summary>
        private void UpdateCancelAllButtonVisibility()
        {
            // Cancel button is now inside progress container, so we control visibility via the container
            var hasActiveDownloads = (_currentFiles?.Any(f => f.IsDownloading) ?? false) || 
                                     (_currentDependencies?.Any(f => f.IsDownloading) ?? false);
            // Show progress container if there are active downloads (it contains the cancel button)
            if (!hasActiveDownloads && _totalDownloadsInBatch == 0)
            {
                DownloadProgressContainer.Visibility = Visibility.Collapsed;
                DownloadAllButton.Visibility = Visibility.Visible;
            }
        }
        
        private void UpdateBatchProgressUI()
        {
            if (_totalDownloadsInBatch == 0)
            {
                // No downloads - show button
                DownloadAllButton.Visibility = Visibility.Visible;
                DownloadProgressContainer.Visibility = Visibility.Collapsed;
                return;
            }
            
            var percent = (_completedDownloadsInBatch * 100) / _totalDownloadsInBatch;
            DownloadAllProgressBar.Value = percent;
            
            DownloadProgressText.Text = $"Downloading {_completedDownloadsInBatch + 1}/{_totalDownloadsInBatch}";
            DownloadProgressDetail.Text = string.IsNullOrEmpty(_currentDownloadingPackage) 
                ? "Starting..." 
                : _currentDownloadingPackage;
        }
        
        private void OnBatchDownloadComplete()
        {
            _totalDownloadsInBatch = 0;
            _completedDownloadsInBatch = 0;
            _currentDownloadingPackage = "";
            
            // Progress container (with cancel button) will be hidden after delay
            
            // Show completed state briefly, then revert to button
            DownloadProgressText.Text = "✓ All Downloads Complete";
            DownloadProgressDetail.Text = "";
            DownloadAllProgressBar.Value = 100;
            
            // After a delay, check if we should show button or "All Installed"
            Task.Delay(1500).ContinueWith(_ =>
            {
                // Use BeginInvoke to prevent UI blocking
                Dispatcher.BeginInvoke(() =>
                {
                    DownloadProgressContainer.Visibility = Visibility.Collapsed;
                    UpdateDownloadAllButton();
                });
            });
        }

        // Track file view models by package name for queue updates
        private Dictionary<string, HubFileViewModel> _downloadingFiles = new Dictionary<string, HubFileViewModel>(StringComparer.OrdinalIgnoreCase);
        
        /// <summary>
        /// Cancel a file download that is in progress
        /// </summary>
        private void CancelFileDownload(HubFileViewModel file)
        {
            // Find the queued download for this file
            var packageName = file.Filename?.Replace(".var", "") ?? "";
            
            // Find matching download in the queue
            var queuedDownload = _downloadQueue.FirstOrDefault(d => 
                d.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase) ||
                d.PackageName.Contains(packageName, StringComparison.OrdinalIgnoreCase));
            
            if (queuedDownload != null)
            {
                _hubService.CancelDownload(queuedDownload);
                UpdateDownloadQueueUI();
            }
            else
            {
                // Fallback: just reset the UI state
                file.Status = "Cancelled";
                file.StatusColor = new SolidColorBrush(Colors.Gray);
                file.IsDownloading = false;
                file.CanDownload = true;
                file.ButtonText = "⬇";
            }
        }
        
        private void QueueFileForDownload(HubFileViewModel file)
        {
            // Determine which URL to use:
            // - For dependency updates: use LatestUrl if available
            // - For main package updates: DownloadUrl already points to latest version
            // - Otherwise: use DownloadUrl
            var downloadUrl = file.HasUpdate && !string.IsNullOrEmpty(file.LatestUrl) 
                ? file.LatestUrl 
                : file.DownloadUrl;
                
            if (!file.CanDownload || string.IsNullOrEmpty(downloadUrl))
                return;

            // Get the package name from the download URL
            string packageName;
            try
            {
                var uri = new Uri(downloadUrl);
                var urlFilename = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrEmpty(urlFilename) && urlFilename.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                {
                    packageName = urlFilename.Replace(".var", "");
                }
                else
                {
                    packageName = file.Filename.Replace(".var", "");
                }
            }
            catch (Exception)
            {
                packageName = file.Filename.Replace(".var", "");
            }
            
            // Update UI to show queued state
            file.Status = "Queued...";
            file.StatusColor = new SolidColorBrush(Colors.Cyan);
            file.CanDownload = false;
            file.ButtonText = "⏳";
            
            // Track this file for queue updates
            _downloadingFiles[packageName] = file;
            
            // Queue the download
            var queuedDownload = _hubService.QueueDownload(downloadUrl, _destinationFolder, packageName, file.FileSize);
            
            // Subscribe to property changes on the queued download to update file UI
            queuedDownload.PropertyChanged += (s, e) =>
            {
                // Use BeginInvoke to prevent UI blocking - progress updates are frequent
                Dispatcher.BeginInvoke(() =>
                {
                    if (e.PropertyName == nameof(QueuedDownload.Status))
                    {
                        switch (queuedDownload.Status)
                        {
                            case DownloadStatus.Downloading:
                                file.Status = file.HasUpdate ? "Updating..." : "Downloading...";
                                file.StatusColor = new SolidColorBrush(Colors.Yellow);
                                file.IsDownloading = true;
                                file.ButtonText = "✕";  // Show cancel button
                                
                                // Progress container (with cancel button) is already visible
                                
                                // Update batch progress - show current package
                                _currentDownloadingPackage = packageName;
                                UpdateBatchProgressUI();
                                break;
                                
                            case DownloadStatus.Completed:
                                var downloadedFilename = packageName + ".var";
                                var downloadedPath = Path.Combine(_destinationFolder, downloadedFilename);
                                
                                // Check if this was an update before clearing the flag
                                bool wasUpdate = file.HasUpdate;
                                
                                file.Status = wasUpdate ? "✓ Updated" : "✓ Downloaded";
                                file.StatusColor = new SolidColorBrush(Colors.LimeGreen);
                                file.ButtonText = "✓";
                                file.IsDownloading = false;
                                file.IsInstalled = true;
                                file.HasUpdate = false;
                                file.LocalPath = downloadedPath;
                                file.Filename = downloadedFilename;
                                
                                _localPackagePaths[packageName] = downloadedPath;
                                
                                // CRITICAL FIX: Update PackageManager metadata so that when the Hub window is reopened,
                                // it will correctly detect that this package is already downloaded and not show it as available
                                if (_packageManager != null && System.IO.File.Exists(downloadedPath))
                                {
                                    try
                                    {
                                        // Parse the downloaded package's metadata
                                        var metadata = _packageManager.ParseVarMetadataComplete(downloadedPath);
                                        if (metadata != null)
                                        {
                                            metadata.FilePath = downloadedPath;
                                            metadata.Status = "Loaded";
                                            _packageManager.PackageMetadata[packageName] = metadata;
                                            
                                            // Rebuild local package lookups so future checks use the updated data
                                            BuildLocalPackageLookups();
                                        }
                                        
                                        // CRITICAL FIX: Remove this package from all MissingDependencies lists
                                        // so that the missing dependencies panel updates correctly after download
                                        _packageManager.RemoveFromMissingDependencies(packageName);
                                    }
                                    catch (Exception)
                                    {
                                        // If parsing fails, at least update the lookups from _localPackagePaths
                                        BuildLocalPackageLookups();
                                        
                                        // Still try to remove from missing dependencies
                                        _packageManager.RemoveFromMissingDependencies(packageName);
                                    }
                                }
                                
                                if (_currentResource != null)
                                {
                                    _currentResource.InLibrary = true;
                                    _currentResource.UpdateAvailable = false;
                                }
                                
                                _downloadingFiles.Remove(packageName);
                                
                                // Handle old versions if this was an update
                                if (wasUpdate)
                                {
                                    HandleOldVersions(packageName);
                                }
                                
                                // Update missing dependencies panel if we're viewing it
                                UpdateMissingDepsPanelAfterDownload(packageName);
                                
                                // Update batch progress
                                _completedDownloadsInBatch++;
                                if (_completedDownloadsInBatch >= _totalDownloadsInBatch && _totalDownloadsInBatch > 0)
                                {
                                    OnBatchDownloadComplete();
                                }
                                else
                                {
                                    UpdateBatchProgressUI();
                                }
                                break;
                                
                            case DownloadStatus.Failed:
                                file.Status = "Download failed";
                                file.StatusColor = new SolidColorBrush(Colors.Red);
                                file.IsDownloading = false;
                                file.CanDownload = true;
                                file.ButtonText = "⬇";
                                _downloadingFiles.Remove(packageName);
                                
                                // Hide cancel button if no more active downloads
                                UpdateCancelAllButtonVisibility();
                                
                                // Update batch progress (count as completed for progress purposes)
                                _completedDownloadsInBatch++;
                                if (_completedDownloadsInBatch >= _totalDownloadsInBatch && _totalDownloadsInBatch > 0)
                                {
                                    OnBatchDownloadComplete();
                                }
                                else
                                {
                                    UpdateBatchProgressUI();
                                }
                                break;
                                
                            case DownloadStatus.Cancelled:
                                file.Status = "Cancelled";
                                file.StatusColor = new SolidColorBrush(Colors.Gray);
                                file.IsDownloading = false;
                                file.CanDownload = true;
                                file.ButtonText = "⬇";
                                _downloadingFiles.Remove(packageName);
                                
                                // Hide cancel button if no more active downloads
                                UpdateCancelAllButtonVisibility();
                                
                                // Update batch progress (count as completed for progress purposes)
                                _completedDownloadsInBatch++;
                                if (_completedDownloadsInBatch >= _totalDownloadsInBatch && _totalDownloadsInBatch > 0)
                                {
                                    OnBatchDownloadComplete();
                                }
                                else
                                {
                                    UpdateBatchProgressUI();
                                }
                                break;
                        }
                    }
                    else if (e.PropertyName == nameof(QueuedDownload.ProgressPercentage))
                    {
                        if (queuedDownload.Status == DownloadStatus.Downloading)
                        {
                            file.Status = file.HasUpdate 
                                ? $"Updating... {queuedDownload.ProgressPercentage}%" 
                                : $"Downloading... {queuedDownload.ProgressPercentage}%";
                        }
                    }
                });
            };

            UpdateDownloadAllButton();
        }

        #endregion
        
        #region Old Version Handling
        
        /// <summary>
        /// Handle old versions of a package based on the selected option
        /// </summary>
        private void HandleOldVersions(string packageName)
        {
            if (_oldVersionHandling == "No Change")
                return;
            
            try
            {
                var basePackageName = GetBasePackageName(packageName);
                var currentVersion = ExtractVersionNumber(packageName);
                
                if (currentVersion <= 0)
                    return;
                
                // Find all old versions of this package
                var oldVersions = new List<string>();
                foreach (var pkg in _localPackagePaths.Keys.ToList())
                {
                    var pkgBase = GetBasePackageName(pkg);
                    if (pkgBase.Equals(basePackageName, StringComparison.OrdinalIgnoreCase))
                    {
                        var pkgVersion = ExtractVersionNumber(pkg);
                        if (pkgVersion > 0 && pkgVersion < currentVersion)
                        {
                            oldVersions.Add(pkg);
                        }
                    }
                }
                
                if (oldVersions.Count == 0)
                    return;
                
                if (_oldVersionHandling == "Archive All Old")
                {
                    ArchiveAllOldVersions(oldVersions);
                }
                else if (_oldVersionHandling == "Discard All Old")
                {
                    DiscardAllOldVersions(oldVersions);
                }
            }
            catch (Exception)
            {
                // Handle exception
            }
        }
        
        /// <summary>
        /// Archive all old versions to \ArchivedPackages\OldPackages\ in the game folder
        /// </summary>
        private void ArchiveAllOldVersions(List<string> oldVersionPackages)
        {
            try
            {
                // Create archive path in game folder: \ArchivedPackages\OldPackages\
                var archiveFolder = Path.Combine(_vamFolder, "ArchivedPackages", "OldPackages");
                Directory.CreateDirectory(archiveFolder);
                
                foreach (var packageName in oldVersionPackages)
                {
                    if (_localPackagePaths.TryGetValue(packageName, out var filePath))
                    {
                        try
                        {
                            // Check file exists before attempting move
                            if (!File.Exists(filePath))
                            {
                                _localPackagePaths.Remove(packageName);
                                continue;
                            }
                            
                            var filename = Path.GetFileName(filePath);
                            var archivePath = Path.Combine(archiveFolder, filename);
                            
                            // If file already exists in archive, delete it first
                            try
                            {
                                if (File.Exists(archivePath))
                                {
                                    File.Delete(archivePath);
                                }
                            }
                            catch (Exception)
                            {
                                // Handle exception
                            }
                            
                            File.Move(filePath, archivePath);
                            _localPackagePaths.Remove(packageName);
                        }
                        catch (Exception)
                        {
                            // Handle exception
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Handle exception
            }
        }
        
        /// <summary>
        /// Move all old versions to \DiscardedPackages\ in the game folder
        /// </summary>
        private void DiscardAllOldVersions(List<string> oldVersionPackages)
        {
            try
            {
                // Create discard path in game folder: \DiscardedPackages\
                var discardFolder = Path.Combine(_vamFolder, "DiscardedPackages");
                Directory.CreateDirectory(discardFolder);
                
                foreach (var packageName in oldVersionPackages)
                {
                    if (_localPackagePaths.TryGetValue(packageName, out var filePath))
                    {
                        try
                        {
                            // Check file exists before attempting move
                            if (!File.Exists(filePath))
                            {
                                _localPackagePaths.Remove(packageName);
                                continue;
                            }
                            
                            var filename = Path.GetFileName(filePath);
                            var discardPath = Path.Combine(discardFolder, filename);
                            
                            // If file already exists in discard folder, delete it first
                            try
                            {
                                if (File.Exists(discardPath))
                                {
                                    File.Delete(discardPath);
                                }
                            }
                            catch (Exception)
                            {
                                // Handle exception
                            }
                            
                            File.Move(filePath, discardPath);
                            _localPackagePaths.Remove(packageName);
                        }
                        catch (Exception)
                        {
                            // Handle exception
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Handle exception
            }
        }
        
        #endregion
        
        #region Download Queue
        
        private void HubService_DownloadQueued(object sender, QueuedDownload download)
        {
            // Use BeginInvoke to prevent UI blocking
            Dispatcher.BeginInvoke(() =>
            {
                _downloadQueue.Add(download);
                UpdateDownloadQueueUI();
            });
        }
        
        private void HubService_DownloadStarted(object sender, QueuedDownload download)
        {
            // Use BeginInvoke to prevent UI blocking
            Dispatcher.BeginInvoke(() =>
            {
                UpdateDownloadQueueUI();
            });
        }
        
        private void HubService_DownloadCompleted(object sender, QueuedDownload download)
        {
            // Use BeginInvoke to prevent UI blocking
            Dispatcher.BeginInvoke(() =>
            {
                // Remove completed/cancelled/failed downloads after a short delay
                if (download.Status == DownloadStatus.Completed || 
                    download.Status == DownloadStatus.Cancelled ||
                    download.Status == DownloadStatus.Failed)
                {
                    // Keep in list briefly so user can see final status
                    Task.Delay(2000).ContinueWith(_ =>
                    {
                        // Use BeginInvoke to prevent UI blocking
                        Dispatcher.BeginInvoke(() =>
                        {
                            _downloadQueue.Remove(download);
                            UpdateDownloadQueueUI();
                        });
                    });
                }
                UpdateDownloadQueueUI();
            });
        }
        
        private void UpdateDownloadQueueUI()
        {
            var activeCount = _downloadQueue.Count(d => d.Status == DownloadStatus.Queued || d.Status == DownloadStatus.Downloading);
            
            DownloadQueueCountText.Text = activeCount.ToString();
            DownloadQueueButton.Visibility = activeCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            
            // Update Cancel All button visibility
            CancelAllDownloadsButton.Visibility = _downloadQueue.Any(d => d.CanCancel) ? Visibility.Visible : Visibility.Collapsed;
            
            // Update Open Downloading button visibility (show if there are saved downloading details)
            OpenDownloadingButton.Visibility = _savedDownloadingDetails.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        
        private void DownloadQueueButton_Click(object sender, RoutedEventArgs e)
        {
            DownloadQueuePopup.IsOpen = !DownloadQueuePopup.IsOpen;
        }
        
        private void CloseDownloadQueuePopup_Click(object sender, RoutedEventArgs e)
        {
            DownloadQueuePopup.IsOpen = false;
        }
        
        private void CancelDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is QueuedDownload download)
            {
                _hubService.CancelDownload(download);
                UpdateDownloadQueueUI();
            }
        }
        
        private void CancelAllDownloads_Click(object sender, RoutedEventArgs e)
        {
            _hubService.CancelAllDownloads();
            UpdateDownloadQueueUI();
        }
        
        private void OpenDownloading_Click(object sender, RoutedEventArgs e)
        {
            // Close the popup first
            DownloadQueuePopup.IsOpen = false;
            
            // Open all saved downloading detail panels
            var keysToOpen = _savedDownloadingDetails.Keys.ToList();
            foreach (var resourceId in keysToOpen)
            {
                if (_savedDownloadingDetails.TryGetValue(resourceId, out var entry))
                {
                    _savedDownloadingDetails.Remove(resourceId);
                    _detailStack.Push(entry);
                }
            }
            
            // Restore the top of the stack if any
            if (_detailStack.Count > 0)
            {
                var top = _detailStack.Peek();
                RestoreDetailFromStack(top);
                ExpandPanel();
                UpdateDetailStackUI();
            }
        }
        
        #endregion
        
        #region Stack-Based Detail Navigation
        
        private void PushToDetailStack(HubResourceDetail detail, HubResource resource, 
            ObservableCollection<HubFileViewModel> files, ObservableCollection<HubFileViewModel> dependencies)
        {
            var resourceId = resource?.ResourceId;
            
            // Don't add duplicate if the same resource is already at the top of the stack
            if (_detailStack.Count > 0)
            {
                var top = _detailStack.Peek();
                if (top.ResourceId == resourceId && !string.IsNullOrEmpty(resourceId))
                {
                    // Same resource - just update the existing entry instead of pushing a new one
                    top.Detail = detail;
                    top.Resource = resource;
                    top.Files = new ObservableCollection<HubFileViewModel>(files);
                    top.Dependencies = new ObservableCollection<HubFileViewModel>(dependencies);
                    return;
                }
            }
            
            // Save current state to stack
            var entry = new DetailStackEntry
            {
                Detail = detail,
                Resource = resource,
                Files = new ObservableCollection<HubFileViewModel>(files),
                Dependencies = new ObservableCollection<HubFileViewModel>(dependencies),
                ResourceId = resourceId
            };
            
            _detailStack.Push(entry);
            UpdateDetailStackUI();
        }
        
        private bool HasActiveDownloads(DetailStackEntry entry)
        {
            if (entry.Files == null) return false;
            
            foreach (var file in entry.Files)
            {
                if (file.IsDownloading || file.Status == "Queued..." || file.Status?.Contains("Downloading") == true)
                    return true;
            }
            
            if (entry.Dependencies != null)
            {
                foreach (var dep in entry.Dependencies)
                {
                    if (dep.IsDownloading || dep.Status == "Queued..." || dep.Status?.Contains("Downloading") == true)
                        return true;
                }
            }
            
            return false;
        }
        
        private void RestoreDetailFromStack(DetailStackEntry entry)
        {
            _currentDetail = entry.Detail;
            _currentResource = entry.Resource;
            _currentResourceId = entry.ResourceId;
            
            // Restore files and dependencies
            _currentFiles.Clear();
            foreach (var file in entry.Files)
                _currentFiles.Add(file);
            
            _currentDependencies.Clear();
            foreach (var dep in entry.Dependencies)
                _currentDependencies.Add(dep);
            
            // Update UI
            PopulateDetailPanel(entry.Detail);
            
            // Navigate WebView if overview panel is visible
            if (_isOverviewPanelVisible && !string.IsNullOrEmpty(_currentResourceId))
            {
                _ = NavigateToHubPage("TabOverview");
            }
        }
        
        private void UpdateDetailStackUI()
        {
            var stackCount = _detailStack.Count;
            
            // Show stack indicator panel when there are items (even just 1 for visibility)
            if (stackCount >= 1)
            {
                DetailStackIndicator.Text = stackCount == 1 ? "(1 item)" : $"({stackCount} in stack)";
                DetailStackPanel.Visibility = Visibility.Visible;
            }
            else
            {
                DetailStackPanel.Visibility = Visibility.Collapsed;
            }
        }
        
        private void StackDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            // Build the list of stack items for the dropdown
            var stackItems = new List<StackDropdownItem>();
            var stackArray = _detailStack.ToArray();
            
            for (int i = 0; i < stackArray.Length; i++)
            {
                var entry = stackArray[i];
                var title = entry.Resource?.Title ?? entry.Detail?.Title ?? "Unknown";
                stackItems.Add(new StackDropdownItem
                {
                    Index = i,
                    Position = $"{i + 1}.",
                    Title = title,
                    IsCurrent = i == 0,
                    DisplayForeground = i == 0 ? new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9)) : new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                    DisplayFontWeight = i == 0 ? FontWeights.Bold : FontWeights.Normal
                });
            }
            
            StackItemsList.ItemsSource = stackItems;
            StackDropdownPopup.IsOpen = true;
        }
        
        private void StackItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is int index)
            {
                StackDropdownPopup.IsOpen = false;
                
                // Index 0 is the current item (top of stack), no action needed
                if (index == 0) return;
                
                // Navigate to the selected item WITHOUT removing anything from the stack
                // Stack is persistent memory - only cleared by X button
                var stackArray = _detailStack.ToArray();
                if (index < stackArray.Length)
                {
                    var selectedEntry = stackArray[index];
                    
                    // Move selected item to top of stack (make it current)
                    // Rebuild stack: selected item on top, then all others in original order (excluding selected)
                    _detailStack.Clear();
                    
                    // Push in reverse order (bottom to top), skipping the selected one
                    for (int i = stackArray.Length - 1; i >= 0; i--)
                    {
                        if (i != index)
                        {
                            _detailStack.Push(stackArray[i]);
                        }
                    }
                    // Push selected item last (so it's on top)
                    _detailStack.Push(selectedEntry);
                    
                    // Restore the selected item's view
                    RestoreDetailFromStack(selectedEntry);
                }
                
                UpdateDetailStackUI();
            }
        }
        
        private void ClearDetailStack()
        {
            // Save any downloading entries before clearing
            while (_detailStack.Count > 0)
            {
                var entry = _detailStack.Pop();
                if (HasActiveDownloads(entry) && !string.IsNullOrEmpty(entry.ResourceId))
                {
                    _savedDownloadingDetails[entry.ResourceId] = entry;
                }
            }
            
            UpdateDetailStackUI();
        }
        
        private void ClearStackButton_Click(object sender, RoutedEventArgs e)
        {
            // Keep only the current item (top of stack), clear the rest
            if (_detailStack.Count <= 1)
            {
                // Nothing to clear, or clear the single item and collapse
                ClearDetailStack();
                CollapsePanel();
                _currentDetail = null;
                _currentResource = null;
                _currentResourceId = null;
                return;
            }
            
            // Keep the current (top) item, clear the rest
            var current = _detailStack.Pop();
            
            // Save downloading entries from the rest
            while (_detailStack.Count > 0)
            {
                var entry = _detailStack.Pop();
                if (HasActiveDownloads(entry) && !string.IsNullOrEmpty(entry.ResourceId))
                {
                    _savedDownloadingDetails[entry.ResourceId] = entry;
                }
            }
            
            // Put current back
            _detailStack.Push(current);
            UpdateDetailStackUI();
        }
        
        #endregion
        
        #region Updates and Missing Dependencies Panels
        
        private async void UpdatesPanelButton_Click(object sender, RoutedEventArgs e)
        {
            // Prevent multiple rapid clicks
            if (_isUpdatesCheckInProgress)
            {
                return;
            }
            
            _isUpdatesCheckInProgress = true;
            try
            {
                await ShowUpdatesPanelAsync();
            }
            finally
            {
                _isUpdatesCheckInProgress = false;
            }
        }
        
        private async void MissingDepsPanelButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowMissingDependenciesPanelAsync();
        }
        
        private async Task ShowUpdatesPanelAsync()
        {
            try
            {
                // Show loading spinner
                StatusLoadingSpinner.Visibility = Visibility.Visible;
                StatusText.Text = "Checking for updates...";
                
                // Get all package groups that have updates available
                // CRITICAL: Use _localPackageVersions which contains the HIGHEST version for each base package
                var updatesAvailable = new List<(string packageGroup, int localVersion, int hubVersion)>();
                
                foreach (var kvp in _localPackageVersions)
                {
                    var groupName = kvp.Key;
                    var localVersion = kvp.Value;
                    
                    if (localVersion > 0 && _hubService.HasUpdate(groupName, localVersion))
                    {
                        var hubVersion = _hubService.GetLatestVersion(groupName);
                        if (hubVersion > localVersion)
                        {
                            updatesAvailable.Add((groupName, localVersion, hubVersion));
                        }
                    }
                }
                
                if (updatesAvailable.Count == 0)
                {
                    StatusLoadingSpinner.Visibility = Visibility.Collapsed;
                    StatusText.Text = "No updates available";
                    MessageBox.Show("All your packages are up to date!", "Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                // Find packages on Hub
                var packageNames = updatesAvailable.Select(u => u.packageGroup + ".latest").ToList();
                var hubPackages = await _hubService.FindPackagesAsync(packageNames);
                
                if (hubPackages == null || hubPackages.Count == 0)
                {
                    StatusLoadingSpinner.Visibility = Visibility.Collapsed;
                    StatusText.Text = "Could not fetch update information";
                    return;
                }
                
                // Create a pseudo-detail view for updates
                _currentFiles.Clear();
                _currentDependencies.Clear();
                
                foreach (var update in updatesAvailable)
                {
                    var packageKey = update.packageGroup + ".latest";
                    var filename = $"{update.packageGroup}.{update.hubVersion}.var";
                    var downloadUrl = "";
                    var fileSize = 0;
                    var latestUrl = "";
                    var hasMetadata = false;
                    
                    if (hubPackages.TryGetValue(packageKey, out var hubPackage) && hubPackage != null)
                    {
                        hasMetadata = true;
                        downloadUrl = !string.IsNullOrEmpty(hubPackage.LatestUrl) 
                            ? hubPackage.LatestUrl 
                            : hubPackage.DownloadUrl;
                        
                        if (!string.IsNullOrEmpty(hubPackage.PackageName))
                            filename = hubPackage.PackageName;
                        
                        fileSize = (int)hubPackage.FileSize;
                        latestUrl = hubPackage.LatestUrl;
                    }
                    
                    var statusColor = hasMetadata 
                        ? new SolidColorBrush(Colors.Orange)
                        : new SolidColorBrush(Colors.Gray);
                    
                    var vm = new HubFileViewModel
                    {
                        Filename = filename,
                        FileSize = fileSize,
                        DownloadUrl = downloadUrl,
                        LatestUrl = latestUrl,
                        Status = hasMetadata
                            ? $"Update {update.localVersion} → {update.hubVersion}"
                            : $"Update available ({update.localVersion} → {update.hubVersion})",
                        StatusColor = statusColor,
                        CanDownload = !string.IsNullOrEmpty(downloadUrl),
                        ButtonText = "⬆",
                        HasUpdate = true,
                        IsInstalled = true
                    };
                    
                    _currentFiles.Add(vm);
                }
                
                // Update UI
                DetailTitle.Text = $"📦 Available Updates ({updatesAvailable.Count})";
                DetailCreator.Text = $"Found {updatesAvailable.Count} updates available";
                DetailCreator.Foreground = new SolidColorBrush(Colors.White);  // Normal text, not blue
                DetailCreator.TextDecorations = null;  // Remove underline
                DetailCreator.Cursor = Cursors.Arrow;  // Not clickable
                DetailCreator.ToolTip = null;  // Remove tooltip
                DetailImageBorder.Visibility = Visibility.Collapsed;
                SupportCreatorButton.Visibility = Visibility.Collapsed;
                DetailCategory.Visibility = Visibility.Collapsed;
                DetailDownloads.Text = "";
                DetailRating.Text = "";
                
                // Hide all per-package elements (not applicable to collection views)
                DetailDependencies.Visibility = Visibility.Collapsed;
                DetailFileSize.Visibility = Visibility.Collapsed;
                DetailLastUpdate.Visibility = Visibility.Collapsed;
                DetailTagsPanel.Visibility = Visibility.Collapsed;
                DetailTagLine.Visibility = Visibility.Collapsed;
                DetailCreatorIcon.Visibility = Visibility.Collapsed;
                DetailInLibraryBadge.Visibility = Visibility.Collapsed;
                DetailUpdateBadge.Visibility = Visibility.Collapsed;
                DetailExternalBadge.Visibility = Visibility.Collapsed;

                DetailFilesControl.ItemsSource = _currentFiles;
                DependenciesHeader.Visibility = Visibility.Collapsed;
                DetailDependenciesControl.ItemsSource = null;
                
                UpdateDownloadAllButton();
                ExpandPanel();
                
                // Clear stack for updates view
                ClearDetailStack();
                _currentDetail = null;
                _currentResource = null;
                _currentResourceId = null;
                
                // Hide loading spinner
                StatusLoadingSpinner.Visibility = Visibility.Collapsed;
                StatusText.Text = $"Found {updatesAvailable.Count} updates";
            }
            catch (Exception ex)
            {
                StatusLoadingSpinner.Visibility = Visibility.Collapsed;
                StatusText.Text = $"Error: {ex.Message}";
            }
        }
        
        private async Task ShowMissingDependenciesPanelAsync()
        {
            try
            {
                // Show loading spinner
                StatusLoadingSpinner.Visibility = Visibility.Visible;
                StatusText.Text = "Scanning for missing dependencies...";
                
                // Check if we have access to package manager
                if (_packageManager == null)
                {
                    StatusLoadingSpinner.Visibility = Visibility.Collapsed;
                    StatusText.Text = "Ready";
                    MessageBox.Show(
                        "Package manager not available.\n\n" +
                        "Please ensure packages have been scanned in the main window.",
                        "Missing Dependencies",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                
                // Collect all missing dependencies from the dependency graph
                var missingDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                // Get all packages with missing dependencies
                foreach (var kvp in _packageManager.PackageMetadata)
                {
                    var metadata = kvp.Value;
                    if (metadata.MissingDependencies != null && metadata.MissingDependencies.Length > 0)
                    {
                        foreach (var dep in metadata.MissingDependencies)
                        {
                            if (!string.IsNullOrEmpty(dep))
                            {
                                missingDeps.Add(dep);
                            }
                        }
                    }
                }
                
                // CRITICAL FIX: Filter out packages that are actually on disk
                // The MissingDependencies list may be stale if packages were downloaded since last scan
                var trulyMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dep in missingDeps)
                {
                    // Check if this exact package is on disk
                    var depClean = dep.Replace(".var", "");
                    if (_localPackageNames.Contains(depClean))
                        continue; // Already have it
                    
                    // Check if it's a .latest reference and we have any version
                    if (dep.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                    {
                        var baseName = dep.Substring(0, dep.Length - 7);
                        if (_localPackageVersions.ContainsKey(baseName))
                            continue; // Have some version of this package
                    }
                    else
                    {
                        // Check if we have any version of this package (for versioned references)
                        var lastDot = depClean.LastIndexOf('.');
                        if (lastDot > 0)
                        {
                            var baseName = depClean.Substring(0, lastDot);
                            if (_localPackageVersions.ContainsKey(baseName))
                                continue; // Have some version of this package
                        }
                    }
                    
                    trulyMissing.Add(dep);
                }
                
                missingDeps = trulyMissing;
                
                if (missingDeps.Count == 0)
                {
                    StatusLoadingSpinner.Visibility = Visibility.Collapsed;
                    StatusText.Text = "Ready";
                    MessageBox.Show(
                        "No missing dependencies found!\n\n" +
                        "All packages have their dependencies satisfied.",
                        "Missing Dependencies",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                
                // Search for missing dependencies on Hub
                StatusText.Text = $"Searching Hub for {missingDeps.Count} missing dependencies...";
                
                var missingDepsList = missingDeps.ToList();
                var hubPackages = await _hubService.FindPackagesAsync(missingDepsList);
                
                if (hubPackages == null || hubPackages.Count == 0)
                {
                    StatusLoadingSpinner.Visibility = Visibility.Collapsed;
                    StatusText.Text = "Ready";
                    MessageBox.Show(
                        $"Could not search Hub for {missingDeps.Count} missing dependencies.\n\n" +
                        "Please check your internet connection and try again.",
                        "Search Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                
                // Create a pseudo-detail view for missing dependencies
                _currentFiles.Clear();
                _currentDependencies.Clear();
                
                int foundCount = 0;
                int notFoundCount = 0;
                
                foreach (var dep in missingDepsList)
                {
                    if (hubPackages.TryGetValue(dep, out var hubPackage) && hubPackage != null && !hubPackage.NotOnHub)
                    {
                        foundCount++;
                        
                        var downloadUrl = !string.IsNullOrEmpty(hubPackage.LatestUrl) 
                            ? hubPackage.LatestUrl 
                            : hubPackage.DownloadUrl;
                        
                        var filename = hubPackage.PackageName ?? $"{dep}.var";
                        
                        // Skip if filename is null or empty
                        if (string.IsNullOrEmpty(filename))
                            continue;
                            
                        var vm = new HubFileViewModel
                        {
                            Filename = filename,
                            FileSize = hubPackage.FileSize,
                            DownloadUrl = downloadUrl,
                            LatestUrl = hubPackage.LatestUrl,
                            Status = "Missing Dependency",
                            StatusColor = new SolidColorBrush(Colors.Red),
                            CanDownload = !string.IsNullOrEmpty(downloadUrl),
                            ButtonText = "⬇",
                            HasUpdate = false,
                            IsInstalled = false
                        };
                        
                        _currentFiles.Add(vm);
                    }
                    else
                    {
                        notFoundCount++;
                    }
                }
                
                // Update UI
                DetailTitle.Text = $"🔗 Missing Dependencies ({foundCount} available, {notFoundCount} not found)";
                DetailCreator.Text = $"Found {foundCount} of {missingDeps.Count} missing dependencies on Hub";
                DetailCreator.Foreground = new SolidColorBrush(Colors.White);  // Normal text, not blue
                DetailCreator.TextDecorations = null;  // Remove underline
                DetailCreator.Cursor = Cursors.Arrow;  // Not clickable
                DetailCreator.ToolTip = null;  // Remove tooltip
                DetailImageBorder.Visibility = Visibility.Collapsed;
                SupportCreatorButton.Visibility = Visibility.Collapsed;
                DetailCategory.Visibility = Visibility.Collapsed;
                DetailDownloads.Text = "";
                DetailRating.Text = "";
                
                // Hide all per-package elements (not applicable to collection views)
                DetailDependencies.Visibility = Visibility.Collapsed;
                DetailFileSize.Visibility = Visibility.Collapsed;
                DetailLastUpdate.Visibility = Visibility.Collapsed;
                DetailTagsPanel.Visibility = Visibility.Collapsed;
                DetailTagLine.Visibility = Visibility.Collapsed;
                DetailCreatorIcon.Visibility = Visibility.Collapsed;
                DetailInLibraryBadge.Visibility = Visibility.Collapsed;
                DetailUpdateBadge.Visibility = Visibility.Collapsed;
                DetailExternalBadge.Visibility = Visibility.Collapsed;
                
                DetailFilesControl.ItemsSource = _currentFiles;
                DependenciesHeader.Visibility = Visibility.Collapsed;
                DetailDependenciesControl.ItemsSource = null;
                
                UpdateDownloadAllButton();
                ExpandPanel();
                
                // Clear stack for missing deps view
                ClearDetailStack();
                _currentDetail = null;
                _currentResource = null;
                _currentResourceId = null;
                
                // Hide loading spinner
                StatusLoadingSpinner.Visibility = Visibility.Collapsed;
                StatusText.Text = $"Found {foundCount} missing dependencies available for download";
            }
            catch (Exception ex)
            {
                StatusLoadingSpinner.Visibility = Visibility.Collapsed;
                StatusText.Text = $"Error: {ex.Message}";
            }
        }
        
        /// <summary>
        /// Updates the missing dependencies panel after a package has been downloaded.
        /// Removes the downloaded package from the list and updates the title.
        /// </summary>
        private void UpdateMissingDepsPanelAfterDownload(string packageName)
        {
            // Check if we're currently viewing the missing dependencies panel
            if (DetailTitle.Text == null || !DetailTitle.Text.StartsWith("🔗 Missing Dependencies"))
                return;
            
            // Count remaining missing dependencies (files that are not yet downloaded)
            int remainingCount = 0;
            int downloadedCount = 0;
            
            foreach (var file in _currentFiles)
            {
                if (file.IsInstalled || file.Status?.Contains("Downloaded") == true || file.Status?.Contains("Updated") == true)
                {
                    downloadedCount++;
                }
                else
                {
                    remainingCount++;
                }
            }
            
            // Update the title to reflect the new state
            if (remainingCount == 0 && downloadedCount > 0)
            {
                DetailTitle.Text = $"🔗 Missing Dependencies (All {downloadedCount} downloaded!)";
                DetailCreator.Text = "All missing dependencies have been downloaded";
            }
            else
            {
                DetailTitle.Text = $"🔗 Missing Dependencies ({remainingCount} remaining, {downloadedCount} downloaded)";
                DetailCreator.Text = $"{downloadedCount} downloaded, {remainingCount} still available for download";
            }
            
            // Keep text styling consistent (white, not blue, not clickable)
            DetailCreator.Foreground = new SolidColorBrush(Colors.White);
            DetailCreator.TextDecorations = null;
            DetailCreator.Cursor = Cursors.Arrow;
            DetailCreator.ToolTip = null;
        }
        
        #endregion
    }
    
    /// <summary>
    /// Entry for stack-based detail navigation
    /// </summary>
    public class DetailStackEntry
    {
        public HubResourceDetail Detail { get; set; }
        public HubResource Resource { get; set; }
        public ObservableCollection<HubFileViewModel> Files { get; set; }
        public ObservableCollection<HubFileViewModel> Dependencies { get; set; }
        public string ResourceId { get; set; }
    }

    /// <summary>
    /// ViewModel for Hub file items in the detail panel
    /// </summary>
    public class HubFileViewModel : INotifyPropertyChanged
    {
        private string _status;
        private SolidColorBrush _statusColor;
        private bool _canDownload;
        private string _buttonText;
        private bool _isDownloading;
        private bool _isInstalled;
        private bool _hasUpdate;
        private float _progress;

        public string Filename { get; set; }
        public long FileSize { get; set; }
        public string DownloadUrl { get; set; }
        public string LatestUrl { get; set; }
        public string LicenseType { get; set; }
        public bool IsDependency { get; set; }
        public bool NotOnHub { get; set; }
        public bool AlreadyHave { get; set; }
        public HubFile HubFile { get; set; }
        public string LocalPath { get; set; } // Path to installed file
        
        public bool HasUpdate
        {
            get => _hasUpdate;
            set { _hasUpdate = value; OnPropertyChanged(nameof(HasUpdate)); }
        }

        public bool IsInstalled
        {
            get => _isInstalled;
            set { _isInstalled = value; OnPropertyChanged(nameof(IsInstalled)); }
        }

        public string FileSizeFormatted
        {
            get => FormatFileSize(FileSize);
            set { } // Allow setting but ignore - for compatibility
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(nameof(Status)); }
        }

        public SolidColorBrush StatusColor
        {
            get => _statusColor;
            set { _statusColor = value; OnPropertyChanged(nameof(StatusColor)); }
        }

        public bool CanDownload
        {
            get => _canDownload;
            set { _canDownload = value; OnPropertyChanged(nameof(CanDownload)); }
        }

        public string ButtonText
        {
            get => _buttonText;
            set { _buttonText = value; OnPropertyChanged(nameof(ButtonText)); }
        }

        public bool IsDownloading
        {
            get => _isDownloading;
            set { _isDownloading = value; OnPropertyChanged(nameof(IsDownloading)); }
        }

        public float Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(nameof(Progress)); }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes <= 0) return "";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => 
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Converter to hide null or empty strings by returning Collapsed visibility
    /// </summary>
    public class NullOrEmptyToVisibilityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value == null)
                return Visibility.Collapsed;
            
            var stringValue = value.ToString();
            if (string.IsNullOrEmpty(stringValue) || stringValue == "null")
                return Visibility.Collapsed;
            
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Attached behavior for loading cached images with async support
    /// </summary>
    public static class CachedImageBehavior
    {
        public static string GetImageUrl(DependencyObject obj)
        {
            return (string)obj.GetValue(ImageUrlProperty);
        }

        public static void SetImageUrl(DependencyObject obj, string value)
        {
            obj.SetValue(ImageUrlProperty, value);
        }

        public static readonly DependencyProperty ImageUrlProperty =
            DependencyProperty.RegisterAttached(
                "ImageUrl",
                typeof(string),
                typeof(CachedImageBehavior),
                new PropertyMetadata(null, OnImageUrlChanged));

        private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is Image image))
                return;

            var imageUrl = (string)e.NewValue;
            if (string.IsNullOrEmpty(imageUrl))
            {
                image.Source = null;
                return;
            }

            var manager = CachedImageManager.GetOrCreate(imageUrl);
            if (manager != null)
            {
                // Set initial image (may be null)
                image.Source = manager.Image;

                // Listen for updates
                manager.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(CachedImageManager.Image))
                    {
                        image.Source = manager.Image;
                    }
                };
            }
        }
    }

    /// <summary>
    /// Manages cached Hub images with UI update support
    /// Handles async downloads and notifies UI when images are ready
    /// </summary>
    public class CachedImageManager : INotifyPropertyChanged
    {
        private static readonly object _instanceCacheLock = new object();
        private static readonly Dictionary<string, CachedImageManager> _instanceCache = new Dictionary<string, CachedImageManager>(StringComparer.OrdinalIgnoreCase);
        private static HubService _hubService;
        
        private BitmapImage _image;
        private bool _isLoading;
        
        public event PropertyChangedEventHandler PropertyChanged;
        
        public BitmapImage Image
        {
            get => _image;
            set
            {
                if (_image != value)
                {
                    _image = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
                }
            }
        }
        
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoading)));
                }
            }
        }
        
        public string ImageUrl { get; private set; }
        
        public static void SetHubService(HubService hubService)
        {
            _hubService = hubService;
        }
        
        public static CachedImageManager GetOrCreate(string imageUrl)
        {
            if (string.IsNullOrEmpty(imageUrl))
                return null;
            
            lock (_instanceCacheLock)
            {
                if (_instanceCache.TryGetValue(imageUrl, out var manager))
                {
                    return manager;
                }
                
                var newManager = new CachedImageManager { ImageUrl = imageUrl };
                _instanceCache[imageUrl] = newManager;
                
                // Start loading asynchronously
                _ = newManager.LoadImageAsync();
                
                return newManager;
            }
        }
        
        private async Task LoadImageAsync()
        {
            if (_hubService == null)
            {
                // Fallback to direct URL
                LoadDirectUrl();
                return;
            }
            
            try
            {
                IsLoading = true;
                
                // Wait for cache to be fully loaded with retries
                BitmapImage cachedImage = null;
                for (int retries = 0; retries < 5; retries++)
                {
                    cachedImage = _hubService.ResourcesCache?.TryGetCachedImage(ImageUrl);
                    if (cachedImage != null)
                    {
                        Image = cachedImage;
                        IsLoading = false;
                        return;
                    }
                    if (retries < 4)
                        await Task.Delay(50); // Wait 50ms before retrying
                }
                
                // Download and cache the image
                var bitmap = await _hubService.GetCachedImageAsync(ImageUrl);
                if (bitmap != null)
                {
                    Image = bitmap;
                }
                else
                {
                    // Fallback to direct URL if download failed
                    LoadDirectUrl();
                }
            }
            catch (Exception)
            {
                LoadDirectUrl();
            }
            finally
            {
                IsLoading = false;
            }
        }
        
        private void LoadDirectUrl()
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(ImageUrl);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                Image = bitmap;
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// Converter for cached Hub images - downloads and caches images from Hub
    /// Returns cached BitmapImage if available, otherwise downloads and caches
    /// Properly updates UI when images are ready
    /// </summary>
    public class CachedHubImageConverter : System.Windows.Data.IValueConverter
    {
        public static void SetHubService(HubService hubService)
        {
            CachedImageManager.SetHubService(hubService);
        }
        
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value == null || string.IsNullOrEmpty(value.ToString()))
                return null;
            
            var imageUrl = value.ToString();
            var manager = CachedImageManager.GetOrCreate(imageUrl);
            
            // Return the current image (may be null if still loading)
            // The manager will update the Image property via PropertyChanged
            // We use an attached behavior to listen for updates
            return manager?.Image;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Item for the stack dropdown list
    /// </summary>
    public class StackDropdownItem
    {
        public int Index { get; set; }
        public string Position { get; set; }
        public string Title { get; set; }
        public bool IsCurrent { get; set; }
        public SolidColorBrush DisplayForeground { get; set; }
        public FontWeight DisplayFontWeight { get; set; }
    }
}
