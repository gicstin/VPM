using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        
        private CancellationTokenSource _searchCts;
        private int _currentPage = 1;
        private int _totalPages = 1;
        private int _totalResources = 0;
        
        // Side panel state
        private bool _isPanelExpanded = false;
        private const double PanelWidth = 380;
        private HubResourceDetail _currentDetail;
        private HubResource _currentResource;  // Track the resource being viewed
        private ObservableCollection<HubFileViewModel> _currentFiles;
        private ObservableCollection<HubFileViewModel> _currentDependencies;

        public HubBrowserWindow(string destinationFolder, Dictionary<string, string> localPackagePaths = null)
        {
            InitializeComponent();
            
            _hubService = new HubService();
            _destinationFolder = destinationFolder;
            _localPackagePaths = localPackagePaths ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            // Derive VaM folder from destination folder (go up from AddonPackages or AllPackages)
            _vamFolder = Path.GetDirectoryName(destinationFolder);
            
            _currentFiles = new ObservableCollection<HubFileViewModel>();
            _currentDependencies = new ObservableCollection<HubFileViewModel>();

            _hubService.StatusChanged += (s, status) => 
            {
                Dispatcher.Invoke(() => StatusText.Text = status);
            };

            SourceInitialized += HubBrowserWindow_SourceInitialized;
            Loaded += HubBrowserWindow_Loaded;
            Closed += HubBrowserWindow_Closed;
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
            // Load packages.json for version checking
            await _hubService.LoadPackagesJsonAsync();
            
            // Initial search
            await SearchAsync();
        }

        private void HubBrowserWindow_Closed(object sender, EventArgs e)
        {
            _searchCts?.Cancel();
            _hubService?.Dispose();
        }

        #region Search & Filtering

        private async Task SearchAsync()
        {
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();

            try
            {
                LoadingPanel.Visibility = Visibility.Visible;
                ResourcesItemsControl.ItemsSource = null;

                var searchParams = BuildSearchParams();
                var response = await _hubService.SearchResourcesAsync(searchParams, _searchCts.Token);

                if (response?.IsSuccess == true)
                {
                    _totalResources = response.Pagination?.TotalFound ?? 0;
                    _totalPages = response.Pagination?.TotalPages ?? 1;

                    // Mark resources that are in library or have updates
                    foreach (var resource in response.Resources ?? Enumerable.Empty<HubResource>())
                    {
                        CheckLibraryStatus(resource);
                    }

                    ResourcesItemsControl.ItemsSource = response.Resources;
                    UpdatePaginationUI();
                    
                    StatusText.Text = $"Found {_totalResources} resources";
                }
                else
                {
                    StatusText.Text = $"Error: {response?.Error ?? "Unknown error"}";
                }
            }
            catch (OperationCanceledException)
            {
                // Search was cancelled
                Debug.WriteLine("[HubBrowser] Search was cancelled");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubBrowser] Search error: {ex.Message}");
                Debug.WriteLine($"[HubBrowser] Stack trace: {ex.StackTrace}");
                StatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }

        private HubSearchParams BuildSearchParams()
        {
            return new HubSearchParams
            {
                Page = _currentPage,
                PerPage = 48,
                Search = SearchBox.Text?.Trim(),
                Category = (CategoryFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All",
                PayType = (PayTypeFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Free",
                Sort = (SortFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Latest Update",
                OnlyDownloadable = OnlyDownloadableCheck.IsChecked == true
            };
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

                // Check if we have this package - verify file actually exists
                var localPath = FindLocalPackage(packageName);
                if (localPath != null)
                {
                    resource.InLibrary = true;
                }

                // Check for updates
                var groupName = GetPackageGroupName(packageName);
                var localVersion = GetHighestLocalVersion(groupName);
                
                if (localVersion > 0 && _hubService.HasUpdate(groupName, localVersion))
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
            int highest = 0;
            foreach (var pkg in _localPackagePaths.Keys)
            {
                var name = pkg.Replace(".var", "");
                if (name.StartsWith(groupName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    var versionPart = name.Substring(groupName.Length + 1);
                    if (int.TryParse(versionPart, out var version) && version > highest)
                    {
                        highest = version;
                    }
                }
            }
            return highest;
        }
        
        /// <summary>
        /// Find a local package by name, checking if the file actually exists.
        /// Supports finding any version of a package (for .latest dependencies).
        /// </summary>
        /// <param name="packageName">Package name to find</param>
        /// <returns>Full path to the package file if found, null otherwise</returns>
        private string FindLocalPackage(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return null;
            
            var cleanName = packageName.Replace(".var", "");
            
            // First, try exact match in our known packages
            if (_localPackagePaths.TryGetValue(cleanName, out var exactPath))
            {
                if (File.Exists(exactPath))
                    return exactPath;
            }
            
            // Try with .var extension
            if (_localPackagePaths.TryGetValue(cleanName + ".var", out exactPath))
            {
                if (File.Exists(exactPath))
                    return exactPath;
            }
            
            // For .latest or version-flexible matching, find any version of this package
            var basePackage = GetBasePackageName(cleanName);
            
            // Find matching packages - must be basePackage.{version} where version is numeric
            var matchingEntry = _localPackagePaths
                .Where(kvp => {
                    var name = kvp.Key.Replace(".var", "");
                    if (!name.StartsWith(basePackage + ".", StringComparison.OrdinalIgnoreCase))
                        return false;
                    // Ensure what follows is a version number
                    var suffix = name.Substring(basePackage.Length + 1);
                    return int.TryParse(suffix, out _);
                })
                .OrderByDescending(kvp => {
                    // Get highest version
                    var name = kvp.Key.Replace(".var", "");
                    var suffix = name.Substring(basePackage.Length + 1);
                    return int.TryParse(suffix, out var v) ? v : 0;
                })
                .FirstOrDefault();
            
            if (!string.IsNullOrEmpty(matchingEntry.Value) && File.Exists(matchingEntry.Value))
            {
                return matchingEntry.Value;
            }
            
            return null;
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
                _currentPage = 1;
                await SearchAsync();
            }
        }

        private async void Filter_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            
            _currentPage = 1;
            await SearchAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await _hubService.LoadPackagesJsonAsync(forceRefresh: true);
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
                
                // Download image data on background thread
                var imageData = await Task.Run(async () =>
                {
                    using var client = new System.Net.Http.HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(10);
                    return await client.GetByteArrayAsync(imageUrl);
                });
                
                // Check if still current before creating bitmap on UI thread
                if (_currentImageUrl != imageUrl) return;
                
                // Create bitmap from memory stream on UI thread
                var bitmap = new BitmapImage();
                using (var stream = new System.IO.MemoryStream(imageData))
                {
                    bitmap.BeginInit();
                    bitmap.StreamSource = stream;
                    bitmap.DecodePixelWidth = 350;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                
                if (_currentImageUrl == imageUrl)
                {
                    DetailImage.Source = bitmap;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubBrowser] Error loading image: {ex.Message}");
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
                
                var detail = await _hubService.GetResourceDetailAsync(resource.ResourceId);
                
                if (detail != null)
                {
                    _currentDetail = detail;
                    _currentResource = resource;  // Store the resource for later updates
                    PopulateDetailPanel(detail);
                    ExpandPanel();
                    StatusText.Text = "Ready";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubBrowser] Error loading resource details: {ex.Message}");
                Debug.WriteLine($"[HubBrowser] Stack trace: {ex.StackTrace}");
                StatusText.Text = $"Error loading details: {ex.Message}";
            }
        }

        private void PopulateDetailPanel(HubResourceDetail detail)
        {
            // Set basic info
            DetailTitle.Text = detail.Title ?? "";
            DetailCreator.Text = detail.Creator ?? "";
            DetailDownloads.Text = $"⬇ {detail.DownloadCount}";
            DetailRating.Text = $"⭐ {detail.RatingAvg:F1}";
            DetailType.Text = detail.Type ?? detail.Category ?? "";
            
            // Load image asynchronously for fast UI response
            LoadDetailImageAsync(detail.ImageUrl);
            
            // Build files list
            _currentFiles.Clear();
            
            // Main package files
            if (detail.HubFiles != null)
            {
                foreach (var file in detail.HubFiles)
                {
                    _currentFiles.Add(CreateFileViewModel(file, false));
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
                        _currentDependencies.Add(CreateFileViewModel(file, true));
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
                
                // If we found a version, replace .latest with actual version
                if (!string.IsNullOrEmpty(latestVersion))
                {
                    var oldFilename = filename;
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
                    
                    // Use LatestUrl if available
                    if (!string.IsNullOrEmpty(file.LatestUrl))
                    {
                        downloadUrl = file.LatestUrl;
                    }
                }
                else
                {
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
                // Get local version from the found package path
                var localPackageName = Path.GetFileNameWithoutExtension(localPath);
                var localVersion = ExtractVersionNumber(localPackageName);
                
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
        /// Extracts version number from a download URL
        /// URL format typically: .../Creator.PackageName.Version.var
        /// </summary>
        private string ExtractVersionFromUrl(string url, string originalFilename)
        {
            Debug.WriteLine($"[HubBrowser] ExtractVersionFromUrl called:");
            Debug.WriteLine($"[HubBrowser]   URL: {url}");
            Debug.WriteLine($"[HubBrowser]   Original filename: {originalFilename}");
            
            if (string.IsNullOrEmpty(url))
            {
                Debug.WriteLine($"[HubBrowser]   URL is null/empty, returning null");
                return null;
            }
            
            try
            {
                // Get the filename from URL
                var uri = new Uri(url);
                var urlFilename = Path.GetFileName(uri.LocalPath);
                Debug.WriteLine($"[HubBrowser]   URL filename extracted: {urlFilename}");
                
                if (string.IsNullOrEmpty(urlFilename))
                {
                    Debug.WriteLine($"[HubBrowser]   URL filename is empty, returning null");
                    return null;
                }
                
                // Remove .var extension
                urlFilename = urlFilename.Replace(".var", "");
                Debug.WriteLine($"[HubBrowser]   URL filename without .var: {urlFilename}");
                
                // Get base package name (Creator.PackageName)
                var baseName = GetBasePackageName(originalFilename.Replace(".var", "").Replace(".latest", ""));
                Debug.WriteLine($"[HubBrowser]   Base package name: {baseName}");
                
                // Extract version - everything after the base name
                if (urlFilename.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    var version = urlFilename.Substring(baseName.Length + 1);
                    Debug.WriteLine($"[HubBrowser]   Extracted version string: {version}");
                    
                    // Validate it looks like a version (numeric)
                    if (!string.IsNullOrEmpty(version) && char.IsDigit(version[0]))
                    {
                        Debug.WriteLine($"[HubBrowser]   Version is valid (starts with digit): {version}");
                        return version;
                    }
                    else
                    {
                        Debug.WriteLine($"[HubBrowser]   Version invalid (doesn't start with digit)");
                    }
                }
                else
                {
                    Debug.WriteLine($"[HubBrowser]   URL filename doesn't start with base name + '.'");
                    Debug.WriteLine($"[HubBrowser]   Expected prefix: {baseName}.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubBrowser] Error extracting version from URL: {ex.Message}");
            }
            
            Debug.WriteLine($"[HubBrowser]   Returning null - no version extracted");
            return null;
        }

        private void UpdateDownloadAllButton()
        {
            var downloadableFiles = _currentFiles.Count(f => f.CanDownload);
            var downloadableDeps = _currentDependencies.Count(f => f.CanDownload);
            var totalDownloadable = downloadableFiles + downloadableDeps;
            
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

        private async void DownloadFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is HubFileViewModel file)
            {
                // If has update available, download the update
                if (file.HasUpdate && file.CanDownload)
                {
                    await DownloadFileAsync(file);
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
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[HubBrowser] Error opening explorer: {ex.Message}");
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
                
                await DownloadFileAsync(file);
            }
        }

        private async void DownloadAll_Click(object sender, RoutedEventArgs e)
        {
            // Download main files first
            var toDownload = _currentFiles.Where(f => f.CanDownload).ToList();
            foreach (var file in toDownload)
            {
                await DownloadFileAsync(file);
            }
            
            // Then download dependencies
            var depsToDownload = _currentDependencies.Where(f => f.CanDownload).ToList();
            foreach (var file in depsToDownload)
            {
                await DownloadFileAsync(file);
            }
        }

        private async Task DownloadFileAsync(HubFileViewModel file)
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

            try
            {
                file.Status = file.HasUpdate ? "Updating..." : "Downloading...";
                file.StatusColor = new SolidColorBrush(Colors.Yellow);
                file.CanDownload = false;
                file.ButtonText = "...";

                // Get the package name from the download URL
                // This ensures we get the correct version for both new downloads and updates
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
                catch
                {
                    packageName = file.Filename.Replace(".var", "");
                }
                
                var progress = new Progress<HubDownloadProgress>(p =>
                {
                    if (p.IsDownloading)
                    {
                        var percent = p.TotalBytes > 0 ? (p.DownloadedBytes * 100 / p.TotalBytes) : 0;
                        file.Status = file.HasUpdate ? $"Updating... {percent}%" : $"Downloading... {percent}%";
                    }
                });

                var success = await _hubService.DownloadPackageAsync(
                    downloadUrl, 
                    _destinationFolder,  // Pass folder, not full path
                    packageName,         // Package name without .var
                    progress);

                if (success)
                {
                    var downloadedFilename = packageName + ".var";
                    var downloadedPath = Path.Combine(_destinationFolder, downloadedFilename);
                    
                    file.Status = file.HasUpdate ? "✓ Updated" : "✓ Downloaded";
                    file.StatusColor = new SolidColorBrush(Colors.LimeGreen);
                    file.ButtonText = "✓";
                    file.IsInstalled = true;
                    file.HasUpdate = false;  // Clear update flag after successful update
                    file.LocalPath = downloadedPath;
                    file.Filename = downloadedFilename;  // Update filename to reflect new version
                    
                    // Add to local packages dictionary for future lookups
                    _localPackagePaths[packageName] = downloadedPath;
                    
                    // Update the resource's InLibrary status so the main grid updates instantly
                    if (_currentResource != null)
                    {
                        _currentResource.InLibrary = true;
                        _currentResource.UpdateAvailable = false;  // Clear update flag
                    }
                }
                else
                {
                    file.Status = "Download failed";
                    file.StatusColor = new SolidColorBrush(Colors.Red);
                    file.CanDownload = true;
                    file.ButtonText = "Retry";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubBrowser] Download error: {ex.Message}");
                file.Status = $"Error: {ex.Message}";
                file.StatusColor = new SolidColorBrush(Colors.Red);
                file.CanDownload = true;
                file.ButtonText = "Retry";
            }

            UpdateDownloadAllButton();
        }

        #endregion
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
}
