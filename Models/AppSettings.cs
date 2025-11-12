using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VPM.Models
{
    /// <summary>
    /// Application settings model that supports property change notifications for automatic saving
    /// </summary>
    public class AppSettings : INotifyPropertyChanged
    {
        // First Launch Settings
        private bool _isFirstLaunch = true;
        
        // UI Settings
        private string _theme = "System";
        private int _imageColumns = 3;
        private string _selectedFolder = "";
        private string _cacheFolder = "";
        private bool _cascadeFiltering = true;
        
        // Window Settings
        private double _windowWidth = 1200;
        private double _windowHeight = 800;
        private double _windowLeft = 100;
        private double _windowTop = 100;
        private bool _windowMaximized = false;
        
        // Panel Settings
        private double _leftPanelWidth = 0.75;
        private double _centerPanelWidth = 1.52;
        private double _rightPanelWidth = 1.0;
        private double _imagesPanelWidth = 1.78;
        private double _depsInfoSplitterHeight = 1.618; // Height of dependencies section (before splitter)
        
        // Display Settings
        private bool _showPackageInfo = true;
        private bool _showDependencies = true;
        private bool _groupImagesByPackage = true;
        
        // Filter Settings
        private List<string> _selectedStatusFilters = new List<string>();
        private List<string> _selectedOptimizationFilters = new List<string>();
        private List<string> _selectedCreatorFilters = new List<string>();
        private List<string> _selectedContentTypeFilters = new List<string>();
        private string _packageSearchText = "";
        private string _dependencySearchText = "";
        private string _creatorFilterText = "";
        private string _contentTypeFilterText = "";
        private bool _hideArchivedPackages = true;
        
        // Filter List Heights
        private double _dateFilterHeight = 100;
        private double _statusFilterHeight = 120;
        private double _optimizationFilterHeight = 80;
        private double _contentTypesFilterHeight = 180;
        private double _creatorsFilterHeight = 180;
        private double _licenseTypeFilterHeight = 120;
        private double _fileSizeFilterHeight = 120;
        private double _subfoldersFilterHeight = 120;
        private double _damagedFilterHeight = 80;
        
        // Filter Section Visibility
        private bool _dateFilterVisible = true;
        private bool _statusFilterVisible = true;
        private bool _optimizationFilterVisible = true;
        private bool _contentTypesFilterVisible = true;
        private bool _creatorsFilterVisible = true;
        private bool _licenseTypeFilterVisible = true;
        private bool _fileSizeFilterVisible = true;
        private bool _subfoldersFilterVisible = true;
        private bool _damagedFilterVisible = true;
        private bool _sceneTypeFilterVisible = true;
        private bool _sceneCreatorFilterVisible = true;
        private bool _sceneSourceFilterVisible = true;
        private bool _presetCategoryFilterVisible = true;
        private bool _presetSubfolderFilterVisible = true;
        private bool _sceneDateFilterVisible = true;
        private bool _sceneFileSizeFilterVisible = true;
        private bool _presetDateFilterVisible = true;
        private bool _presetFileSizeFilterVisible = true;
        private bool _sceneStatusFilterVisible = true;
        private bool _presetStatusFilterVisible = true;
        
        // File Size Filter Settings (in MB)
        private double _fileSizeTinyMax = 1;
        private double _fileSizeSmallMax = 10;
        private double _fileSizeMediumMax = 100;
        
        // Performance Settings
        private bool _enableVirtualization = true;
        private int _cacheLength = 2;
        private bool _enableImageCaching = true;
        private int _imageCacheSize = 500;
        private int _maxSafeSelection = 200;
        
        // Texture Validation Settings
        private bool _useThoroughTextureScan = false;
        
        // Package Downloader Settings
        private bool _enableAutoDownload = false;
        
        // Network Permission Settings
        private bool _networkPermissionGranted = false;
        private bool _networkPermissionAsked = false;
        private bool _neverShowNetworkPermissionDialog = false;
        
        // Package Optimizer Settings
        private bool _forceLatestDependencies = true;
        private bool _disableMorphPreload = true;
        private bool _minifyJsonFiles = true;
        
        // Filter Position Settings
        private List<string> _packageFilterOrder = new List<string> { "DateFilter", "StatusFilter", "ContentTypesFilter", "CreatorsFilter", "LicenseTypeFilter", "FileSizeFilter", "SubfoldersFilter", "DamagedFilter" };
        private List<string> _sceneFilterOrder = new List<string> { "SceneTypeFilter", "SceneCreatorFilter", "SceneSourceFilter", "SceneDateFilter", "SceneFileSizeFilter", "SceneStatusFilter" };
        private List<string> _presetFilterOrder = new List<string> { "PresetCategoryFilter", "PresetSubfolderFilter", "PresetDateFilter", "PresetFileSizeFilter", "PresetStatusFilter" };

        // Sorting Settings
        private Dictionary<string, SerializableSortingState> _sortingStates = new Dictionary<string, SerializableSortingState>();

        public event PropertyChangedEventHandler PropertyChanged;

        // First Launch Settings Properties
        public bool IsFirstLaunch
        {
            get => _isFirstLaunch;
            set => SetProperty(ref _isFirstLaunch, value);
        }

        // UI Settings Properties
        public string Theme
        {
            get => _theme;
            set => SetProperty(ref _theme, value);
        }

        public int ImageColumns
        {
            get => _imageColumns;
            set => SetProperty(ref _imageColumns, Math.Max(1, Math.Min(12, value)));
        }

        public string SelectedFolder
        {
            get => _selectedFolder;
            set => SetProperty(ref _selectedFolder, value ?? "");
        }

        public string CacheFolder
        {
            get => _cacheFolder;
            set => SetProperty(ref _cacheFolder, value ?? "");
        }

        public bool CascadeFiltering
        {
            get => _cascadeFiltering;
            set => SetProperty(ref _cascadeFiltering, value);
        }


        // Window Settings Properties
        public double WindowWidth
        {
            get => _windowWidth;
            set => SetProperty(ref _windowWidth, Math.Max(800, value));
        }

        public double WindowHeight
        {
            get => _windowHeight;
            set => SetProperty(ref _windowHeight, Math.Max(600, value));
        }

        public double WindowLeft
        {
            get => _windowLeft;
            set => SetProperty(ref _windowLeft, value);
        }

        public double WindowTop
        {
            get => _windowTop;
            set => SetProperty(ref _windowTop, value);
        }

        public bool WindowMaximized
        {
            get => _windowMaximized;
            set => SetProperty(ref _windowMaximized, value);
        }

        // Panel Settings Properties
        public double LeftPanelWidth
        {
            get => _leftPanelWidth;
            set => SetProperty(ref _leftPanelWidth, Math.Max(0.1, value));
        }

        public double CenterPanelWidth
        {
            get => _centerPanelWidth;
            set => SetProperty(ref _centerPanelWidth, Math.Max(0.1, value));
        }

        public double RightPanelWidth
        {
            get => _rightPanelWidth;
            set => SetProperty(ref _rightPanelWidth, Math.Max(0.1, value));
        }

        public double ImagesPanelWidth
        {
            get => _imagesPanelWidth;
            set => SetProperty(ref _imagesPanelWidth, Math.Max(0, value));
        }

        public double DepsInfoSplitterHeight
        {
            get => _depsInfoSplitterHeight;
            set => SetProperty(ref _depsInfoSplitterHeight, Math.Max(0.1, value));
        }

        // Display Settings Properties
        public bool ShowPackageInfo
        {
            get => _showPackageInfo;
            set => SetProperty(ref _showPackageInfo, value);
        }

        public bool ShowDependencies
        {
            get => _showDependencies;
            set => SetProperty(ref _showDependencies, value);
        }

        public bool GroupImagesByPackage
        {
            get => _groupImagesByPackage;
            set => SetProperty(ref _groupImagesByPackage, value);
        }

        /// <summary>
        /// Backward compatibility property for old settings files.
        /// Silently ignores the value since virtualization handles all images.
        /// </summary>
        public int MaxImagesPerPackage
        {
            get => 100; // Return dummy value, not used anymore
            set { } // Silently ignore - for backward compatibility only
        }

        /// <summary>
        /// Backward compatibility property for old settings files.
        /// Silently ignores the value since virtualization handles all images.
        /// </summary>
        public int MaxTotalImages
        {
            get => 1000; // Return dummy value, not used anymore
            set { } // Silently ignore - for backward compatibility only
        }

        // Filter Settings Properties
        public List<string> SelectedStatusFilters
        {
            get => _selectedStatusFilters;
            set => SetProperty(ref _selectedStatusFilters, value ?? new List<string>());
        }

        public List<string> SelectedOptimizationFilters
        {
            get => _selectedOptimizationFilters;
            set => SetProperty(ref _selectedOptimizationFilters, value ?? new List<string>());
        }

        public List<string> SelectedCreatorFilters
        {
            get => _selectedCreatorFilters;
            set => SetProperty(ref _selectedCreatorFilters, value ?? new List<string>());
        }

        public List<string> SelectedContentTypeFilters
        {
            get => _selectedContentTypeFilters;
            set => SetProperty(ref _selectedContentTypeFilters, value ?? new List<string>());
        }

        public string PackageSearchText
        {
            get => _packageSearchText;
            set => SetProperty(ref _packageSearchText, value ?? "");
        }

        public string DependencySearchText
        {
            get => _dependencySearchText;
            set => SetProperty(ref _dependencySearchText, value ?? "");
        }

        public string CreatorFilterText
        {
            get => _creatorFilterText;
            set => SetProperty(ref _creatorFilterText, value ?? "");
        }

        public string ContentTypeFilterText
        {
            get => _contentTypeFilterText;
            set => SetProperty(ref _contentTypeFilterText, value ?? "");
        }

        public bool HideArchivedPackages
        {
            get => _hideArchivedPackages;
            set => SetProperty(ref _hideArchivedPackages, value);
        }

        // Filter List Height Properties
        public double DateFilterHeight
        {
            get => _dateFilterHeight;
            set => SetProperty(ref _dateFilterHeight, Math.Max(60, Math.Min(500, value)));
        }

        public double StatusFilterHeight
        {
            get => _statusFilterHeight;
            set => SetProperty(ref _statusFilterHeight, Math.Max(80, Math.Min(400, value)));
        }

        public double OptimizationFilterHeight
        {
            get => _optimizationFilterHeight;
            set => SetProperty(ref _optimizationFilterHeight, Math.Max(60, Math.Min(200, value)));
        }

        public double ContentTypesFilterHeight
        {
            get => _contentTypesFilterHeight;
            set => SetProperty(ref _contentTypesFilterHeight, Math.Max(120, Math.Min(500, value)));
        }

        public double CreatorsFilterHeight
        {
            get => _creatorsFilterHeight;
            set => SetProperty(ref _creatorsFilterHeight, Math.Max(120, Math.Min(500, value)));
        }

        public double LicenseTypeFilterHeight
        {
            get => _licenseTypeFilterHeight;
            set => SetProperty(ref _licenseTypeFilterHeight, Math.Max(100, Math.Min(400, value)));
        }

        public double FileSizeFilterHeight
        {
            get => _fileSizeFilterHeight;
            set => SetProperty(ref _fileSizeFilterHeight, Math.Max(100, Math.Min(400, value)));
        }

        public double SubfoldersFilterHeight
        {
            get => _subfoldersFilterHeight;
            set => SetProperty(ref _subfoldersFilterHeight, Math.Max(100, Math.Min(400, value)));
        }

        public double DamagedFilterHeight
        {
            get => _damagedFilterHeight;
            set => SetProperty(ref _damagedFilterHeight, Math.Max(60, Math.Min(200, value)));
        }

        // Filter Section Visibility Properties
        public bool DateFilterVisible
        {
            get => _dateFilterVisible;
            set => SetProperty(ref _dateFilterVisible, value);
        }

        public bool StatusFilterVisible
        {
            get => _statusFilterVisible;
            set => SetProperty(ref _statusFilterVisible, value);
        }

        public bool OptimizationFilterVisible
        {
            get => _optimizationFilterVisible;
            set => SetProperty(ref _optimizationFilterVisible, value);
        }

        public bool ContentTypesFilterVisible
        {
            get => _contentTypesFilterVisible;
            set => SetProperty(ref _contentTypesFilterVisible, value);
        }

        public bool CreatorsFilterVisible
        {
            get => _creatorsFilterVisible;
            set => SetProperty(ref _creatorsFilterVisible, value);
        }

        public bool LicenseTypeFilterVisible
        {
            get => _licenseTypeFilterVisible;
            set => SetProperty(ref _licenseTypeFilterVisible, value);
        }

        public bool FileSizeFilterVisible
        {
            get => _fileSizeFilterVisible;
            set => SetProperty(ref _fileSizeFilterVisible, value);
        }

        public bool SubfoldersFilterVisible
        {
            get => _subfoldersFilterVisible;
            set => SetProperty(ref _subfoldersFilterVisible, value);
        }

        public bool DamagedFilterVisible
        {
            get => _damagedFilterVisible;
            set => SetProperty(ref _damagedFilterVisible, value);
        }

        public bool SceneTypeFilterVisible
        {
            get => _sceneTypeFilterVisible;
            set => SetProperty(ref _sceneTypeFilterVisible, value);
        }

        public bool SceneCreatorFilterVisible
        {
            get => _sceneCreatorFilterVisible;
            set => SetProperty(ref _sceneCreatorFilterVisible, value);
        }

        public bool SceneSourceFilterVisible
        {
            get => _sceneSourceFilterVisible;
            set => SetProperty(ref _sceneSourceFilterVisible, value);
        }

        public bool PresetCategoryFilterVisible
        {
            get => _presetCategoryFilterVisible;
            set => SetProperty(ref _presetCategoryFilterVisible, value);
        }

        public bool PresetSubfolderFilterVisible
        {
            get => _presetSubfolderFilterVisible;
            set => SetProperty(ref _presetSubfolderFilterVisible, value);
        }

        public bool SceneDateFilterVisible
        {
            get => _sceneDateFilterVisible;
            set => SetProperty(ref _sceneDateFilterVisible, value);
        }

        public bool SceneFileSizeFilterVisible
        {
            get => _sceneFileSizeFilterVisible;
            set => SetProperty(ref _sceneFileSizeFilterVisible, value);
        }

        public bool PresetDateFilterVisible
        {
            get => _presetDateFilterVisible;
            set => SetProperty(ref _presetDateFilterVisible, value);
        }

        public bool PresetFileSizeFilterVisible
        {
            get => _presetFileSizeFilterVisible;
            set => SetProperty(ref _presetFileSizeFilterVisible, value);
        }

        public bool SceneStatusFilterVisible
        {
            get => _sceneStatusFilterVisible;
            set => SetProperty(ref _sceneStatusFilterVisible, value);
        }

        public bool PresetStatusFilterVisible
        {
            get => _presetStatusFilterVisible;
            set => SetProperty(ref _presetStatusFilterVisible, value);
        }

        // File Size Filter Settings Properties
        public double FileSizeTinyMax
        {
            get => _fileSizeTinyMax;
            set => SetProperty(ref _fileSizeTinyMax, Math.Max(0.1, Math.Min(1000, value)));
        }

        public double FileSizeSmallMax
        {
            get => _fileSizeSmallMax;
            set => SetProperty(ref _fileSizeSmallMax, Math.Max(1, Math.Min(1000, value)));
        }

        public double FileSizeMediumMax
        {
            get => _fileSizeMediumMax;
            set => SetProperty(ref _fileSizeMediumMax, Math.Max(10, Math.Min(10000, value)));
        }

        // Performance Settings Properties
        public bool EnableVirtualization
        {
            get => _enableVirtualization;
            set => SetProperty(ref _enableVirtualization, value);
        }

        public int CacheLength
        {
            get => _cacheLength;
            set => SetProperty(ref _cacheLength, Math.Max(1, value));
        }

        public bool EnableImageCaching
        {
            get => _enableImageCaching;
            set => SetProperty(ref _enableImageCaching, value);
        }

        public int ImageCacheSize
        {
            get => _imageCacheSize;
            set => SetProperty(ref _imageCacheSize, Math.Max(50, value));
        }

        public int MaxSafeSelection
        {
            get => _maxSafeSelection;
            set => SetProperty(ref _maxSafeSelection, Math.Max(50, Math.Min(5000, value)));
        }
        
        // Texture Validation Settings Properties
        public bool UseThoroughTextureScan
        {
            get => _useThoroughTextureScan;
            set => SetProperty(ref _useThoroughTextureScan, value);
        }
        
        // Package Downloader Settings Properties
        public bool EnableAutoDownload
        {
            get => _enableAutoDownload;
            set => SetProperty(ref _enableAutoDownload, value);
        }
        
        // Network Permission Settings Properties
        public bool NetworkPermissionGranted
        {
            get => _networkPermissionGranted;
            set => SetProperty(ref _networkPermissionGranted, value);
        }
        
        public bool NetworkPermissionAsked
        {
            get => _networkPermissionAsked;
            set => SetProperty(ref _networkPermissionAsked, value);
        }
        
        public bool NeverShowNetworkPermissionDialog
        {
            get => _neverShowNetworkPermissionDialog;
            set => SetProperty(ref _neverShowNetworkPermissionDialog, value);
        }
        
        // Package Optimizer Settings Properties
        public bool ForceLatestDependencies
        {
            get => _forceLatestDependencies;
            set => SetProperty(ref _forceLatestDependencies, value);
        }
        
        public bool DisableMorphPreload
        {
            get => _disableMorphPreload;
            set => SetProperty(ref _disableMorphPreload, value);
        }
        
        public bool MinifyJsonFiles
        {
            get => _minifyJsonFiles;
            set => SetProperty(ref _minifyJsonFiles, value);
        }
        
        // Filter Position Settings Properties
        public List<string> PackageFilterOrder
        {
            get => _packageFilterOrder;
            set => SetProperty(ref _packageFilterOrder, value ?? new List<string> { "DateFilter", "StatusFilter", "ContentTypesFilter", "CreatorsFilter", "LicenseTypeFilter", "FileSizeFilter", "SubfoldersFilter", "DamagedFilter" });
        }

        public List<string> SceneFilterOrder
        {
            get => _sceneFilterOrder;
            set => SetProperty(ref _sceneFilterOrder, value ?? new List<string> { "SceneTypeFilter", "SceneCreatorFilter", "SceneSourceFilter", "SceneDateFilter", "SceneFileSizeFilter", "SceneStatusFilter" });
        }

        public List<string> PresetFilterOrder
        {
            get => _presetFilterOrder;
            set => SetProperty(ref _presetFilterOrder, value ?? new List<string> { "PresetCategoryFilter", "PresetSubfolderFilter", "PresetDateFilter", "PresetFileSizeFilter", "PresetStatusFilter" });
        }

        // Sorting Settings Properties
        public Dictionary<string, SerializableSortingState> SortingStates
        {
            get => _sortingStates;
            set => SetProperty(ref _sortingStates, value ?? new Dictionary<string, SerializableSortingState>());
        }

        /// <summary>
        /// Helper method to set property values and raise PropertyChanged event
        /// </summary>
        protected virtual bool SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(backingStore, value))
                return false;

            backingStore = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// Raises the PropertyChanged event
        /// </summary>
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Creates a default settings instance with reasonable defaults
        /// </summary>
        public static AppSettings CreateDefault()
        {
            return new AppSettings
            {
                IsFirstLaunch = true,
                Theme = "Dark",
                ImageColumns = 3,
                SelectedFolder = "",
                CacheFolder = System.IO.Path.Combine(Environment.CurrentDirectory, "cache"),
                CascadeFiltering = false,
                WindowWidth = 1200,
                WindowHeight = 800,
                WindowLeft = 100,
                WindowTop = 100,
                WindowMaximized = false,
                LeftPanelWidth = 0.75,
                CenterPanelWidth = 1.52,
                RightPanelWidth = 1.0,
                ImagesPanelWidth = 1.78,
                DepsInfoSplitterHeight = 1.618,
                ShowPackageInfo = true,
                ShowDependencies = true,
                GroupImagesByPackage = true,
                SelectedStatusFilters = new List<string>(),
                SelectedOptimizationFilters = new List<string>(),
                SelectedCreatorFilters = new List<string>(),
                SelectedContentTypeFilters = new List<string>(),
                PackageSearchText = "",
                DependencySearchText = "",
                CreatorFilterText = "",
                ContentTypeFilterText = "",
                DateFilterHeight = 100,
                StatusFilterHeight = 120,
                OptimizationFilterHeight = 80,
                ContentTypesFilterHeight = 180,
                CreatorsFilterHeight = 180,
                LicenseTypeFilterHeight = 120,
                FileSizeFilterHeight = 120,
                SubfoldersFilterHeight = 120,
                DamagedFilterHeight = 80,
                DateFilterVisible = true,
                StatusFilterVisible = true,
                OptimizationFilterVisible = true,
                ContentTypesFilterVisible = true,
                CreatorsFilterVisible = true,
                LicenseTypeFilterVisible = true,
                FileSizeFilterVisible = true,
                SubfoldersFilterVisible = true,
                DamagedFilterVisible = true,
                SceneTypeFilterVisible = true,
                SceneCreatorFilterVisible = true,
                SceneSourceFilterVisible = true,
                PresetCategoryFilterVisible = true,
                PresetSubfolderFilterVisible = true,
                SceneDateFilterVisible = true,
                SceneFileSizeFilterVisible = true,
                PresetDateFilterVisible = true,
                PresetFileSizeFilterVisible = true,
                SceneStatusFilterVisible = true,
                PresetStatusFilterVisible = true,
                FileSizeTinyMax = 1,
                FileSizeSmallMax = 10,
                FileSizeMediumMax = 100,
                EnableVirtualization = true,
                CacheLength = 2,
                EnableImageCaching = true,
                ImageCacheSize = 500,
                MaxSafeSelection = 500,
                UseThoroughTextureScan = false,
                EnableAutoDownload = false,
                HideArchivedPackages = true,
                MinifyJsonFiles = true,
                PackageFilterOrder = new List<string> { "DateFilter", "StatusFilter", "ContentTypesFilter", "CreatorsFilter", "LicenseTypeFilter", "FileSizeFilter", "SubfoldersFilter", "DamagedFilter" },
                SceneFilterOrder = new List<string> { "SceneTypeFilter", "SceneCreatorFilter", "SceneSourceFilter", "SceneDateFilter", "SceneFileSizeFilter", "SceneStatusFilter" },
                PresetFilterOrder = new List<string> { "PresetCategoryFilter", "PresetSubfolderFilter", "PresetDateFilter", "PresetFileSizeFilter", "PresetStatusFilter" },
                SortingStates = new Dictionary<string, SerializableSortingState>()
            };
        }
    }
}

