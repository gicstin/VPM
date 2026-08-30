using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using VPM.Models;

namespace VPM.Windows
{
    public partial class HubBrowserWindow
    {
        private HubWorkView _workView = HubWorkView.Browse;
        private HubLibraryFilter _libraryFilter = HubLibraryFilter.All;
        private ICollectionView _resultsView;
        private readonly ObservableCollection<HubFileViewModel> _updateItems = new ObservableCollection<HubFileViewModel>();
        private readonly ObservableCollection<HubFileViewModel> _missingItems = new ObservableCollection<HubFileViewModel>();
        private bool _forceClose;
        private bool _downloadDockCollapsed;
        private bool _updatesLoaded;
        private bool _missingLoaded;
        private int _missingNotFoundCount;

        /// <summary>MainWindow refreshes its package list when Hub downloads land on disk.</summary>
        public event EventHandler LibraryRefreshNeeded;

        public HubWorkView CurrentWorkView => _workView;

        public bool HasActiveHubDownloads =>
            _downloadQueue != null &&
            _downloadQueue.Any(d => d.Status == DownloadStatus.Queued || d.Status == DownloadStatus.Downloading);

        private void SetupWorkViews()
        {
            if (UpdatesWorkGrid != null)
                UpdatesWorkGrid.ItemsSource = _updateItems;
            if (MissingWorkGrid != null)
                MissingWorkGrid.ItemsSource = _missingItems;

            if (_vm?.Results != null)
            {
                _resultsView = CollectionViewSource.GetDefaultView(_vm.Results);
                _resultsView.Filter = FilterBrowseResult;
                if (_resultsView is ICollectionViewLiveShaping live && live.CanChangeLiveFiltering)
                {
                    live.LiveFilteringProperties.Add(nameof(HubResource.InLibrary));
                    live.LiveFilteringProperties.Add(nameof(HubResource.UpdateAvailable));
                    live.LiveFilteringProperties.Add(nameof(HubResource.MissingDependencyCount));
                    live.LiveFilteringProperties.Add(nameof(HubResource.DependencyStatusResolved));
                    live.LiveFilteringProperties.Add(nameof(HubResource.CardDownloadStatusReady));
                    live.IsLiveFiltering = true;
                }

                _vm.Results.CollectionChanged += BrowseResults_CollectionChanged;
                foreach (var resource in _vm.Results)
                    resource.PropertyChanged += BrowseResource_PropertyChanged;
            }

            Closing += HubBrowserWindow_Closing;
            ApplyWorkViewChrome();
            ApplyLibraryFilterChrome();
            UpdateDownloadDock();
            if (StatusText != null)
            {
                DependencyPropertyDescriptor
                    .FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                    .AddValueChanged(StatusText, (_, __) => UpdateIdleStatusVisibility());
                UpdateIdleStatusVisibility();
            }
            ApplyHubChromeDensity();
        }

