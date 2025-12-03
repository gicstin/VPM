using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    /// <summary>
    /// Unified window for package downloads and online searches
    /// Handles missing package downloads by searching online package database
    /// Supports drag-drop of .var, .json, .txt files and manual input
    /// </summary>
    public partial class PackageSearchWindow : Window
    {
        #region Fields

        private readonly PackageManager _packageManager;
        private readonly PackageDownloader _packageDownloader;
        private readonly DownloadQueueManager _downloadQueueManager;
        private readonly string _addonPackagesFolder;
        private readonly string _allPackagesFolder;
        private readonly Func<Task<bool>> _updateDatabaseCallback;
        private readonly Action<string, string> _onPackageDownloadedCallback;
        private readonly ObservableCollection<PackageSearchResult> _searchResults;
        private HubService _hubService;
        private PackageUpdateChecker _updateChecker;
        private bool _isSearching = false;
        private int _totalDownloads = 0;
        private int _completedDownloads = 0;
        private int _failedDownloads = 0;

        // Windows API for dark title bar
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        #endregion

        #region Constructor

        public PackageSearchWindow(
            PackageManager packageManager,
            PackageDownloader packageDownloader,
            DownloadQueueManager downloadQueueManager,
            string addonPackagesFolder,
            Func<Task<bool>> updateDatabaseCallback,
            Action<string, string> onPackageDownloadedCallback = null)
        {
            InitializeComponent();

            _packageManager = packageManager ?? throw new ArgumentNullException(nameof(packageManager));
            _packageDownloader = packageDownloader ?? throw new ArgumentNullException(nameof(packageDownloader));
            _downloadQueueManager = downloadQueueManager ?? throw new ArgumentNullException(nameof(downloadQueueManager));
            _addonPackagesFolder = addonPackagesFolder ?? throw new ArgumentNullException(nameof(addonPackagesFolder));
            _updateDatabaseCallback = updateDatabaseCallback ?? throw new ArgumentNullException(nameof(updateDatabaseCallback));
            _onPackageDownloadedCallback = onPackageDownloadedCallback;
            
            // Calculate AllPackages folder path (unloaded packages location)
            var vamRoot = Path.GetDirectoryName(_addonPackagesFolder);
            _allPackagesFolder = Path.Combine(vamRoot, "AllPackages");

            _searchResults = new ObservableCollection<PackageSearchResult>();
            ResultsDataGrid.ItemsSource = _searchResults;

            // Set initial title
            Title = "Package Downloads - Ready";

            // Apply dark title bar
            SourceInitialized += (s, e) => ApplyDarkTitleBar();

            // Monitor text changes for placeholder visibility
            InputTextBox.TextChanged += (s, e) =>
            {
                PlaceholderText.Visibility = string.IsNullOrWhiteSpace(InputTextBox.Text) 
                    ? Visibility.Visible 
                    : Visibility.Collapsed;
            };

            // Subscribe to download events
            _packageDownloader.DownloadProgress += OnDownloadProgress;
            _packageDownloader.DownloadCompleted += OnDownloadCompleted;
            _packageDownloader.DownloadError += OnDownloadError;
            
            // Try to load offline database silently on startup
            Loaded += async (s, e) => await TryLoadOfflineDatabase();
            
            // Check if database is already loaded and update status
            UpdateDatabaseStatus();
        }
        
        /// <summary>
        /// Attempts to load package source (local links.txt or Hub resources)
        /// </summary>
        private async Task TryLoadOfflineDatabase()
        {
            try
            {
                // Initialize HubService and UpdateChecker if not already done
                if (_hubService == null)
                {
                    _hubService = new HubService();
                }
                
                if (_updateChecker == null)
                {
                    _updateChecker = new PackageUpdateChecker(_hubService);
                }
                
                // Load package source (local links.txt or Hub resources)
                bool loaded = await _updateChecker.LoadPackageSourceAsync();
                
                if (loaded)
                {
                    UpdateDatabaseStatus();
                    
                    if (_updateChecker.IsUsingLocalLinks)
                    {
                        SetStatus($"Local links.txt loaded");
                    }
                    else
                    {
                        SetStatus($"Hub packages loaded");
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Pre-fills the input field with package names and optionally triggers search
        /// </summary>
        public async void LoadPackageNames(IEnumerable<string> packageNames, bool autoSearch = true)
        {
            if (packageNames == null || !packageNames.Any())
                return;

            // Fill the input box
            InputTextBox.Text = string.Join("\n", packageNames);

            // Optionally trigger search automatically
            if (autoSearch)
            {
                // Wait a moment for the window to fully load
                await Task.Delay(100);
                ParseButton_Click(null, null);
            }
        }

        /// <summary>
        /// Appends package names to existing content and optionally triggers search
        /// </summary>
        public async void AppendPackageNames(IEnumerable<string> packageNames, bool autoSearch = true)
        {
            if (packageNames == null || !packageNames.Any())
                return;

            // Get existing package names
            var existingText = InputTextBox.Text;
            var existingPackages = existingText
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Filter out duplicates
            var newPackages = packageNames
                .Where(p => !existingPackages.Contains(p))
                .ToList();

            if (!newPackages.Any())
            {
                // All packages already exist, just trigger search if needed
                if (autoSearch)
                {
                    await Task.Delay(100);
                    ParseButton_Click(null, null);
                }
                return;
            }

            // Append only new packages
            var newPackagesText = string.Join("\n", newPackages);
            
            if (string.IsNullOrWhiteSpace(existingText))
            {
                InputTextBox.Text = newPackagesText;
            }
            else
            {
                InputTextBox.Text = existingText + "\n" + newPackagesText;
            }

            // Optionally trigger search automatically
            if (autoSearch)
            {
                // Wait a moment for the UI to update
                await Task.Delay(100);
                ParseButton_Click(null, null);
            }
        }

        #endregion

        #region UI Helpers

        private void ApplyDarkTitleBar()
        {
            try
            {
                bool isDarkMode = false;
                if (Application.Current?.Resources != null)
                {
                    if (Application.Current.Resources.MergedDictionaries.Count > 0)
                    {
                        var themeDict = Application.Current.Resources.MergedDictionaries[0];
                        if (themeDict.Source != null && themeDict.Source.ToString().Contains("Dark"))
                        {
                            isDarkMode = true;
                        }
                    }

                    if (!isDarkMode && Application.Current.Resources.Contains(SystemColors.ControlBrushKey))
                    {
                        var brush = Application.Current.Resources[SystemColors.ControlBrushKey] as SolidColorBrush;
                        if (brush != null)
                        {
                            isDarkMode = brush.Color.R < 128;
                        }
                    }
                }

                if (isDarkMode)
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        int value = 1;
                        // Try Windows 11/10 20H1+ attribute first, then fall back to older Windows 10 attribute
                        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                        {
                            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref value, sizeof(int));
                        }
                    }
                }
            }
            catch
            {
                // Silently fail if dark title bar is not supported
            }
        }

        internal void SetStatus(string message)
        {
            Dispatcher.Invoke(() =>
            {
                Title = $"Package Downloads - {message}";
            });
        }
        
        /// <summary>
        /// Updates the database status label with current package count
        /// </summary>
        private void UpdateDatabaseStatus()
        {
            Dispatcher.Invoke(() =>
            {
                // Check if using local links.txt or Hub
                bool usingLocalLinks = _updateChecker?.IsUsingLocalLinks ?? false;
                int packageCount = 0;
                string sourceName = "";
                string tooltipText = "";
                
                if (usingLocalLinks)
                {
                    // Get count from local links cache
                    packageCount = _updateChecker?.GetAllPackageNames()?.Count ?? 0;
                    sourceName = "Local Links";
                    tooltipText = $"Using local links.txt file\n{packageCount:N0} packages available";
                }
                else
                {
                    // Get count from Hub service
                    packageCount = _hubService?.GetPackageCount() ?? 0;
                    sourceName = "Hub Packages";
                    tooltipText = $"Using VaM Hub resources\n{packageCount:N0} packages available";
                }
                
                if (packageCount > 0)
                {
                    // Format count with K suffix for thousands
                    string formattedCount = packageCount >= 1000 
                        ? $"{packageCount / 1000.0:0.#}K" 
                        : packageCount.ToString();
                    
                    // Show green dot and label
                    DatabaseStatusDot.Visibility = Visibility.Visible;
                    DatabaseStatusLabel.Visibility = Visibility.Visible;
                    DatabaseStatusLabel.Text = $"{sourceName}: {formattedCount}";
                    
                    // Set tooltip with detailed info
                    DatabaseStatusTooltip.Content = tooltipText;
                }
                else
                {
                    // Hide status if no database loaded
                    DatabaseStatusDot.Visibility = Visibility.Collapsed;
                    DatabaseStatusLabel.Visibility = Visibility.Collapsed;
                }
            });
        }

        /// <summary>
        /// Strips version number from package name (e.g., "Package.1" -> "Package")
        /// </summary>
        private string StripVersionFromPackageName(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return packageName;

            // Check if the last part after the last dot is a number (version)
            var lastDotIndex = packageName.LastIndexOf('.');
            if (lastDotIndex > 0 && lastDotIndex < packageName.Length - 1)
            {
                var lastPart = packageName.Substring(lastDotIndex + 1);
                if (int.TryParse(lastPart, out _))
                {
                    // It's a version number, strip it
                    return packageName.Substring(0, lastDotIndex);
                }
            }

            return packageName;
        }

        #endregion

        #region Drag and Drop

        private void InputBorder_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
            {
                e.Effects = DragDropEffects.Copy;
                InputBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                InputBorder.BorderThickness = new Thickness(2);
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void InputBorder_DragLeave(object sender, DragEventArgs e)
        {
            InputBorder.BorderBrush = (SolidColorBrush)Application.Current.Resources[SystemColors.ActiveBorderBrushKey];
            InputBorder.BorderThickness = new Thickness(1);
        }

        private void InputBorder_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private async void InputBorder_Drop(object sender, DragEventArgs e)
        {
            InputBorder.BorderBrush = (SolidColorBrush)Application.Current.Resources[SystemColors.ActiveBorderBrushKey];
            InputBorder.BorderThickness = new Thickness(2);

            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    await ProcessDroppedFiles(files);
                }
                else if (e.Data.GetDataPresent(DataFormats.Text))
                {
                    string text = (string)e.Data.GetData(DataFormats.Text);
                    InputTextBox.Text += (string.IsNullOrWhiteSpace(InputTextBox.Text) ? "" : "\n") + text;
                }
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error processing dropped content: {ex.Message}", 
                    "Drop Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ProcessDroppedFiles(string[] files)
        {
            var packageNames = new List<string>();

            foreach (var file in files)
            {
                try
                {
                    var extension = Path.GetExtension(file).ToLowerInvariant();

                    if (extension == ".var")
                    {
                        // Extract package name from .var file
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        packageNames.Add(fileName);
                    }
                    else if (extension == ".json" || extension == ".txt")
                    {
                        // Read file content and parse
                        var content = await Task.Run(() => File.ReadAllText(file));
                        var parsed = ParsePackageNames(content);
                        packageNames.AddRange(parsed);
                    }
                    else
                    {
                        SetStatus($"Unsupported file type: {extension}");
                    }
                }
                catch (Exception ex)
                {
                    SetStatus($"Error reading file {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            if (packageNames.Any())
            {
                var currentText = InputTextBox.Text;
                var newText = string.Join("\n", packageNames);
                InputTextBox.Text = string.IsNullOrWhiteSpace(currentText) 
                    ? newText 
                    : currentText + "\n" + newText;
                
                SetStatus($"Added {packageNames.Count} package(s) from dropped files");
            }
        }

        #endregion

        #region Parsing

        private List<string> ParsePackageNames(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new List<string>();

            var packageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Split by newlines first to handle lines with "packagename URL" format
            var lines = input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                // Remove common prefixes
                trimmed = trimmed.TrimStart('-', '*', '•', '·', '>', ' ', '\t');

                // Check if line contains a URL (format: "packagename URL" or "packagename.var URL")
                // In this case, extract package name from the first part before the URL
                string packagePart = trimmed;
                if (trimmed.Contains("://"))
                {
                    // Split by whitespace to separate package name from URL
                    var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        // Use the first part (package name) and ignore the URL
                        packagePart = parts[0];
                    }
                }
                else
                {
                    // No URL, might have multiple package names separated by delimiters
                    var subParts = trimmed.Split(new[] { ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var subPart in subParts)
                    {
                        ProcessPackageName(subPart.Trim(), packageNames);
                    }
                    continue;
                }

                ProcessPackageName(packagePart, packageNames);
            }

            return packageNames.ToList();
        }

        private void ProcessPackageName(string input, HashSet<string> packageNames)
        {
            var trimmed = input.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return;

            // Remove trailing dots
            trimmed = trimmed.TrimEnd('.', ' ', '\t');

            // Remove file extensions (.var, .json, .txt, etc.)
            var extensions = new[] { ".var", ".json", ".txt", ".zip", ".rar", ".7z" };
            foreach (var ext in extensions)
            {
                if (trimmed.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed.Substring(0, trimmed.Length - ext.Length);
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(trimmed))
                return;

            // Try to extract package name using regex first (handles all formats)
            // This matches patterns like: Creator.PackageName.Version
            // Supports special characters like &, [], (), _, -, etc. in package names
            var packagePattern = @"([a-zA-Z0-9_\-]+\.[a-zA-Z0-9_\-\[\]\(\)&\s]+(?:\.\d+)+)";
            var matches = Regex.Matches(trimmed, packagePattern, RegexOptions.IgnoreCase);
            
            if (matches.Count > 0)
            {
                // Found package name(s) with regex - add all matches
                foreach (Match match in matches)
                {
                    var packageName = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(packageName))
                    {
                        packageNames.Add(packageName);
                    }
                }
                return;
            }

            // Fallback: If the trimmed text contains a dot, it's likely a package name
            // Just use it directly to preserve special characters like &, [], etc.
            if (trimmed.Contains("."))
            {
                packageNames.Add(trimmed);
            }
        }

        #endregion

        #region Search Logic

        private async Task PerformSearch(List<string> packageNames)
        {
            if (_isSearching)
            {
                CustomMessageBox.Show("Search is already in progress.", "Search", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            // Queue-based downloads allow concurrent operations, so no need to block search

            _isSearching = true;
            _searchResults.Clear();
            ParseButton.IsEnabled = false;

            try
            {
                SetStatus($"Searching for {packageNames.Count} package(s)...");

                // First, search locally
                await SearchLocally(packageNames);

                // Then, search online for ALL packages to check for updates
                // This includes both missing packages AND local packages (to check for newer versions)
                if (packageNames.Any())
                {
                    await SearchOnline(packageNames);
                }

                var foundCount = _searchResults.Count(r => r.IsLocal);
                var availableOnlineCount = _searchResults.Count(r => r.IsAvailableOnline && !r.IsLocal);
                var notFoundCount = _searchResults.Count(r => !r.IsLocal && !r.IsAvailableOnline);
                var updatesAvailableCount = _searchResults.Count(r => r.HasNewerVersionOnline);

                if (updatesAvailableCount > 0)
                {
                    SetStatus($"Found: {foundCount} local ({updatesAvailableCount} updates available), {availableOnlineCount} online, {notFoundCount} not found");
                }
                else
                {
                    SetStatus($"Found: {foundCount} local, {availableOnlineCount} online, {notFoundCount} not found");
                }
                
                // Apply filter
                ApplyFilter();
                
                // Enable download buttons
                UpdateDownloadButtons();
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error during search: {ex.Message}", "Search Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Search failed");
            }
            finally
            {
                _isSearching = false;
                ParseButton.IsEnabled = true;
            }
        }

        private async Task SearchLocally(List<string> packageNames)
        {
            await Task.Run(() =>
            {
                // Build lookup dictionaries ONCE for all packages (major performance improvement)
                Dictionary<string, FileInfo> exactMatchLookup = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, List<FileInfo>> baseNameLookup = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
                
                try
                {
                    // Scan AddonPackages folder once - including subfolders
                    if (Directory.Exists(_addonPackagesFolder))
                    {
                        var loadedFiles = Directory.GetFiles(_addonPackagesFolder, "*.var", SearchOption.AllDirectories);
                        foreach (var filePath in loadedFiles)
                        {
                            var fileInfo = new FileInfo(filePath);
                            var fileName = Path.GetFileNameWithoutExtension(filePath);
                            
                            // Add to exact match lookup
                            exactMatchLookup[fileName] = fileInfo;
                            
                            // Add to base name lookup (for version matching)
                            var baseName = ExtractBaseName(fileName);
                            if (!baseNameLookup.ContainsKey(baseName))
                                baseNameLookup[baseName] = new List<FileInfo>();
                            baseNameLookup[baseName].Add(fileInfo);
                        }
                    }
                    
                    // Scan AllPackages folder once - including subfolders
                    if (Directory.Exists(_allPackagesFolder))
                    {
                        var unloadedFiles = Directory.GetFiles(_allPackagesFolder, "*.var", SearchOption.AllDirectories);
                        foreach (var filePath in unloadedFiles)
                        {
                            var fileInfo = new FileInfo(filePath);
                            var fileName = Path.GetFileNameWithoutExtension(filePath);
                            
                            // Only add if not already in exact match (AddonPackages takes priority)
                            if (!exactMatchLookup.ContainsKey(fileName))
                            {
                                exactMatchLookup[fileName] = fileInfo;
                            }
                            
                            // Add to base name lookup
                            var baseName = ExtractBaseName(fileName);
                            if (!baseNameLookup.ContainsKey(baseName))
                                baseNameLookup[baseName] = new List<FileInfo>();
                            baseNameLookup[baseName].Add(fileInfo);
                        }
                    }
                }
                catch (Exception)
                {
                    // If directory scan fails, continue with empty lookups
                }
                
                // Now perform fast O(1) lookups for each package
                foreach (var packageName in packageNames)
                {
                    var result = new PackageSearchResult { PackageName = packageName };
                    FileInfo foundFile = null;
                    
                    // Try exact match first (fastest)
                    if (exactMatchLookup.TryGetValue(packageName, out foundFile))
                    {
                        result.IsLocal = true;
                        result.Location = foundFile.FullName;
                        result.Size = foundFile.Length;
                        result.SizeFormatted = FormatHelper.FormatFileSize(foundFile.Length);
                        result.LocalPackageName = Path.GetFileNameWithoutExtension(foundFile.FullName);
                    }
                    else
                    {
                        // Try base name match (for version variations)
                        var baseName = ExtractBaseName(packageName);
                        if (baseNameLookup.TryGetValue(baseName, out var matchingFiles) && matchingFiles.Count > 0)
                        {
                            // Pick the HIGHEST version (not just first match)
                            foundFile = matchingFiles
                                .OrderByDescending(f => ExtractVersionFromPackageName(Path.GetFileNameWithoutExtension(f.FullName)))
                                .First();
                            
                            result.IsLocal = true;
                            result.Location = foundFile.FullName;
                            result.Size = foundFile.Length;
                            result.SizeFormatted = FormatHelper.FormatFileSize(foundFile.Length);
                            result.LocalPackageName = Path.GetFileNameWithoutExtension(foundFile.FullName);
                        }
                        else
                        {
                            result.IsLocal = false;
                            result.Location = "Not found locally";
                        }
                    }

                    Dispatcher.Invoke(() => _searchResults.Add(result));
                }
            });
        }
        
        /// <summary>
        /// Extracts base package name without version number for efficient lookups
        /// </summary>
        private string ExtractBaseName(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return packageName;
            
            var lastDotIndex = packageName.LastIndexOf('.');
            if (lastDotIndex > 0 && lastDotIndex < packageName.Length - 1)
            {
                var lastPart = packageName.Substring(lastDotIndex + 1);
                if (int.TryParse(lastPart, out _))
                {
                    return packageName.Substring(0, lastDotIndex);
                }
            }
            
            return packageName;
        }

        private async Task SearchOnline(List<string> missingPackages)
        {
            try
            {
                // Initialize HubService and UpdateChecker if not already done
                if (_hubService == null)
                {
                    _hubService = new HubService();
                }
                
                if (_updateChecker == null)
                {
                    _updateChecker = new PackageUpdateChecker(_hubService);
                }
                
                // Check if package source is already loaded
                bool usingLocalLinks = _updateChecker.IsUsingLocalLinks;
                int existingCount = usingLocalLinks 
                    ? _updateChecker.GetAllPackageNames()?.Count ?? 0 
                    : _hubService.GetPackageCount();
                
                if (existingCount > 0)
                {
                    // Source already loaded
                    string source = usingLocalLinks ? "local links.txt" : "Hub";
                    SetStatus($"Using {source}: {existingCount:N0} packages");
                }
                else
                {
                    // Need to load package source
                    SetStatus("Loading package source...");
                    
                    bool loaded = await _updateChecker.LoadPackageSourceAsync();

                    if (!loaded)
                    {
                        SetStatus("Failed to load package source");
                        CustomMessageBox.Show("Failed to load package source.\n\nPlease check your internet connection or ensure links.txt exists.",
                            "Source Load Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    
                    // Verify source has packages
                    usingLocalLinks = _updateChecker.IsUsingLocalLinks;
                    int packageCount = usingLocalLinks 
                        ? _updateChecker.GetAllPackageNames()?.Count ?? 0 
                        : _hubService.GetPackageCount();
                    
                    if (packageCount == 0)
                    {
                        SetStatus("Package source is empty");
                        CustomMessageBox.Show("The package source is empty or could not be loaded.",
                            "Source Empty", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    
                    string sourceName = usingLocalLinks ? "Local links" : "Hub packages";
                    SetStatus($"{sourceName} loaded: {packageCount:N0} packages available");
                    
                    // Update the database status label
                    UpdateDatabaseStatus();
                }

                foreach (var packageName in missingPackages)
                {
                    var result = _searchResults.FirstOrDefault(r => r.PackageName == packageName);
                    if (result == null) continue;

                    // Check if package is available
                    string downloadUrl = null;
                    string onlinePackageName = null;
                    int onlineVersion = -1;
                    
                    if (usingLocalLinks)
                    {
                        // Check local links.txt
                        downloadUrl = _updateChecker.GetDownloadUrl(packageName);
                        if (!string.IsNullOrEmpty(downloadUrl))
                        {
                            onlinePackageName = packageName;
                            var versionStr = ExtractVersionFromPackageName(packageName);
                            int.TryParse(versionStr.Split('.').FirstOrDefault() ?? "0", out onlineVersion);
                        }
                    }
                    else
                    {
                        // Check Hub - get resource ID to verify package exists
                        var baseName = ExtractBaseName(packageName);
                        var hubLatestVersion = _hubService.GetLatestVersion(baseName);
                        
                        if (hubLatestVersion > 0)
                        {
                            onlinePackageName = $"{baseName}.{hubLatestVersion}";
                            onlineVersion = hubLatestVersion;
                            // Hub download URL will be fetched when actually downloading
                            downloadUrl = "hub"; // Placeholder to indicate available on Hub
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(downloadUrl))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            result.IsAvailableOnline = true;
                            result.DownloadUrl = downloadUrl;
                            result.OnlineVersion = onlinePackageName;
                            
                            // Check if local package exists and if online version is newer
                            if (result.IsLocal && !string.IsNullOrEmpty(result.LocalPackageName))
                            {
                                try
                                {
                                    var localVersionStr = ExtractVersionFromPackageName(result.LocalPackageName);
                                    int.TryParse(localVersionStr.Split('.').FirstOrDefault() ?? "0", out int localVersionInt);
                                    
                                    if (onlineVersion > localVersionInt)
                                    {
                                        result.HasNewerVersionOnline = true;
                                    }
                                }
                                catch (Exception)
                                {
                                    result.HasNewerVersionOnline = false;
                                }
                            }
                            else if (!result.IsLocal)
                            {
                                // Package not found locally but available online
                                if (!packageName.Contains(".") || !char.IsDigit(packageName[packageName.Length - 1]))
                                {
                                    result.HasNewerVersionOnline = true;
                                }
                            }
                            
                            if (string.IsNullOrEmpty(result.Location) || result.Location == "Not found locally")
                            {
                                result.Location = "Available for download";
                            }
                        });
                    }
                    else
                    {
                        Dispatcher.Invoke(() =>
                        {
                            result.IsAvailableOnline = false;
                            if (!result.IsLocal)
                            {
                                result.Location = "Not found anywhere";
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Online search error: {ex.Message}");
            }
        }

        #endregion

        #region Download Logic

        private async void DownloadPackages(List<PackageSearchResult> selectedItems)
        {
            if (!selectedItems.Any())
            {
                return;
            }

            _totalDownloads = selectedItems.Count;
            _completedDownloads = 0;
            _failedDownloads = 0;
            
            try
            {
                SetStatus($"Queueing {selectedItems.Count} package(s) for download...");

                int queuedCount = 0;
                for (int i = 0; i < selectedItems.Count; i++)
                {
                    var item = selectedItems[i];
                    
                    // Use OnlineVersion if available (for updates), otherwise use PackageName
                    string packageToDownload = !string.IsNullOrEmpty(item.OnlineVersion) ? item.OnlineVersion : item.PackageName;
                    
                    // Check if using local links or Hub
                    bool usingLocalLinks = _updateChecker?.IsUsingLocalLinks ?? false;
                    PackageDownloadInfo downloadInfo = null;
                    
                    if (usingLocalLinks)
                    {
                        // Get download URL from local links
                        var downloadUrl = _updateChecker?.GetDownloadUrl(packageToDownload);
                        if (!string.IsNullOrEmpty(downloadUrl))
                        {
                            downloadInfo = new PackageDownloadInfo
                            {
                                PackageName = packageToDownload,
                                DownloadUrl = downloadUrl
                            };
                        }
                        else
                        {
                        }
                    }
                    else
                    {
                        // For Hub, we need to get the resource detail to get the download URL
                        // First, try to get the package info from the downloader (if it has cached data)
                        downloadInfo = _packageDownloader.GetPackageInfo(packageToDownload);
                        
                        if (downloadInfo == null)
                        {
                            // If not in downloader cache, try to get from Hub using the package name
                            try
                            {
                                var hubDetail = await _hubService.GetResourceDetailAsync(packageToDownload, isPackageName: true);
                                
                                if (hubDetail != null)
                                {
                                    // Try to get download URL from HubFiles or ExternalDownloadUrl
                                    string downloadUrl = null;
                                    
                                    if (hubDetail.HubFiles != null && hubDetail.HubFiles.Count > 0)
                                    {
                                        var hubFile = hubDetail.HubFiles[0];
                                        downloadUrl = hubFile.EffectiveDownloadUrl;
                                    }
                                    else if (!string.IsNullOrEmpty(hubDetail.ExternalDownloadUrl))
                                    {
                                        downloadUrl = hubDetail.ExternalDownloadUrl;
                                    }
                                    
                                    if (!string.IsNullOrEmpty(downloadUrl))
                                    {
                                        downloadInfo = new PackageDownloadInfo
                                        {
                                            PackageName = packageToDownload,
                                            DownloadUrl = downloadUrl
                                        };
                                    }
                                    else
                                    {
                                    }
                                }
                                else
                                {
                                }
                            }
                            catch (Exception ex)
                            {
                            }
                            
                            // Add a small delay between Hub lookups to avoid rate limiting
                            if (i < selectedItems.Count - 1)
                            {
                                await Task.Delay(100);
                            }
                        }
                        else
                        {
                        }
                    }
                    
                    if (downloadInfo != null)
                    {
                        // Add to queue - downloads will start automatically
                        bool queued = _downloadQueueManager.EnqueueDownload(packageToDownload, downloadInfo);
                        
                        if (queued)
                        {
                            queuedCount++;
                            item.IsDownloading = true;
                            item.StatusText = "Queued";
                            item.StatusColor = "#FFA500"; // Orange
                            item.ProgressText = "Waiting in queue...";
                            item.ProgressVisibility = Visibility.Visible;
                        }
                        else
                        {
                            item.StatusText = "Already queued";
                            item.StatusColor = "#9E9E9E"; // Gray
                        }
                    }
                    else
                    {
                        item.StatusText = "✗ Not found";
                        item.StatusColor = "#F44336"; // Red
                        item.ProgressText = "Package not in database";
                    }
                }
                
                
                SetStatus($"Queued {_totalDownloads} package(s) for download");
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error queueing downloads: {ex.Message}", "Queue Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Failed to queue downloads");
            }
        }

        #endregion

        #region Download Event Handlers

        private void OnDownloadProgress(object sender, DownloadProgressEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // Find the result that matches this download (check both PackageName and OnlineVersion)
                var result = _searchResults.FirstOrDefault(r => 
                    r.OnlineVersion?.Equals(e.PackageName, StringComparison.OrdinalIgnoreCase) == true ||
                    r.PackageName.Equals(e.PackageName, StringComparison.OrdinalIgnoreCase));
                    
                if (result != null && result.IsDownloading)
                {
                    result.StatusText = "Downloading";
                    result.StatusColor = "#03A9F4"; // Light blue
                    var mbDownloaded = e.DownloadedBytes / (1024.0 * 1024.0);
                    var sourceText = !string.IsNullOrEmpty(e.DownloadSource) ? $" ({e.DownloadSource})" : "";
                    result.ProgressText = $"{mbDownloaded:F1} MB downloaded...{sourceText}";
                }
                else
                {
                }
            });
        }

        private void OnDownloadCompleted(object sender, DownloadCompletedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // Find the result that matches this download (check both PackageName and OnlineVersion)
                var result = _searchResults.FirstOrDefault(r => 
                    r.OnlineVersion?.Equals(e.PackageName, StringComparison.OrdinalIgnoreCase) == true ||
                    r.PackageName.Equals(e.PackageName, StringComparison.OrdinalIgnoreCase));
                    
                if (result != null)
                {
                    result.IsDownloading = false;
                    result.StatusText = "✓ Completed";
                    result.StatusColor = "#4CAF50"; // Green
                    result.ProgressText = "Download completed successfully";
                    result.ProgressVisibility = Visibility.Collapsed;
                    result.Location = e.FilePath;
                    result.LocalPackageName = Path.GetFileNameWithoutExtension(e.FilePath);
                    result.IsLocal = true; // Mark as local now
                    result.HasNewerVersionOnline = false; // No longer has update available
                    result.OnlineVersion = null; // Clear online version so display shows just local version
                    
                    // Update file size
                    if (File.Exists(e.FilePath))
                    {
                        var fileInfo = new FileInfo(e.FilePath);
                        result.Size = fileInfo.Length;
                        result.SizeFormatted = FormatHelper.FormatFileSize(fileInfo.Length);
                    }
                    
                    _completedDownloads++;
                    
                    // Notify MainWindow to refresh package list and dependencies
                    _onPackageDownloadedCallback?.Invoke(e.PackageName, e.FilePath);
                    
                    // Reapply filter in case "Hide local packages" is checked
                    ApplyFilter();
                }
                else
                {
                }
            });
        }

        private void OnDownloadError(object sender, DownloadErrorEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                
                // Find the result that matches this download (check both PackageName and OnlineVersion)
                var result = _searchResults.FirstOrDefault(r => 
                    r.OnlineVersion?.Equals(e.PackageName, StringComparison.OrdinalIgnoreCase) == true ||
                    r.PackageName.Equals(e.PackageName, StringComparison.OrdinalIgnoreCase));
                    
                if (result != null)
                {
                    result.IsDownloading = false;
                    result.StatusText = "✗ Failed";
                    result.StatusColor = "#F44336"; // Red
                    result.ProgressText = e.ErrorMessage;
                    result.ProgressVisibility = Visibility.Visible; // Keep visible to show error
                    result.Location = $"Download failed: {e.ErrorMessage}";
                    _failedDownloads++;
                }
                else
                {
                }
                
                SetStatus($"Download error: {e.ErrorMessage}");
            });
        }

        #endregion

        #region Button Event Handlers

        private async void ParseButton_Click(object sender, RoutedEventArgs e)
        {
            var input = InputTextBox.Text;
            if (string.IsNullOrWhiteSpace(input))
            {
                CustomMessageBox.Show("Please enter package names or drag files to search.", 
                    "No Input", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var packageNames = ParsePackageNames(input);
            if (!packageNames.Any())
            {
                CustomMessageBox.Show("No valid package names found in the input.", 
                    "Parse Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Check if package downloader is initialized and database is loaded
            if (_packageDownloader == null || _packageDownloader.GetPackageCount() == 0)
            {
                // Try to load offline database first if not already loaded
                if (_packageDownloader != null && _packageDownloader.GetPackageCount() == 0)
                {
                    await TryLoadOfflineDatabase();
                }
                
                // If still empty after offline load attempt, offer database update
                if (_packageDownloader.GetPackageCount() == 0)
                {
                    // Always grant network access and update database
                    bool updateDatabase = true;
                    
                    if (updateDatabase)
                    {
                        // Update database using the main window's method
                        SetStatus("Updating package database...");
                        bool success = await _updateDatabaseCallback();
                        
                        if (!success)
                        {
                            SetStatus("Database update failed");
                            return; // Don't proceed with search
                        }
                        
                        // Update the database status label
                        UpdateDatabaseStatus();
                        
                        int packageCount = _packageDownloader.GetPackageCount();
                        SetStatus($"Database updated: {packageCount:N0} packages");
                    }
                    else if (_packageDownloader.GetPackageCount() == 0)
                    {
                        CustomMessageBox.Show("The package database is empty. Please update the database first.",
                            "Database Empty", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
            }

            await PerformSearch(packageNames);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            InputTextBox.Clear();
            _searchResults.Clear();
            ResultsDataGrid.ItemsSource = _searchResults;
            if (HideLocalCheckBox != null)
            {
                HideLocalCheckBox.IsChecked = false;
            }
            SetStatus("Ready");
            UpdateDownloadButtons();
        }

        private void UpdateDownloadButtons()
        {
            Dispatcher.Invoke(() =>
            {
                var missingCount = _searchResults.Count(r => r.CanDownload);
                var selectedCount = ResultsDataGrid.SelectedItems.Cast<PackageSearchResult>().Count(r => r.CanDownload);

                DownloadMissingButton.Content = missingCount > 0 
                    ? $"Download Missing ({missingCount})" 
                    : "Download Missing";
                
                DownloadSelectedButton.Content = selectedCount > 0 
                    ? $"Download Selected ({selectedCount})" 
                    : "Download Selected";
                    
                DownloadMissingButton.IsEnabled = missingCount > 0;
                DownloadSelectedButton.IsEnabled = selectedCount > 0;
            });
        }



        private void ResultsDataGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateDownloadButtons();
        }

        private void ResultsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ResultsDataGrid.SelectedItem is PackageSearchResult result)
            {
                if (result.IsLocal && !string.IsNullOrEmpty(result.Location) && File.Exists(result.Location))
                {
                    // Open folder and select file for local packages
                    try
                    {
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{result.Location}\"");
                    }
                    catch (Exception ex)
                    {
                        CustomMessageBox.Show($"Error opening folder: {ex.Message}",
                            "Open Folder Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else if (result.CanDownload)
                {
                    // Download package for online available packages - no confirmation
                    var downloadInfo = _packageDownloader.GetPackageInfo(result.PackageName);
                    if (downloadInfo != null)
                    {
                        bool queued = _downloadQueueManager.EnqueueDownload(result.PackageName, downloadInfo);
                        if (queued)
                        {
                            result.IsDownloading = true;
                            result.StatusText = "Queued";
                            result.StatusColor = "#FFA500"; // Orange
                            result.ProgressText = "Waiting in queue...";
                            result.ProgressVisibility = Visibility.Visible;
                        }
                    }
                }
            }
        }

        private void HideLocalCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (HideLocalCheckBox == null)
            {
                ResultsDataGrid.ItemsSource = _searchResults;
                return;
            }

            if (HideLocalCheckBox.IsChecked == true)
            {
                // Hide packages that are local (only show missing/online packages)
                var filteredResults = _searchResults.Where(r => !r.IsLocal).ToList();
                ResultsDataGrid.ItemsSource = new ObservableCollection<PackageSearchResult>(filteredResults);
            }
            else
            {
                // Show all packages
                ResultsDataGrid.ItemsSource = _searchResults;
            }
            
            UpdateDownloadButtons();
        }

        private void DownloadMissingButton_Click(object sender, RoutedEventArgs e)
        {
            var missingPackages = _searchResults.Where(r => r.CanDownload).ToList();
            
            if (!missingPackages.Any())
            {
                CustomMessageBox.Show("No missing packages available for download.",
                    "Download Missing", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DownloadPackages(missingPackages);
        }

        private void DownloadSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = ResultsDataGrid.SelectedItems.Cast<PackageSearchResult>()
                .Where(r => r.CanDownload)
                .ToList();

            if (!selectedItems.Any())
            {
                CustomMessageBox.Show("Please select packages that are available for download.", 
                    "Download", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DownloadPackages(selectedItems);
        }


        private void LocalDot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PackageSearchResult result)
            {
                if (result.IsLocal && !string.IsNullOrEmpty(result.Location) && File.Exists(result.Location))
                {
                    try
                    {
                        // Open folder and select the file
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{result.Location}\"");
                    }
                    catch (Exception ex)
                    {
                        CustomMessageBox.Show($"Error opening folder: {ex.Message}",
                            "Open Folder Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void OnlineDot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PackageSearchResult result)
            {
                if (result.CanDownload)
                {
                    // Download this single package - no confirmation
                    var downloadInfo = _packageDownloader.GetPackageInfo(result.PackageName);
                    if (downloadInfo != null)
                    {
                        bool queued = _downloadQueueManager.EnqueueDownload(result.PackageName, downloadInfo);
                        if (queued)
                        {
                            result.IsDownloading = true;
                            result.StatusText = "Queued";
                            result.StatusColor = "#FFA500"; // Orange
                            result.ProgressText = "Waiting in queue...";
                            result.ProgressVisibility = Visibility.Visible;
                        }
                    }
                }
            }
        }

        
        private void CancelDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PackageSearchResult result)
            {
                // Use OnlineVersion if available, otherwise use PackageName
                string packageToCancel = !string.IsNullOrEmpty(result.OnlineVersion) ? result.OnlineVersion : result.PackageName;
                
                // Try to cancel active download first
                bool cancelledActive = _downloadQueueManager.CancelDownload(packageToCancel);
                
                // Try to remove from queue if not actively downloading
                bool removedFromQueue = false;
                if (!cancelledActive)
                {
                    removedFromQueue = _downloadQueueManager.RemoveFromQueue(packageToCancel);
                }
                
                if (cancelledActive || removedFromQueue)
                {
                    result.IsDownloading = false;
                    result.StatusText = "Cancelled";
                    result.StatusColor = "#9E9E9E"; // Gray
                    result.ProgressText = "Cancelled by user";
                    result.ProgressVisibility = Visibility.Collapsed;
                    
                    string action = cancelledActive ? "Cancelled active download" : "Removed from queue";
                    SetStatus($"{action}: {packageToCancel}");
                }
                else
                {
                }
            }
        }
        
        private void CancelAllButton_Click(object sender, RoutedEventArgs e)
        {
            // Cancel all active downloads
            var activeDownloads = _downloadQueueManager.ActiveDownloads.ToList();
            foreach (var download in activeDownloads)
            {
                _downloadQueueManager.CancelDownload(download.PackageName);
            }
            
            // Clear the queue
            _downloadQueueManager.ClearQueue();
            
            // Reset all downloading/queued items in UI
            foreach (var result in _searchResults.Where(r => r.IsDownloading).ToList())
            {
                result.IsDownloading = false;
                result.StatusText = "Cancelled";
                result.StatusColor = "#9E9E9E"; // Gray
                result.ProgressText = "Cancelled by user";
                result.ProgressVisibility = Visibility.Collapsed;
            }
            
            SetStatus("All downloads cancelled");
        }

        #endregion

        #region Utility Methods

        protected override void OnClosed(EventArgs e)
        {
            // Unsubscribe from events
            _packageDownloader.DownloadProgress -= OnDownloadProgress;
            _packageDownloader.DownloadCompleted -= OnDownloadCompleted;
            _packageDownloader.DownloadError -= OnDownloadError;
            
            base.OnClosed(e);
        }

        /// <summary>
        /// Extract version number from package name (e.g., "package.1.2.3" -> "1.2.3")
        /// </summary>
        private static string ExtractVersionFromPackageName(string packageName)
        {
            if (string.IsNullOrEmpty(packageName)) return "0";
            
            // Find the last dot-separated segment that looks like a version
            var parts = packageName.Split('.');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (int.TryParse(parts[i], out _))
                {
                    // Found a numeric part, collect all trailing numeric parts
                    var versionParts = new List<string>();
                    for (int j = i; j < parts.Length; j++)
                    {
                        if (int.TryParse(parts[j], out _))
                            versionParts.Add(parts[j]);
                        else
                            break;
                    }
                    return string.Join(".", versionParts);
                }
            }
            return "0";
        }

        /// <summary>
        /// Compare two version strings (returns >0 if v1 > v2, <0 if v1 < v2, 0 if equal)
        /// </summary>
        private static int CompareVersions(string v1, string v2)
        {
            var parts1 = v1.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
            var parts2 = v2.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
            
            int maxLength = Math.Max(parts1.Length, parts2.Length);
            for (int i = 0; i < maxLength; i++)
            {
                int p1 = i < parts1.Length ? parts1[i] : 0;
                int p2 = i < parts2.Length ? parts2[i] : 0;
                
                if (p1 != p2)
                    return p1.CompareTo(p2);
            }
            return 0;
        }

        /// <summary>
        /// Marks packages as having updates available
        /// Called after update checker finds updates
        /// </summary>
        public void MarkPackagesWithUpdates(List<string> packageNamesWithUpdates)
        {
            if (packageNamesWithUpdates == null || packageNamesWithUpdates.Count == 0)
                return;
            
            Dispatcher.Invoke(() =>
            {
                foreach (var packageName in packageNamesWithUpdates)
                {
                    // Find matching result in search results by package name
                    var result = _searchResults.FirstOrDefault(r => 
                        r.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));
                    
                    if (result != null && result.IsLocal)
                    {
                        result.HasNewerVersionOnline = true;
                    }
                }
            });
        }

        #endregion
    }

    #region PackageSearchResult Model

    /// <summary>
    /// Represents a search result for a package
    /// </summary>
    public class PackageSearchResult : INotifyPropertyChanged
    {
        private string _packageName;
        private string _localPackageName;
        private bool _isLocal;
        private bool _isAvailableOnline;
        private bool _isDownloading;
        private bool _hasNewerVersionOnline;
        private string _location;
        private long _size;
        private string _sizeFormatted;
        private string _statusText;
        private string _statusColor = "#00000000"; // Default to transparent to avoid BrushConverter null error
        private string _progressText;
        private Visibility _progressVisibility = Visibility.Collapsed;
        private string _downloadUrl;
        private string _onlineVersion;

        public string PackageName
        {
            get => _packageName;
            set { _packageName = value; OnPropertyChanged(nameof(PackageName)); OnPropertyChanged(nameof(DisplayName)); }
        }
        
        public string LocalPackageName
        {
            get => _localPackageName;
            set { _localPackageName = value; OnPropertyChanged(nameof(LocalPackageName)); OnPropertyChanged(nameof(LocalVersionNumber)); }
        }

        public string OnlineVersion
        {
            get => _onlineVersion;
            set { _onlineVersion = value; OnPropertyChanged(nameof(OnlineVersion)); OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(OnlineVersionNumber)); }
        }

        public string DisplayName
        {
            get
            {
                // For local packages, show the actual local version
                if (IsLocal && !string.IsNullOrEmpty(_localPackageName))
                {
                    var baseName = GetBaseNameWithoutVersion(_localPackageName);
                    var localVer = ExtractVersion(_localPackageName);
                    
                    // If update available, show: BaseName v12 → v14
                    if (HasNewerVersionOnline && !string.IsNullOrEmpty(_onlineVersion))
                    {
                        var onlineVer = ExtractVersion(_onlineVersion);
                        return $"{baseName} v{localVer} → v{onlineVer}";
                    }
                    
                    // Otherwise just show: BaseName v12
                    return $"{baseName} v{localVer}";
                }
                
                // For non-local packages with online version available
                if (!IsLocal && !string.IsNullOrEmpty(_onlineVersion))
                {
                    var baseName = GetBaseNameWithoutVersion(_onlineVersion);
                    var onlineVer = ExtractVersion(_onlineVersion);
                    return $"{baseName} v{onlineVer}";
                }
                
                // Fallback to package name
                return _packageName;
            }
        }
        
        private string GetBaseNameWithoutVersion(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return packageName;
            
            // Remove version number (last segment after dot if it's a number)
            var lastDotIndex = packageName.LastIndexOf('.');
            if (lastDotIndex > 0)
            {
                var lastSegment = packageName.Substring(lastDotIndex + 1);
                if (int.TryParse(lastSegment, out _))
                {
                    return packageName.Substring(0, lastDotIndex);
                }
            }
            
            return packageName;
        }

        private string ExtractVersion(string fullPackageName)
        {
            if (string.IsNullOrEmpty(fullPackageName))
                return "";
            
            var lastDot = fullPackageName.LastIndexOf('.');
            if (lastDot > 0 && lastDot < fullPackageName.Length - 1)
            {
                var version = fullPackageName.Substring(lastDot + 1);
                if (int.TryParse(version, out _))
                    return version;
            }
            return fullPackageName;
        }
        
        // Public property to expose the online version number for UI binding
        public string OnlineVersionNumber
        {
            get
            {
                if (string.IsNullOrEmpty(_onlineVersion))
                    return "";
                return ExtractVersion(_onlineVersion);
            }
        }
        
        // Public property to expose the local version number for UI binding
        public string LocalVersionNumber
        {
            get
            {
                if (string.IsNullOrEmpty(_localPackageName))
                    return "";
                return ExtractVersion(_localPackageName);
            }
        }

        public bool IsLocal
        {
            get => _isLocal;
            set 
            { 
                _isLocal = value; 
                OnPropertyChanged(nameof(IsLocal));
                OnPropertyChanged(nameof(StatusDotColor));
                OnPropertyChanged(nameof(StatusTooltip));
                OnPropertyChanged(nameof(CanDownload));
            }
        }

        public bool IsAvailableOnline
        {
            get => _isAvailableOnline;
            set 
            { 
                _isAvailableOnline = value; 
                OnPropertyChanged(nameof(IsAvailableOnline));
                OnPropertyChanged(nameof(StatusDotColor));
                OnPropertyChanged(nameof(StatusTooltip));
                OnPropertyChanged(nameof(CanDownload));
            }
        }

        public bool IsDownloading
        {
            get => _isDownloading;
            set 
            { 
                _isDownloading = value; 
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(CanDownload));
            }
        }

        public bool HasNewerVersionOnline
        {
            get => _hasNewerVersionOnline;
            set 
            { 
                _hasNewerVersionOnline = value; 
                OnPropertyChanged(nameof(HasNewerVersionOnline));
                OnPropertyChanged(nameof(StatusDotColor));
                OnPropertyChanged(nameof(StatusTooltip));
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        public string Location
        {
            get => _location;
            set { _location = value; OnPropertyChanged(nameof(Location)); }
        }

        public long Size
        {
            get => _size;
            set { _size = value; OnPropertyChanged(nameof(Size)); }
        }

        public string SizeFormatted
        {
            get => _sizeFormatted;
            set { _sizeFormatted = value; OnPropertyChanged(nameof(SizeFormatted)); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        public string StatusColor
        {
            get => _statusColor;
            set { _statusColor = value; OnPropertyChanged(nameof(StatusColor)); }
        }

        public string ProgressText
        {
            get => _progressText;
            set { _progressText = value; OnPropertyChanged(nameof(ProgressText)); }
        }

        public Visibility ProgressVisibility
        {
            get => _progressVisibility;
            set { _progressVisibility = value; OnPropertyChanged(nameof(ProgressVisibility)); }
        }

        public string DownloadUrl
        {
            get => _downloadUrl;
            set { _downloadUrl = value; OnPropertyChanged(nameof(DownloadUrl)); }
        }

        // Simplified status dot color (single dot system)
        public string StatusDotColor
        {
            get
            {
                if (HasNewerVersionOnline) return "#FFA500"; // Orange - update available
                if (IsLocal) return "#4CAF50"; // Green - found locally
                if (IsAvailableOnline) return "#2196F3"; // Blue - available online for download
                return "#F44336"; // Red - not found anywhere
            }
        }
        
        public string StatusTooltip
        {
            get
            {
                if (IsLocal && HasNewerVersionOnline) return "Local (newer version available)";
                if (IsLocal) return "Found locally";
                if (IsAvailableOnline) return "Available online";
                return "Not found";
            }
        }
        
        public bool CanDownload => ((IsAvailableOnline && !IsLocal) || HasNewerVersionOnline) && !IsDownloading;

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #endregion

    #region StatusWriter

    /// <summary>
    /// Custom TextWriter to capture console output and update status bar
    /// </summary>
    internal class StatusWriter : System.IO.TextWriter
    {
        private readonly PackageSearchWindow _window;
        
        public StatusWriter(PackageSearchWindow window)
        {
            _window = window;
        }
        
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        
        public override void WriteLine(string value)
        {
            if (value != null && value.Contains("[PackageDownloader]"))
            {
                // Extract the message part
                var message = value.Substring(value.IndexOf("]") + 1).Trim();
                
                // Update status bar on UI thread
                _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (message.Contains("Retry attempt"))
                    {
                        _window.SetStatus(message);
                    }
                    else if (message.Contains("Waiting") && message.Contains("firewall"))
                    {
                        _window.SetStatus("Waiting for firewall approval...");
                    }
                    else if (message.Contains("Attempting to load"))
                    {
                        _window.SetStatus("Downloading package database...");
                    }
                    else if (message.Contains("Successfully loaded"))
                    {
                        _window.SetStatus("Database loaded successfully");
                    }
                }));
            }
            
            // Also write to debug output
            System.Diagnostics.Debug.WriteLine(value);
        }
    }

    #endregion
}

