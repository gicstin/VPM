using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using VPM.Models;
using VPM.Services;

namespace VPM.Windows
{
    public sealed class HubBrowserViewModel : ViewModelBase, IDisposable
    {
        private readonly HubService _hubService;
        private readonly SettingsManager _settingsManager;
        private readonly Dictionary<string, string> _localPackagePaths;
        private readonly Dispatcher _uiDispatcher;

        private CancellationTokenSource _searchCts;
        private readonly DispatcherTimer _searchDebounceTimer;
        private bool _suppressAutoSearch;
        private bool _suppressStateSave = true;

        // Pre-computed lookups for fast library status checking
        private HashSet<string> _localPackageNames;
        private Dictionary<string, int> _localPackageVersions;

        private readonly ConcurrentDictionary<string, HubResourceDetail> _resourceDetailCache = new(StringComparer.OrdinalIgnoreCase);

        private readonly object _searchDedupLock = new object();
        private string _activeSearchKey;
        private string _lastCompletedSearchKey;
        private DateTime _lastCompletedSearchUtc = DateTime.MinValue;

        public ObservableCollection<HubResource> Results { get; } = new ObservableCollection<HubResource>();

        public ObservableCollection<string> ScopeOptions { get; } = new ObservableCollection<string>
        {
            "Hub And Dependencies",
            "Hub Only",
            "All"
        };

