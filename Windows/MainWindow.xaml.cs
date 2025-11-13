using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    /// <summary>
    /// Main window for the VPM application
    /// This partial class contains the core initialization logic.
    /// Other functionality is split across partial class files:
    /// - MainWindow.ImageManagement.cs: Image display and layout management
    /// - MainWindow.PackageOperations.cs: Load/unload operations and button management
    /// - MainWindow.EventHandlers.cs: UI event handlers
    /// - MainWindow.FilteringAndSearch.cs: Search and filtering functionality
    /// - MainWindow.UIManagement.cs: Theme, console, and settings management
    /// </summary>
        public partial class MainWindow : Window
        {
            // Ensure ImageManager and PackageDownloader are disposed to release resources
            protected override void OnClosed(EventArgs e)
            {
                base.OnClosed(e);
                _imageManager?.Dispose();
                DisposePackageDownloader();
            }
        #region Fields and Properties
        
        // UI Constants
        private const double UI_CORNER_RADIUS = 4.0;
        
        // Collections
        public OptimizedObservableCollection<PackageItem> Packages { get; set; }
        public AsyncObservableCollection<DependencyItem> Dependencies { get; set; }
        public OptimizedObservableCollection<SceneItem> Scenes { get; set; }
        public OptimizedObservableCollection<CustomAtomItem> CustomAtomItems { get; set; }
        public ICollectionView PackagesView { get; set; }
        public ICollectionView ScenesView { get; set; }
        public ICollectionView CustomAtomItemsView { get; set; }
        
        // Service managers
        private PackageManager _packageManager;
        private ImageManager _imageManager;
        private FilterManager _filterManager;
        private ReactiveFilterManager _reactiveFilterManager;
        private SettingsManager _settingsManager;
        private KeyboardNavigationManager _keyboardNavigationManager;
        private PackageFileManager _packageFileManager;
        private SceneScanner _sceneScanner;
        private CustomAtomPersonScanner _customAtomPersonScanner;

        private string _cacheFolder;
        
        // Suppress selection handling when programmatically updating lists/filters
        private bool _suppressSelectionEvents = false;
        private bool _suppressDependenciesSelectionEvents = false;
        
        // Cascade filtering setting (enabled by default for better UX)
        private bool _cascadeFiltering = true;
        
        // Store original dependencies for filtering
        private List<DependencyItem> _originalDependencies = new List<DependencyItem>();
        
        // Track whether we're showing dependencies or dependents
        private bool _showingDependents = false;
        
        // Store counts for both tabs
        private int _dependenciesCount = 0;
        private int _dependentsCount = 0;
        
        #endregion
        
        #region Constructor
        
        public MainWindow()
        {
            InitializeComponent();
            
            // Set version in menu button
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
            {
                MenuButton.Header = $"☰ VPM v{version.Major}.{version.Minor}.{version.Build}";
            }

            // Initialize collections
            Packages = new OptimizedObservableCollection<PackageItem>();
            Dependencies = new AsyncObservableCollection<DependencyItem>();
            Scenes = new OptimizedObservableCollection<SceneItem>();
            CustomAtomItems = new OptimizedObservableCollection<CustomAtomItem>();

            var packagesSource = (CollectionViewSource)FindResource("PackagesView");
            packagesSource.Source = Packages;
            PackagesView = packagesSource.View;

            var scenesSource = new CollectionViewSource { Source = Scenes };
            ScenesView = scenesSource.View;

            var customAtomItemsSource = new CollectionViewSource { Source = CustomAtomItems };
            CustomAtomItemsView = customAtomItemsSource.View;

            // Initialize settings manager first
            _settingsManager = new SettingsManager();
            _settingsManager.SettingsChanged += OnSettingsChanged;

            // Apply loaded settings to current variables
            ApplySettingsToUI();

            // Initialize cache folder from settings
            _cacheFolder = _settingsManager.Settings.CacheFolder;


            // Initialize service managers
            _packageManager = new PackageManager(_cacheFolder);
            _imageManager = new ImageManager(_cacheFolder, _packageManager);

            _filterManager = new FilterManager();
            _reactiveFilterManager = new ReactiveFilterManager(_filterManager);
            
            // Sync file size filter settings from AppSettings to FilterManager
            _filterManager.FileSizeTinyMax = _settingsManager.Settings.FileSizeTinyMax;
            _filterManager.FileSizeSmallMax = _settingsManager.Settings.FileSizeSmallMax;
            _filterManager.FileSizeMediumMax = _settingsManager.Settings.FileSizeMediumMax;
            _filterManager.HideArchivedPackages = _settingsManager.Settings.HideArchivedPackages;

            // Initialize PackageFileManager if we have a selected folder
            InitializePackageFileManager();

            // Initialize SceneScanner and CustomAtomPersonScanner
            if (!string.IsNullOrEmpty(_settingsManager.Settings.SelectedFolder))
            {
                _sceneScanner = new SceneScanner(_settingsManager.Settings.SelectedFolder);
                _customAtomPersonScanner = new CustomAtomPersonScanner(_settingsManager.Settings.SelectedFolder);
            }

            // Initialize keyboard navigation manager
            _keyboardNavigationManager = new KeyboardNavigationManager(this);
            SetupKeyboardNavigationEvents();

            // Initialize favorites manager
            InitializeFavoritesManager();

            // Initialize autoinstall manager
            InitializeAutoInstallManager();

            // Initialize renaming service
            InitializeRenamingService();

            // Only load sample data if no folder is selected (not first launch with auto-detected path)
            if (string.IsNullOrEmpty(_settingsManager.Settings.SelectedFolder))
            {
                LoadSampleData();
            }

            // Set up data binding
            PackageDataGrid.ItemsSource = PackagesView;
            DependenciesDataGrid.ItemsSource = Dependencies;

            // Initialize search boxes
            InitializeSearchBoxes();

            // Initialize date filter
            InitializeDateFilter();

            // Initialize file size filter
            InitializeFileSizeFilter();

            // Initialize sorting
            InitializeSorting();

            // Set up event handlers for UI elements
            SetupEventHandlers();

            // Update UI
            UpdateUI();

            // Set up window event handlers
            this.Loaded += OnWindowLoaded;
            this.Closing += OnWindowClosing;
            this.SizeChanged += OnWindowSizeChanged;
            this.LocationChanged += OnWindowLocationChanged;
            this.StateChanged += OnWindowStateChanged;

            // Initialize console window visibility
            InitializeConsoleWindow();

            // Initialize dependencies tab state
            InitializeDependenciesTabs();

            // Initialize content mode dropdown selection
            // App starts in Packages mode, so dropdown should show "Packages"
            if (ContentModeDropdown != null && ContentModeDropdown.Items.Count > 0)
            {
                ContentModeDropdown.SelectedIndex = 0; // Select "📦 Packages"
            }

        }
        
        #endregion
        
        #region Initialization Helpers
        
        /// <summary>
        /// Set up event handlers for UI elements
        /// </summary>
        private void SetupEventHandlers()
        {
            try
            {
                // Package data grid events
                PackageDataGrid.SelectionChanged += PackageDataGrid_SelectionChanged;
                PackageDataGrid.PreviewMouseDown += PackageDataGrid_PreviewMouseDown;
                PackageDataGrid.PreviewMouseUp += PackageDataGrid_PreviewMouseUp;
                PackageDataGrid.PreviewMouseMove += PackageDataGrid_PreviewMouseMove;
                PackageDataGrid.KeyDown += PackageDataGrid_KeyDown;
                PackageDataGrid.MouseDoubleClick += PackageDataGrid_MouseDoubleClick;
                
                // Dependencies data grid events
                DependenciesDataGrid.SelectionChanged += DependenciesDataGrid_SelectionChanged;
                DependenciesDataGrid.PreviewMouseDown += DependenciesDataGrid_PreviewMouseDown;
                DependenciesDataGrid.PreviewMouseUp += DependenciesDataGrid_PreviewMouseUp;
                DependenciesDataGrid.PreviewMouseMove += DependenciesDataGrid_PreviewMouseMove;
                DependenciesDataGrid.KeyDown += DependenciesDataGrid_KeyDown;
                DependenciesDataGrid.MouseDoubleClick += DependenciesDataGrid_MouseDoubleClick;
                DependenciesDataGrid.GotFocus += DependenciesDataGrid_GotFocus;
                DependenciesDataGrid.LostFocus += DependenciesDataGrid_LostFocus;
                
                // Search box events
                PackageSearchBox.TextChanged += PackageSearchBox_TextChanged;
                PackageSearchBox.GotFocus += PackageSearchBox_GotFocus;
                PackageSearchBox.LostFocus += PackageSearchBox_LostFocus;
                
                DepsSearchBox.TextChanged += DepsSearchBox_TextChanged;
                DepsSearchBox.GotFocus += DepsSearchBox_GotFocus;
                DepsSearchBox.LostFocus += DepsSearchBox_LostFocus;
                
                // Filter list events
                StatusFilterList.SelectionChanged += StatusFilterList_SelectionChanged;
                CreatorsList.SelectionChanged += CreatorsList_SelectionChanged;
                ContentTypesList.SelectionChanged += ContentTypesList_SelectionChanged;
                
                // Image scroll viewer events
                ImagesScrollViewer.PreviewMouseWheel += ImagesScrollViewer_PreviewMouseWheel;
                
                // Event handlers setup completed
            }
            catch (Exception)
            {
                // Error setting up event handlers
            }
        }

        private void InitializeDependenciesTabs()
        {
            DependenciesTab.Tag = "Active";
            DependentsTab.Tag = null;
        }
        
        #endregion
    }
}

