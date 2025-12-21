using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
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

        private CancellationTokenSource _searchCts;
        private readonly DispatcherTimer _searchDebounceTimer;
        private bool _suppressAutoSearch;
        private bool _suppressStateSave = true;

        // Pre-computed lookups for fast library status checking
        private HashSet<string> _localPackageNames;
        private Dictionary<string, int> _localPackageVersions;

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

        private string _sort = "Last Update";
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

            BuildLocalPackageLookups();
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
                Sort = SortOptions.Count > 0 ? SortOptions[0] : "Last Update";
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
                    Debug.WriteLine($"[HubBrowserViewModel] GetFilterOptions failed: {result.ErrorMessage} {result.Exception}");
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
                    SortOptions.Add("Last Update");
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
                Debug.WriteLine($"[HubBrowserViewModel] Error loading filter options: {ex}");
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
                Sort = string.IsNullOrWhiteSpace(Sort) ? "Last Update" : Sort,
                SortSecondary = string.IsNullOrWhiteSpace(SortSecondary) ? "None" : SortSecondary,
                OnlyDownloadable = OnlyDownloadable
            };
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
                var response = await _hubService.SearchResourcesAsync(searchParams, token);

                token.ThrowIfCancellationRequested();

                if (response?.IsSuccess == true)
                {
                    TotalResources = response.Pagination?.TotalFound ?? 0;
                    TotalPages = response.Pagination?.TotalPages ?? 1;

                    var list = response.Resources ?? new List<HubResource>();
                    var evaluated = await Task.Run(() =>
                    {
                        var results = new List<(HubResource Resource, bool InLibrary, bool UpdateAvailable)>(list.Count);
                        foreach (var resource in list)
                        {
                            token.ThrowIfCancellationRequested();
                            var (inLibrary, updateAvailable) = EvaluateLibraryStatus(resource);
                            results.Add((resource, inLibrary, updateAvailable));
                        }
                        return results;
                    }, token);

                    foreach (var item in evaluated)
                    {
                        item.Resource.InLibrary = item.InLibrary;
                        item.Resource.UpdateAvailable = item.UpdateAvailable;
                        item.Resource.UpdateMessage = item.UpdateAvailable ? "Update available" : null;
                    }

                    Results.Clear();
                    foreach (var r in list)
                        Results.Add(r);

                    HasEmptyResults = TotalResources == 0;
                    StatusText = "Ready";
                    _hasLoadedResultsOnce = true;

                    SaveState();
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
                IsLoading = false;
            }
        }

        private (bool InLibrary, bool UpdateAvailable) EvaluateLibraryStatus(HubResource resource)
        {
            if (resource?.HubFiles == null || resource.HubFiles.Count == 0)
                return (false, false);

            var inLibrary = false;
            var updateAvailable = false;

            foreach (var file in resource.HubFiles)
            {
                var packageName = file?.PackageName;
                if (string.IsNullOrEmpty(packageName))
                    continue;

                var cleanName = packageName.Replace(".var", "", StringComparison.OrdinalIgnoreCase);
                if (_localPackageNames != null && _localPackageNames.Contains(cleanName))
                {
                    inLibrary = true;
                }
                else
                {
                    var groupName = GetPackageGroupName(cleanName);
                    if (_localPackageVersions != null && _localPackageVersions.ContainsKey(groupName))
                        inLibrary = true;
                }

                var pkgGroupName = GetPackageGroupName(cleanName);
                if (!updateAvailable && _localPackageVersions != null &&
                    _localPackageVersions.TryGetValue(pkgGroupName, out var localVersion) && localVersion > 0 &&
                    _hubService.HasUpdate(pkgGroupName, localVersion))
                {
                    updateAvailable = true;
                }

                if (inLibrary && updateAvailable)
                    break;
            }

            return (inLibrary, updateAvailable);
        }

        private void BuildLocalPackageLookups()
        {
            _localPackageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _localPackageVersions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var pkg in _localPackagePaths.Keys)
            {
                var name = (pkg ?? string.Empty).Replace(".var", "");
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
                _settingsManager.UpdateSetting("HubBrowserSort", Sort ?? "Last Update");
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
                Sort = _settingsManager.GetSetting("HubBrowserSort", "Last Update") ?? "Last Update";
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