        public ObservableCollection<string> PayTypeOptions { get; } = new ObservableCollection<string>
        {
            "All",
            "Free",
            "Paid"
        };
        public ObservableCollection<string> Categories { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> SortOptions { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> SortSecondaryOptions { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> Creators { get; } = new ObservableCollection<string>();

        private HubResource _selectedResource;
        public HubResource SelectedResource
        {
            get => _selectedResource;
            set => SetProperty(ref _selectedResource, value);
        }

        private string _statusText = "Ready";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (SetProperty(ref _isLoading, value))
                {
                    OnPropertyChanged(nameof(IsNotLoading));
                    OnPropertyChanged(nameof(LoadingVisibility));
                    _searchCommand?.RaiseCanExecuteChanged();
                    _refreshCommand?.RaiseCanExecuteChanged();
                    _nextPageCommand?.RaiseCanExecuteChanged();
                    _prevPageCommand?.RaiseCanExecuteChanged();
                    _clearAllFiltersCommand?.RaiseCanExecuteChanged();
                    _clearSearchCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        public async Task RefreshLibraryStatusesAsync(CancellationToken cancellationToken = default)
        {
            BuildLocalPackageLookups();

            if (Results == null || Results.Count == 0)
                return;

            foreach (var resource in Results)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (inLibrary, updateAvailable) = await EvaluateLibraryStatusAsync(resource, cancellationToken);
                resource.InLibrary = inLibrary;
                resource.UpdateAvailable = updateAvailable;
                resource.UpdateMessage = updateAvailable ? "Update available" : null;
            }
        }

        public bool IsNotLoading => !IsLoading;

        public System.Windows.Visibility LoadingVisibility => IsLoading ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        private bool _hasLoadedResultsOnce;

        private string _errorText;
        public string ErrorText
        {
            get => _errorText;
            set
            {
                if (SetProperty(ref _errorText, value))
                {
                    OnPropertyChanged(nameof(HasError));
                    OnPropertyChanged(nameof(ErrorVisibility));
                }
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

        public System.Windows.Visibility ErrorVisibility => HasError ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        private bool _hasEmptyResults;
        public bool HasEmptyResults
        {
            get => _hasEmptyResults;
            set
            {
                if (SetProperty(ref _hasEmptyResults, value))
                    OnPropertyChanged(nameof(EmptyResultsVisibility));
            }
        }

        public System.Windows.Visibility EmptyResultsVisibility => HasEmptyResults ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        private int _currentPage = 1;
        public int CurrentPage
        {
            get => _currentPage;
            set
            {
                if (SetProperty(ref _currentPage, value))
                {
                    OnPropertyChanged(nameof(PageDisplay));
                    _nextPageCommand?.RaiseCanExecuteChanged();
                    _prevPageCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        private int _totalPages = 1;
        public int TotalPages
        {
            get => _totalPages;
            set
            {
                if (SetProperty(ref _totalPages, value))
                {
                    OnPropertyChanged(nameof(PageDisplay));
                    _nextPageCommand?.RaiseCanExecuteChanged();
                    _prevPageCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        private int _totalResources;
        public int TotalResources
        {
            get => _totalResources;
            set
            {
                if (SetProperty(ref _totalResources, value))
                    OnPropertyChanged(nameof(TotalCountText));
            }
        }

        public string PageDisplay => $"{CurrentPage} / {TotalPages}";
        public string TotalCountText => $"Total: {TotalResources}";

        // Filters / query
        private string _searchText;
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    OnPropertyChanged(nameof(HasSearchText));
                    _clearSearchCommand?.RaiseCanExecuteChanged();
                    SaveState();
                    if (!_suppressAutoSearch)
                        DebounceSearch();
                }
            }
        }

        public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

        public System.Windows.Visibility ClearSearchVisibility => HasSearchText ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        private string _scope = "All";
        public string Scope
        {
            get => _scope;
            set
            {
                if (SetProperty(ref _scope, value))
                {
                    SaveState();
                    if (!_suppressAutoSearch)
                        _ = SearchAsync(explicitSearch: true);
                }
            }
        }

        private string _payType = "All";
        public string PayType
        {
            get => _payType;
            set
            {
                if (SetProperty(ref _payType, value))
                {
                    SaveState();
                    try { _settingsManager.SaveSettingsImmediate(); } catch { }
                    try { _ = _settingsManager.SaveSettingsAsync(); } catch { }
                    if (!_suppressAutoSearch)
                        _ = SearchAsync(explicitSearch: true);
                }
            }
        }

        private string _category = "All";
        public string Category
        {
            get => _category;
            set
            {
                if (SetProperty(ref _category, value))
                {
                    SaveState();
                    if (!_suppressAutoSearch)
                        _ = SearchAsync(explicitSearch: true);
                }
            }
        }

        private string _creator = "All";
        public string Creator
        {
            get => _creator;
            set
            {
                if (SetProperty(ref _creator, value))
                {
                    SaveState();
                    if (!_suppressAutoSearch)
                        _ = SearchAsync(explicitSearch: true);
                }
            }
        }

        private string _sort = "Latest Update";
        public string Sort
        {
            get => _sort;
            set
            {
                if (SetProperty(ref _sort, value))
                {
                    SaveState();
                    if (!_suppressAutoSearch)
                        _ = SearchAsync(explicitSearch: true);
                }
            }
        }

        private string _sortSecondary = "None";
        public string SortSecondary
        {
            get => _sortSecondary;
            set
            {
                if (SetProperty(ref _sortSecondary, value))
                {
                    SaveState();
                    if (!_suppressAutoSearch)
                        _ = SearchAsync(explicitSearch: true);
                }
            }
        }

        private bool _onlyDownloadable;
        public bool OnlyDownloadable
        {
            get => _onlyDownloadable;
            set
            {
                if (SetProperty(ref _onlyDownloadable, value))
                {
                    SaveState();
                    if (!_suppressAutoSearch)
                        _ = SearchAsync(explicitSearch: true);
                }
            }
        }

        private string _tags = "All";
        public string Tags
        {
            get => _tags;
            set
            {
                SetProperty(ref _tags, value ?? "All");
            }
        }

        // Commands
        private readonly RelayCommand _searchCommand;
        public RelayCommand SearchCommand => _searchCommand;

        private readonly RelayCommand _refreshCommand;
        public RelayCommand RefreshCommand => _refreshCommand;

        private readonly RelayCommand _nextPageCommand;
        public RelayCommand NextPageCommand => _nextPageCommand;

        private readonly RelayCommand _prevPageCommand;
        public RelayCommand PrevPageCommand => _prevPageCommand;

        private readonly RelayCommand _clearAllFiltersCommand;
        public RelayCommand ClearAllFiltersCommand => _clearAllFiltersCommand;

        private readonly RelayCommand _clearSearchCommand;
        public RelayCommand ClearSearchCommand => _clearSearchCommand;

        public HubBrowserViewModel(HubService hubService, SettingsManager settingsManager, Dictionary<string, string> localPackagePaths)
        {
            _hubService = hubService ?? throw new ArgumentNullException(nameof(hubService));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _localPackagePaths = localPackagePaths ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            _uiDispatcher = Dispatcher.CurrentDispatcher;

            _hubService.StatusChanged += (s, status) => StatusText = status;

            _searchCommand = new RelayCommand(() => _ = SearchAsync(explicitSearch: true), () => !IsLoading);
            _refreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsLoading);
            _nextPageCommand = new RelayCommand(() => { if (CurrentPage < TotalPages) { CurrentPage++; _ = SearchAsync(explicitSearch: true); } }, () => !IsLoading && CurrentPage < TotalPages);
            _prevPageCommand = new RelayCommand(() => { if (CurrentPage > 1) { CurrentPage--; _ = SearchAsync(explicitSearch: true); } }, () => !IsLoading && CurrentPage > 1);
            _clearAllFiltersCommand = new RelayCommand(() => _ = ClearAllFiltersAsync(), () => !IsLoading);
            _clearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => !IsLoading && HasSearchText);

            _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _searchDebounceTimer.Tick += (s, e) =>
            {
                _searchDebounceTimer.Stop();
                CurrentPage = 1;
                _ = SearchAsync(explicitSearch: false);
            };
        }

        public async Task InitializeAsync()
        {
            IsLoading = true;
            ErrorText = null;
            try
            {
                _suppressStateSave = true;

                var packagesTask = _hubService.LoadPackagesJsonAsync();
                var filterTask = LoadFilterOptionsAsync();
                await Task.WhenAll(packagesTask, filterTask);

                LoadCreatorListFromPackages();
                RestoreState();

                CurrentPage = Math.Max(1, CurrentPage);
                await SearchAsync(explicitSearch: true);
            }
            finally
            {
                _suppressStateSave = false;
                IsLoading = false;
            }
        }

        private void DebounceSearch()
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private async Task RefreshAsync()
        {
            _hubService.ClearSearchCache();
            await _hubService.LoadPackagesJsonAsync(forceRefresh: true);
            LoadCreatorListFromPackages();
            CurrentPage = 1;
            await SearchAsync(explicitSearch: true);
        }

        private async Task ClearAllFiltersAsync()
        {
            try
            {
                _suppressAutoSearch = true;

                SearchText = string.Empty;
                Scope = "All";
                Category = "All";
                PayType = "All";
                Sort = SortOptions.Count > 0 ? SortOptions[0] : "Latest Update";
                SortSecondary = "None";
                Creator = "All";
                OnlyDownloadable = false;
            }
            finally
            {
                _suppressAutoSearch = false;
            }

            CurrentPage = 1;
            await SearchAsync(explicitSearch: true);
        }

        private async Task LoadFilterOptionsAsync()
        {
            try
            {
                StatusText = "Loading filter options...";
                var result = await _hubService.GetFilterOptionsResultAsync();

                if (!result.Success)
                {
                    StatusText = result.ErrorMessage ?? "Failed to load filter options.";
                }

                var options = result.Success ? result.Value : await _hubService.GetFilterOptionsAsync();

                Categories.Clear();
                Categories.Add("All");
                if (options?.Types != null)
                {
                    foreach (var t in options.Types)
                        Categories.Add(t);
                }

                SortOptions.Clear();
                if (options?.SortOptions != null && options.SortOptions.Count > 0)
                {
                    foreach (var s in options.SortOptions)
                        SortOptions.Add(s);
                }
                else
                {
                    SortOptions.Add("Latest Update");
                }

                SortSecondaryOptions.Clear();
                SortSecondaryOptions.Add("None");
                foreach (var s in SortOptions)
                    SortSecondaryOptions.Add(s);

                if (result.Success)
                    StatusText = "Ready";
            }
            catch (Exception ex)
            {
                StatusText = $"Error loading filter options: {ex.Message}";
            }
        }

        private void LoadCreatorListFromPackages()
        {
            var creators = _hubService.GetAllCreators();
            Creators.Clear();
            Creators.Add("All");
            foreach (var c in creators)
                Creators.Add(c);
        }

        private HubSearchParams BuildSearchParams()
        {
            return new HubSearchParams
            {
                Page = CurrentPage,
                PerPage = 48,
                Search = SearchText?.Trim(),
                Location = string.IsNullOrWhiteSpace(Scope) ? "All" : Scope,
                Category = string.IsNullOrWhiteSpace(Category) ? "All" : Category,
                Creator = string.IsNullOrWhiteSpace(Creator) ? "All" : Creator,
                PayType = string.IsNullOrWhiteSpace(PayType) ? "All" : PayType,
                Tags = string.IsNullOrWhiteSpace(Tags) ? "All" : Tags,
                Sort = string.IsNullOrWhiteSpace(Sort) ? "Latest Update" : Sort,
                SortSecondary = string.IsNullOrWhiteSpace(SortSecondary) ? "None" : SortSecondary,
                OnlyDownloadable = OnlyDownloadable
            };
        }

        private static string BuildSearchKey(HubSearchParams p)
        {
            return $"{p?.Page}|{p?.PerPage}|{p?.Location}|{p?.Search}|{p?.PayType}|{p?.Category}|{p?.Creator}|{p?.Tags}|{p?.Sort}|{p?.SortSecondary}|{p?.OnlyDownloadable}";
        }

        public async Task SearchAsync(bool explicitSearch)
        {
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            try
            {
                ErrorText = null;
                HasEmptyResults = false;

                var showOverlay = !_hasLoadedResultsOnce || HasError;
                IsLoading = explicitSearch && showOverlay;
                StatusText = explicitSearch && showOverlay ? "Loading..." : "Updating...";

                var searchParams = BuildSearchParams();

                var searchKey = BuildSearchKey(searchParams);
                lock (_searchDedupLock)
                {
                    // If the same search is already running, don't start it again.
                    if (!string.IsNullOrEmpty(_activeSearchKey) && string.Equals(_activeSearchKey, searchKey, StringComparison.Ordinal))
                    {
                        return;
                    }

                    // If the same search just completed very recently, skip accidental double-invocation.
                    if (!string.IsNullOrEmpty(_lastCompletedSearchKey) && string.Equals(_lastCompletedSearchKey, searchKey, StringComparison.Ordinal) &&
                        (DateTime.UtcNow - _lastCompletedSearchUtc) < TimeSpan.FromSeconds(2))
                    {
                        return;
                    }

                    _activeSearchKey = searchKey;
                }

                var response = await _hubService.SearchResourcesAsync(searchParams, token);

                token.ThrowIfCancellationRequested();

                if (response?.IsSuccess == true)
                {
                    TotalResources = response.Pagination?.TotalFound ?? 0;
                    TotalPages = response.Pagination?.TotalPages ?? 1;

                    var list = response.Resources ?? new List<HubResource>();
                    Results.Clear();
                    foreach (var r in list)
                        Results.Add(r);

                    _hasLoadedResultsOnce = true;

                    // Don't block the page render on expensive detail/status evaluation.
                    // Compute flags in the background and apply them progressively.
                    _ = EvaluateStatusesAsync(list, token);

                    HasEmptyResults = TotalResources == 0;
                }
                else
                {
                    var error = response?.Error ?? "Unknown error";
                    ErrorText = error;
                    StatusText = $"Error: {error}";
                }
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (Exception ex)
            {
                ErrorText = ex.Message;
                StatusText = $"Error: {ex.Message}";
            }
            finally
            {
                lock (_searchDedupLock)
                {
                    _lastCompletedSearchKey = _activeSearchKey;
                    _lastCompletedSearchUtc = DateTime.UtcNow;
                    _activeSearchKey = null;
                }
                IsLoading = false;
            }
        }

        private async Task EvaluateStatusesAsync(IReadOnlyList<HubResource> resources, CancellationToken token)
        {
            if (resources == null || resources.Count == 0)
                return;

            var maxConcurrency = 6;
            try
            {
                using (var gate = new SemaphoreSlim(maxConcurrency))
                {
                    var tasks = new List<Task>(resources.Count);
                    foreach (var resource in resources)
                    {
                        tasks.Add(Task.Run(async () =>
                        {
                            await gate.WaitAsync(token).ConfigureAwait(false);
                            try
                            {
                                token.ThrowIfCancellationRequested();
                                var (inLibrary, updateAvailable) = await EvaluateLibraryStatusAsync(resource, token).ConfigureAwait(false);

                                // Apply UI-bound updates on the dispatcher thread
                                _uiDispatcher.BeginInvoke(new Action(() =>
                                {
                                    resource.InLibrary = inLibrary;
                                    resource.UpdateAvailable = updateAvailable;
                                    resource.UpdateMessage = updateAvailable ? "Update available" : null;
                                }), DispatcherPriority.Background);
                            }
                            finally
                            {
                                gate.Release();
                            }
                        }, token));
                    }

                    await Task.WhenAll(tasks);
                }
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (Exception ex)
            {
            }
        }

        private async Task<(bool InLibrary, bool UpdateAvailable)> EvaluateLibraryStatusAsync(HubResource resource, CancellationToken cancellationToken)
        {
            if (resource?.HubFiles == null || resource.HubFiles.Count == 0)
                return (false, false);

            HubResourceDetail detail = resource as HubResourceDetail;
            if (detail == null && resource.DependencyCount > 0 && !string.IsNullOrEmpty(resource.ResourceId))
            {
                try
                {
                    if (_resourceDetailCache.TryGetValue(resource.ResourceId, out var cached) && cached != null)
                    {
                        detail = cached;
                    }
                    else
                    {
                        detail = await _hubService.GetResourceDetailAsync(resource.ResourceId, isPackageName: false, cancellationToken).ConfigureAwait(false);
                        if (detail != null)
                        {
                            _resourceDetailCache[resource.ResourceId] = detail;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    detail = null;
                }
            }

            // Treat the resource as a collection (main + dependencies). If the Hub says there are dependencies,
            // we must be able to enumerate them; otherwise we cannot claim the collection is complete.
            if (resource.DependencyCount > 0 && (detail?.Dependencies == null || detail.Dependencies.Count == 0))
            {
                var updateFromMainOnly = false;
                foreach (var hubFile in resource.HubFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(hubFile?.Filename))
                        continue;

                    var cleanName = hubFile.PackageName.Replace(".var", "", StringComparison.OrdinalIgnoreCase);
                    var pkgGroupName = GetPackageGroupName(cleanName);
                    if (_localPackageVersions != null && _localPackageVersions.TryGetValue(pkgGroupName, out var localVersion) && localVersion > 0)
                    {
                        if (_hubService.HasUpdate(pkgGroupName, localVersion))
                        {
                            updateFromMainOnly = true;
                            break;
                        }
                    }
                }
                return (false, updateFromMainOnly);
            }

            var requiredFiles = new List<HubFile>();
            if (detail?.HubFiles != null)
                requiredFiles.AddRange(detail.HubFiles);
            else
                requiredFiles.AddRange(resource.HubFiles);

            if (detail?.Dependencies != null)
            {
                foreach (var depGroup in detail.Dependencies.Values)
                {
                    if (depGroup == null)
                        continue;
                    requiredFiles.AddRange(depGroup);
                }
            }

            if (requiredFiles.Count == 0)
                return (false, false);

            var allInLibrary = true;
            var updateAvailable = false;

            foreach (var file in requiredFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (file == null || string.IsNullOrEmpty(file.Filename))
                {
                    allInLibrary = false;
                    continue;
                }

                var packageName = file.PackageName;
                if (string.IsNullOrEmpty(packageName))
                {
                    allInLibrary = false;
                    continue;
                }

                var cleanName = packageName.Replace(".var", "", StringComparison.OrdinalIgnoreCase);
                var pkgGroupName = GetPackageGroupName(cleanName);
                var hubVersion = GetHubFileVersion(file);

                var hasLocal = false;
                var localVersion = -1;

                if (hubVersion > 0)
                {
                    // Versioned requirement: accept any local version in the group as long as it's >= required.
                    if (_localPackageVersions != null && _localPackageVersions.TryGetValue(pkgGroupName, out var lv) && lv > 0)
                    {
                        localVersion = lv;
                        hasLocal = localVersion >= hubVersion;
                    }
                }
                else
                {
                    // Unversioned / non-numeric requirement (commonly .latest): require exact package presence.
                    if (_localPackageNames != null && _localPackageNames.Contains(cleanName))
                        hasLocal = true;

                    // Still capture a local version (if any) for update detection, but it should not satisfy the requirement.
                    if (_localPackageVersions != null && _localPackageVersions.TryGetValue(pkgGroupName, out var lv) && lv > 0)
                        localVersion = lv;
                }

                if (!hasLocal)
                {
                    allInLibrary = false;
                }
                else if (!updateAvailable && localVersion > 0)
                {
                    if ((hubVersion > 0 && hubVersion > localVersion) || _hubService.HasUpdate(pkgGroupName, localVersion))
                        updateAvailable = true;
                }
            }
            return (allInLibrary, updateAvailable);
        }

        private static int GetHubFileVersion(HubFile file)
        {
            if (file == null)
                return -1;

            if (!string.IsNullOrEmpty(file.LatestVersion) && int.TryParse(file.LatestVersion, out var parsedLatest))
                return parsedLatest;

            if (!string.IsNullOrEmpty(file.Version) && int.TryParse(file.Version, out var parsedVersion))
                return parsedVersion;

            if (string.IsNullOrEmpty(file.Filename))
                return -1;

            var name = file.Filename;
            if (name.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            var lastDot = name.LastIndexOf('.');
            if (lastDot > 0 && lastDot < name.Length - 1)
            {
                var versionPart = name.Substring(lastDot + 1);
                if (int.TryParse(versionPart, out var version))
                    return version;
            }

            return -1;
        }

        private void BuildLocalPackageLookups()
        {
            _localPackageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _localPackageVersions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in _localPackagePaths)
            {
                var pkg = kvp.Key;
                var path = kvp.Value;

                if (string.IsNullOrWhiteSpace(pkg) || string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                    continue;

                var name = pkg.Replace(".var", "");
                if (string.IsNullOrEmpty(name))
                    continue;

                _localPackageNames.Add(name);

                var groupName = GetPackageGroupName(name);
                var lastDot = name.LastIndexOf('.');
                if (lastDot > 0)
                {
                    var versionPart = name.Substring(lastDot + 1);
                    if (int.TryParse(versionPart, out var version))
                    {
                        if (!_localPackageVersions.TryGetValue(groupName, out var existing) || version > existing)
                            _localPackageVersions[groupName] = version;
                    }
                }
            }
        }

        private static string GetPackageGroupName(string packageName)
        {
            var name = packageName ?? string.Empty;

            if (name.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            if (name.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 7);

            var lastDot = name.LastIndexOf('.');
            if (lastDot > 0)
            {
                var afterDot = name.Substring(lastDot + 1);
                if (int.TryParse(afterDot, out _))
                    return name.Substring(0, lastDot);
            }

            return name;
        }

        private void SaveState()
        {
            try
            {
                if (_suppressStateSave)
                    return;

                _settingsManager.UpdateSetting("HubBrowserSearchText", SearchText?.Trim() ?? "");
                _settingsManager.UpdateSetting("HubBrowserSource", Scope ?? "All");
                _settingsManager.UpdateSetting("HubBrowserCategory", Category ?? "All");
                if (_settingsManager?.Settings != null)
                    _settingsManager.Settings.HubBrowserPayType = PayType ?? "All";
                _settingsManager.UpdateSetting("HubBrowserSort", Sort ?? "Latest Update");
                _settingsManager.UpdateSetting("HubBrowserSortSecondary", SortSecondary ?? "None");
                _settingsManager.UpdateSetting("HubBrowserCreator", Creator ?? "All");
            }
            catch (Exception)
            {
            }
        }

        private void RestoreState()
        {
            try
            {
                _suppressAutoSearch = true;

                SearchText = _settingsManager.GetSetting("HubBrowserSearchText", "") ?? "";
                Scope = _settingsManager.GetSetting("HubBrowserSource", "All") ?? "All";
                Category = _settingsManager.GetSetting("HubBrowserCategory", "All") ?? "All";
                var savedPayType = _settingsManager?.Settings?.HubBrowserPayType ?? "All";
                PayType = savedPayType;
                Sort = _settingsManager.GetSetting("HubBrowserSort", SortOptions.Count > 0 ? SortOptions[0] : "Latest Update") ?? (SortOptions.Count > 0 ? SortOptions[0] : "Latest Update");
                SortSecondary = _settingsManager.GetSetting("HubBrowserSortSecondary", "None") ?? "None";
                Creator = _settingsManager.GetSetting("HubBrowserCreator", "All") ?? "All";
            }
            catch (Exception)
            {
            }
            finally
            {
                _suppressAutoSearch = false;
            }
        }

        public void Dispose()
        {
            try { _searchCts?.Cancel(); } catch { }
            try { _searchCts?.Dispose(); } catch { }
        }
    }
}