        private void BrowseResults_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems)
                {
                    if (item is HubResource oldResource)
                        oldResource.PropertyChanged -= BrowseResource_PropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is HubResource newResource)
                        newResource.PropertyChanged += BrowseResource_PropertyChanged;
                }
            }

            if (e.Action == NotifyCollectionChangedAction.Reset && _vm?.Results != null)
            {
                foreach (var resource in _vm.Results)
                    resource.PropertyChanged += BrowseResource_PropertyChanged;
            }

            UpdateFilteredEmptyState();
        }

        private void BrowseResource_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HubResource.InLibrary) ||
                e.PropertyName == nameof(HubResource.UpdateAvailable) ||
                e.PropertyName == nameof(HubResource.MissingDependencyCount) ||
                e.PropertyName == nameof(HubResource.DependencyStatusResolved))
            {
                Dispatcher.BeginInvoke(new Action(UpdateFilteredEmptyState), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        public void PrepareForAppExit()
        {
            _forceClose = true;
        }

        /// <summary>Re-open existing Hub window on a specific job (Browse / Updates / Missing).</summary>
        public void ShowWorkView(HubWorkView view)
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Show();
            Activate();
            SwitchWorkView(view);
        }

        public void SyncLocalPackages(Dictionary<string, string> localPackagePaths)
        {
            if (localPackagePaths == null)
                return;

            _localPackagePaths.Clear();
            foreach (var kvp in localPackagePaths)
            {
                if (!string.IsNullOrEmpty(kvp.Key) && !_localPackagePaths.ContainsKey(kvp.Key))
                    _localPackagePaths[kvp.Key] = kvp.Value;
            }

            BuildLocalPackageLookups();
            _updatesLoaded = false;
            _missingLoaded = false;
            _ = _vm?.RefreshLibraryStatusesAsync();
            RefreshBrowseFilter();

            if (_workView == HubWorkView.Updates)
                _ = LoadUpdatesViewAsync(force: true);
            else if (_workView == HubWorkView.Missing)
                _ = LoadMissingViewAsync(force: true);
        }

        private void HubBrowserWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_forceClose)
                return;

            e.Cancel = true;
            SaveHubBrowserState();
            try { _settingsManager?.SaveSettingsImmediate(); } catch { }
            Hide();
        }

        private void WorkViewTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton tab)
                return;

            if (ReferenceEquals(tab, TabWorkBrowse))
                SwitchWorkView(HubWorkView.Browse);
            else if (ReferenceEquals(tab, TabWorkUpdates))
                SwitchWorkView(HubWorkView.Updates);
            else if (ReferenceEquals(tab, TabWorkMissing))
                SwitchWorkView(HubWorkView.Missing);
        }

        private void SwitchWorkView(HubWorkView view)
        {
            if (_workView != view)
                _workView = view;

            if (TabWorkBrowse != null)
                TabWorkBrowse.IsChecked = view == HubWorkView.Browse;
            if (TabWorkUpdates != null)
                TabWorkUpdates.IsChecked = view == HubWorkView.Updates;
            if (TabWorkMissing != null)
                TabWorkMissing.IsChecked = view == HubWorkView.Missing;

            if (view == HubWorkView.Updates && !_updatesLoaded)
                SetUpdatesBusy("Checking for updates…");
            else if (view == HubWorkView.Missing && !_missingLoaded)
                SetMissingBusy("Scanning for missing dependencies…");

            ApplyWorkViewChrome();

            if (view == HubWorkView.Updates)
                _ = LoadUpdatesViewAsync(force: !_updatesLoaded);
            else if (view == HubWorkView.Missing)
                _ = LoadMissingViewAsync(force: !_missingLoaded);
        }

        private void ApplyWorkViewChrome()
        {
            var browse = _workView == HubWorkView.Browse;
            var updates = _workView == HubWorkView.Updates;
            var missing = _workView == HubWorkView.Missing;

            SetVis(BrowseFiltersRow, browse);
            SetVis(LibraryFilterBar, browse);
            SetVis(BrowseResultsHost, browse);
            SetVis(BrowseSearchHost, browse);
            SetVis(RefreshButton, browse);
            SetVis(BrowsePagerControls, browse);
            SetVis(UpdatesWorkPanel, updates);
            SetVis(MissingWorkPanel, missing);
            if (browse)
            {
                try { UpdateActiveFiltersUI(); } catch { }
                RefreshBrowseFilter();
            }
            else
            {
                SetVis(ActiveFiltersBorder, false);
            }
        }

        private static void SetVis(UIElement element, bool visible)
        {
            if (element != null)
                element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowAllLibraryFilter_Click(object sender, RoutedEventArgs e)
        {
            _libraryFilter = HubLibraryFilter.All;
            ApplyLibraryFilterChrome();
            RefreshBrowseFilter();
        }

        private void LibraryFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton radio)
                return;

            if (ReferenceEquals(radio, FilterLibAll))
                _libraryFilter = HubLibraryFilter.All;
            else if (ReferenceEquals(radio, FilterLibNotInLibrary))
                _libraryFilter = HubLibraryFilter.NotInLibrary;
            else if (ReferenceEquals(radio, FilterLibUpdates))
                _libraryFilter = HubLibraryFilter.Updates;
            else if (ReferenceEquals(radio, FilterLibMissing))
                _libraryFilter = HubLibraryFilter.MissingDeps;

            RefreshBrowseFilter();
        }

        private void ApplyLibraryFilterChrome()
        {
            if (FilterLibAll != null)
                FilterLibAll.IsChecked = _libraryFilter == HubLibraryFilter.All;
            if (FilterLibNotInLibrary != null)
                FilterLibNotInLibrary.IsChecked = _libraryFilter == HubLibraryFilter.NotInLibrary;
            if (FilterLibUpdates != null)
                FilterLibUpdates.IsChecked = _libraryFilter == HubLibraryFilter.Updates;
            if (FilterLibMissing != null)
                FilterLibMissing.IsChecked = _libraryFilter == HubLibraryFilter.MissingDeps;
        }

        private bool FilterBrowseResult(object item)
        {
            if (item is not HubResource resource)
                return false;

            return _libraryFilter switch
            {
                HubLibraryFilter.NotInLibrary => !resource.InLibrary,
                HubLibraryFilter.Updates => resource.UpdateAvailable,
                HubLibraryFilter.MissingDeps => resource.DependencyStatusResolved && resource.MissingDependencyCount > 0,
                _ => true
            };
        }

        private void RefreshBrowseFilter()
        {
            try { _resultsView?.Refresh(); } catch { }
            UpdateFilteredEmptyState();
        }

        private void UpdateFilteredEmptyState()
        {
            if (FilteredEmptyPanel == null || _workView != HubWorkView.Browse)
            {
                SetVis(FilteredEmptyPanel, false);
                return;
            }

            var total = _vm?.Results?.Count ?? 0;
            var visible = 0;
            if (_resultsView != null)
            {
                foreach (var _ in _resultsView)
                    visible++;
            }

            var filteredOut = total > 0 && visible == 0 && _libraryFilter != HubLibraryFilter.All;
            SetVis(FilteredEmptyPanel, filteredOut);

            if (FilteredEmptyText != null && filteredOut)
            {
                FilteredEmptyText.Text = _libraryFilter switch
                {
                    HubLibraryFilter.NotInLibrary => "Every result on this page is already in the library.",
                    HubLibraryFilter.Updates => "No updates on this page. Open the Updates view to scan the whole library.",
                    HubLibraryFilter.MissingDeps => "No resources on this page with missing dependencies. Open the Missing view to scan the whole library.",
                    _ => "No results match this library filter."
                };
            }
        }

        private async Task LoadUpdatesViewAsync(bool force)
        {
            if (!force && _updatesLoaded)
            {
                UpdateWorkViewStatus();
                return;
            }

            if (_isUpdatesCheckInProgress)
                return;

            _isUpdatesCheckInProgress = true;
            try
            {
                SetUpdatesBusy("Checking for updates…");
                BuildLocalPackageLookups();

                var updatesAvailable = new List<(string packageGroup, int localVersion, int hubVersion)>();
                if (_localPackageVersions != null)
                {
                    foreach (var kvp in _localPackageVersions)
                    {
                        var groupName = kvp.Key;
                        var localVersion = kvp.Value;
                        if (localVersion > 0 && _hubService.HasUpdate(groupName, localVersion))
                        {
                            var hubVersion = _hubService.GetLatestVersion(groupName);
                            if (hubVersion > localVersion)
                                updatesAvailable.Add((groupName, localVersion, hubVersion));
                        }
                    }
                }

                _updateItems.Clear();

                if (updatesAvailable.Count == 0)
                {
                    _updatesLoaded = true;
                    SetWorkViewEmpty(UpdatesEmptyPanel, UpdatesEmptyText, true, "All packages are up to date.");
                    SetUpdatesIdle("No updates available");
                    return;
                }

                var packageNames = updatesAvailable.Select(u => u.packageGroup + ".latest").ToList();
                var hubPackages = await _hubService.FindPackagesAsync(packageNames);

                foreach (var update in updatesAvailable)
                {
                    var packageKey = update.packageGroup + ".latest";
                    var filename = $"{update.packageGroup}.{update.hubVersion}.var";
                    var downloadUrl = "";
                    var fileSize = 0L;
                    var latestUrl = "";
                    var hasMetadata = false;

                    if (hubPackages != null && hubPackages.TryGetValue(packageKey, out var hubPackage) && hubPackage != null)
                    {
                        hasMetadata = true;
                        downloadUrl = !string.IsNullOrEmpty(hubPackage.LatestUrl)
                            ? hubPackage.LatestUrl
                            : hubPackage.DownloadUrl;
                        if (!string.IsNullOrEmpty(hubPackage.PackageName))
                            filename = hubPackage.PackageName;
                        fileSize = hubPackage.FileSize;
                        latestUrl = hubPackage.LatestUrl;
                    }

                    _updateItems.Add(new HubFileViewModel
                    {
                        Filename = filename,
                        PackageGroup = update.packageGroup,
                        VersionChange = $"{update.localVersion} → {update.hubVersion}",
                        FileSize = fileSize,
                        DownloadUrl = downloadUrl,
                        LatestUrl = latestUrl,
                        Status = hasMetadata ? "Update available" : "Index only — no Hub file URL",
                        StatusColor = hasMetadata
                            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00))
                            : new SolidColorBrush(Colors.Gray),
                        CanDownload = !string.IsNullOrEmpty(downloadUrl),
                        ButtonText = "Update",
                        HasUpdate = true,
                        IsInstalled = true,
                        NotOnHub = !hasMetadata
                    });
                }

                _updatesLoaded = true;
                SetWorkViewEmpty(UpdatesEmptyPanel, UpdatesEmptyText, false, null);
                SetUpdatesIdle($"{_updateItems.Count} update{(_updateItems.Count == 1 ? "" : "s")}");
            }
            catch (Exception ex)
            {
                SetUpdatesIdle($"Error: {ex.Message}");
            }
            finally
            {
                _isUpdatesCheckInProgress = false;
            }
        }

        private async Task LoadMissingViewAsync(bool force)
        {
            if (!force && _missingLoaded)
            {
                UpdateWorkViewStatus();
                return;
            }

            try
            {
                SetMissingBusy("Scanning for missing dependencies…");
                BuildLocalPackageLookups();
                _missingItems.Clear();
                _missingNotFoundCount = 0;

                if (_packageManager == null)
                {
                    _missingLoaded = true;
                    SetWorkViewEmpty(MissingEmptyPanel, MissingEmptyText, true, "Package manager not available. Scan packages in the main window first.");
                    SetMissingIdle("Ready");
                    return;
                }

                var missingDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in _packageManager.PackageMetadata)
                {
                    var metadata = kvp.Value;
                    if (metadata.MissingDependencies == null || metadata.MissingDependencies.Length == 0)
                        continue;
                    foreach (var dep in metadata.MissingDependencies)
                    {
                        if (!string.IsNullOrEmpty(dep))
                            missingDeps.Add(dep);
                    }
                }

                var trulyMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dep in missingDeps)
                {
                    var depClean = dep.Replace(".var", "");
                    if (_localPackageNames != null && _localPackageNames.Contains(depClean))
                        continue;

                    if (dep.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                    {
                        var baseName = dep.Substring(0, dep.Length - 7);
                        if (_localPackageVersions != null && _localPackageVersions.ContainsKey(baseName))
                            continue;
                    }
                    else
                    {
                        var lastDot = depClean.LastIndexOf('.');
                        if (lastDot > 0)
                        {
                            var baseName = depClean.Substring(0, lastDot);
                            if (_localPackageVersions != null && _localPackageVersions.ContainsKey(baseName))
                                continue;
                        }
                    }

                    trulyMissing.Add(dep);
                }

                if (trulyMissing.Count == 0)
                {
                    _missingLoaded = true;
                    SetMissingDepsExportList(Array.Empty<string>());
                    SetWorkViewEmpty(MissingEmptyPanel, MissingEmptyText, true, "No missing dependencies. All packages have what they need.");
                    SetMissingIdle("Ready");
                    return;
                }

                var missingDepsList = trulyMissing.ToList();
                SetMissingDepsExportList(missingDepsList);
                SetMissingBusy($"Searching Hub for {missingDepsList.Count} missing dependencies…");

                var hubPackages = await _hubService.FindPackagesAsync(missingDepsList);

                foreach (var dep in missingDepsList)
                {
                    if (hubPackages != null &&
                        hubPackages.TryGetValue(dep, out var hubPackage) &&
                        hubPackage != null &&
                        !hubPackage.NotOnHub)
                    {
                        var downloadUrl = !string.IsNullOrEmpty(hubPackage.LatestUrl)
                            ? hubPackage.LatestUrl
                            : hubPackage.DownloadUrl;
                        var filename = hubPackage.PackageName ?? $"{dep}.var";
                        if (string.IsNullOrEmpty(filename))
                            continue;

                        _missingItems.Add(new HubFileViewModel
                        {
                            Filename = filename,
                            PackageGroup = dep,
                            FileSize = hubPackage.FileSize,
                            DownloadUrl = downloadUrl,
                            LatestUrl = hubPackage.LatestUrl,
                            Status = "Missing",
                            StatusColor = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)),
                            CanDownload = !string.IsNullOrEmpty(downloadUrl),
                            ButtonText = "Download",
                            HasUpdate = false,
                            IsInstalled = false
                        });
                    }
                    else
                    {
                        _missingNotFoundCount++;
                        _missingItems.Add(new HubFileViewModel
                        {
                            Filename = dep.EndsWith(".var", StringComparison.OrdinalIgnoreCase) ? dep : dep + ".var",
                            PackageGroup = dep,
                            Status = "Not on Hub",
                            StatusColor = new SolidColorBrush(Colors.Gray),
                            CanDownload = false,
                            ButtonText = "",
                            NotOnHub = true,
                            IsInstalled = false
                        });
                    }
                }

                _missingLoaded = true;
                var downloadable = _missingItems.Count(i => i.CanDownload);
                SetWorkViewEmpty(MissingEmptyPanel, MissingEmptyText, _missingItems.Count == 0, "No missing dependencies found.");
                SetMissingIdle(downloadable > 0
                    ? $"{downloadable} on Hub, {_missingNotFoundCount} not found"
                    : $"{_missingNotFoundCount} missing — none on Hub");
            }
            catch (Exception ex)
            {
                SetMissingIdle($"Error: {ex.Message}");
            }
        }

        private void SetWorkViewEmpty(FrameworkElement panel, TextBlock textBlock, bool empty, string message)
        {
            SetVis(panel, empty);
            if (textBlock != null && !string.IsNullOrEmpty(message))
                textBlock.Text = message;
        }

        private void EchoStatusIfCurrent(HubWorkView view, string status)
        {
            if (_workView != view)
                return;
            try { StatusText.Text = status; } catch { }
        }

        private void SetUpdatesBusy(string status)
        {
            EchoStatusIfCurrent(HubWorkView.Updates, status);
            if (UpdatesBusyText != null)
                UpdatesBusyText.Text = status;
            if (UpdatesLoadingPanelText != null)
                UpdatesLoadingPanelText.Text = status;
            SetVis(UpdatesBusyCluster, true);
            SetVis(UpdatesLoadingPanel, true);
            SetVis(UpdatesEmptyPanel, false);
            SetUpdatesCommandsEnabled(false);
        }

        private void SetUpdatesIdle(string status)
        {
            EchoStatusIfCurrent(HubWorkView.Updates, status);
            SetVis(UpdatesBusyCluster, false);
            SetVis(UpdatesLoadingPanel, false);
            SetUpdatesCommandsEnabled(true);
        }

        private void SetMissingBusy(string status)
        {
            EchoStatusIfCurrent(HubWorkView.Missing, status);
            if (MissingBusyText != null)
                MissingBusyText.Text = status;
            if (MissingLoadingPanelText != null)
                MissingLoadingPanelText.Text = status;
            SetVis(MissingBusyCluster, true);
            SetVis(MissingLoadingPanel, true);
            SetVis(MissingEmptyPanel, false);
            SetMissingCommandsEnabled(false);
        }

        private void SetMissingIdle(string status)
        {
            EchoStatusIfCurrent(HubWorkView.Missing, status);
            SetVis(MissingBusyCluster, false);
            SetVis(MissingLoadingPanel, false);
            SetMissingCommandsEnabled(true);
        }

        private void SetUpdatesCommandsEnabled(bool enabled)
        {
            if (UpdatesRefreshButton != null)
                UpdatesRefreshButton.IsEnabled = enabled;
            if (UpdatesDownloadSelectedButton != null)
                UpdatesDownloadSelectedButton.IsEnabled = enabled;
            if (UpdatesDownloadAllButton != null)
                UpdatesDownloadAllButton.IsEnabled = enabled;
        }

        private void SetMissingCommandsEnabled(bool enabled)
        {
            if (MissingRefreshButton != null)
                MissingRefreshButton.IsEnabled = enabled;
            if (MissingDownloadSelectedButton != null)
                MissingDownloadSelectedButton.IsEnabled = enabled;
            if (MissingDownloadAllButton != null)
                MissingDownloadAllButton.IsEnabled = enabled;
        }

        private void UpdateWorkViewStatus()
        {
            if (_workView == HubWorkView.Updates)
                SetUpdatesIdle(_updateItems.Count == 0 ? "No updates available" : $"{_updateItems.Count} update{(_updateItems.Count == 1 ? "" : "s")}");
            else if (_workView == HubWorkView.Missing)
            {
                var downloadable = _missingItems.Count(i => i.CanDownload);
                SetMissingIdle(downloadable > 0
                    ? $"{downloadable} on Hub, {_missingNotFoundCount} not found"
                    : "No missing dependencies");
            }
        }

        private void QueueWorkItems(IEnumerable<HubFileViewModel> items)
        {
            var list = items?.Where(f => f != null && f.CanDownload && !string.IsNullOrEmpty(f.DownloadUrl)).ToList();
            if (list == null || list.Count == 0)
                return;

            _totalDownloadsInBatch = list.Count;
            _completedDownloadsInBatch = 0;
            _currentDownloadingPackage = "";

            foreach (var file in list)
                QueueFileForDownload(file);

            UpdateDownloadDock();
            try { StatusText.Text = $"Queued {list.Count} download{(list.Count == 1 ? "" : "s")}"; } catch { }
        }

        private void DownloadSelectedUpdates_Click(object sender, RoutedEventArgs e)
        {
            QueueWorkItems(UpdatesWorkGrid?.SelectedItems.OfType<HubFileViewModel>());
        }

        private void DownloadAllUpdates_Click(object sender, RoutedEventArgs e)
        {
            QueueWorkItems(_updateItems);
        }

        private void RefreshUpdates_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadUpdatesViewAsync(force: true);
        }

        private void DownloadSelectedMissing_Click(object sender, RoutedEventArgs e)
        {
            QueueWorkItems(MissingWorkGrid?.SelectedItems.OfType<HubFileViewModel>());
        }

        private void DownloadAllMissing_Click(object sender, RoutedEventArgs e)
        {
            QueueWorkItems(_missingItems);
        }

        private void RefreshMissing_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadMissingViewAsync(force: true);
        }

        private void WorkItemDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is HubFileViewModel file)
                QueueWorkItems(new[] { file });
        }

        private void RaiseLibraryRefreshNeeded()
        {
            try { LibraryRefreshNeeded?.Invoke(this, EventArgs.Empty); } catch { }
        }

        private void UpdateDownloadDock()
        {
            if (DownloadDock == null)
                return;

            var hasItems = _downloadQueue != null && _downloadQueue.Count > 0;
            DownloadDock.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;

            var activeCount = _downloadQueue?.Count(d => d.Status == DownloadStatus.Queued || d.Status == DownloadStatus.Downloading) ?? 0;
            if (DownloadDockCountText != null)
                DownloadDockCountText.Text = activeCount > 0
                    ? $"{activeCount} download{(activeCount == 1 ? "" : "s")}"
                    : "Downloads";

            if (DownloadDockBody != null)
                DownloadDockBody.Visibility = _downloadDockCollapsed ? Visibility.Collapsed : Visibility.Visible;

            if (DownloadDockToggle != null)
                DownloadDockToggle.Content = _downloadDockCollapsed ? "▲" : "▼";

            if (DownloadDockCancelAll != null)
                DownloadDockCancelAll.Visibility = _downloadQueue != null && _downloadQueue.Any(d => d.CanCancel)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (CancelAllDownloadsButton != null)
                CancelAllDownloadsButton.Visibility = DownloadDockCancelAll?.Visibility ?? Visibility.Collapsed;

            if (OpenDownloadingButton != null)
                OpenDownloadingButton.Visibility = _savedDownloadingDetails.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private void DownloadDockToggle_Click(object sender, RoutedEventArgs e)
        {
            _downloadDockCollapsed = !_downloadDockCollapsed;
            UpdateDownloadDock();
        }

        internal void HandleWorkViewKey(KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return;

            switch (e.Key)
            {
                case Key.D1:
                    SwitchWorkView(HubWorkView.Browse);
                    e.Handled = true;
                    break;
                case Key.D2:
                    SwitchWorkView(HubWorkView.Updates);
                    e.Handled = true;
                    break;
                case Key.D3:
                    SwitchWorkView(HubWorkView.Missing);
                    e.Handled = true;
                    break;
            }
        }
    }
}
