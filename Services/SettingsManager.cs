using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using System.Windows.Threading;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Interface for settings management to support mocking in tests
    /// </summary>
    public interface ISettingsManager
    {
        AppSettings Settings { get; }
        event EventHandler<AppSettings> SettingsChanged;
        void LoadSettings();
        void SaveSettingsImmediate();
        Task SaveSettingsAsync();
        void UpdateSetting<T>(string propertyName, T value);
        T GetSetting<T>(string propertyName, T defaultValue = default);
        void ResetToDefaults();
        void ExportSettings(string filePath);
        void ImportSettings(string filePath);
        void Dispose();
    }

    /// <summary>
    /// Manages application settings with automatic saving and loading
    /// </summary>
    public class SettingsManager : ISettingsManager
    {
        private readonly string _settingsFilePath;
        private readonly string _cacheFolder;
        private readonly DispatcherTimer _saveTimer;
        private bool _hasUnsavedChanges = false;
        private readonly object _saveLock = new object();
        private readonly PlaylistCache _playlistCache;
        
        // Cache JsonSerializerOptions to avoid repeated allocations
        // Using JSON source generation for .NET 10 performance optimization
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            TypeInfoResolver = JsonSourceGenerationContext.Default
        };
        
        // Cache property accessors for fast reflection-free access
        private static readonly ConcurrentDictionary<string, Func<AppSettings, object>> _propertyGetters = new();
        private static readonly ConcurrentDictionary<string, Action<AppSettings, object>> _propertySetters = new();

        private AppSettings _settings;
        public AppSettings Settings 
        { 
            get => _settings;
            set => _settings = value;
        }

        public event EventHandler<AppSettings> SettingsChanged;

        /// <summary>
        /// Initializes the settings manager with the specified settings file path
        /// </summary>
        /// <param name="settingsFilePath">Path to the settings file (defaults to VPM.json in current directory)</param>
        public SettingsManager(string settingsFilePath = null)
        {
            // Use application directory if no path is specified
            // This ensures settings are saved in the same directory as the executable
            if (settingsFilePath != null)
            {
                _settingsFilePath = settingsFilePath;
            }
            else
            {
                var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var appPath = Path.Combine(appDirectory, "VPM.json");
                
                // Try to use app directory first
                try
                {
                    // Test if we can write to app directory
                    var testFile = Path.Combine(appDirectory, ".write_test");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);
                    _settingsFilePath = appPath;
                }
                catch
                {
                    // Fall back to user's local app data directory if app directory is not writable
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var vpmFolder = Path.Combine(localAppData, "VPM");
                    if (!Directory.Exists(vpmFolder))
                    {
                        Directory.CreateDirectory(vpmFolder);
                    }
                    _settingsFilePath = Path.Combine(vpmFolder, "VPM.json");
                }
            }
            
            // Settings file path configured
            
            // Determine cache folder
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _cacheFolder = Path.Combine(appDataPath, "VPM", "Cache");

            // Initialize PlaylistCache
            _playlistCache = new PlaylistCache(_cacheFolder);

            // Initialize auto-save timer (saves after 1 second of inactivity)
            _saveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _saveTimer.Tick += SaveTimer_Tick;

            // Load existing settings or create defaults
            LoadSettings();
            
            // Subscribe to property changes for auto-save
            Settings.PropertyChanged += Settings_PropertyChanged;
        }

        /// <summary>
        /// Loads settings from file or creates default settings if file doesn't exist
        /// </summary>
        public void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var loadedSettings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
                    
                    if (loadedSettings != null)
                    {
                        Settings = loadedSettings;
                        
                        // Apply migrations based on settings version
                        MigrateSettings(Settings);
                    }
                    else
                    {
                        Settings = AppSettings.CreateDefault();
                    }
                }
                else
                {
                    Settings = AppSettings.CreateDefault();
                    
                    // Don't save default settings immediately on first launch
                    // Let the first launch setup process handle saving after user selects game path
                }
            }
            catch (Exception)
            {
                Settings = AppSettings.CreateDefault();
            }

            // Load playlists from cache
            try
            {
                if (_playlistCache != null)
                {
                    var cachedPlaylists = _playlistCache.LoadPlaylists();
                    if (cachedPlaylists != null && cachedPlaylists.Count > 0)
                    {
                        Settings.Playlists = cachedPlaylists;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Applies migrations to settings based on version
        /// </summary>
        private void MigrateSettings(AppSettings settings)
        {
            if (settings == null)
                return;

            // Ensure all required filters are present in filter orders
            EnsureAllFiltersInOrder(settings);

            // Update version to current
            if (settings.SettingsVersion < 2)
            {
                settings.SettingsVersion = 2;
            }
        }

        /// <summary>
        /// Ensures all required filters are present in all filter order lists
        /// </summary>
        private void EnsureAllFiltersInOrder(AppSettings settings)
        {
            if (settings == null)
                return;

            // Ensure Package filters
            if (settings.PackageFilterOrder != null)
            {
                FilterConfiguration.EnsureFiltersInOrder(settings.PackageFilterOrder, FilterConfiguration.PackageFilters);
            }
            else
            {
                settings.PackageFilterOrder = new List<string>(FilterConfiguration.PackageFilters);
            }

            // Ensure Scene filters
            if (settings.SceneFilterOrder != null)
            {
                FilterConfiguration.EnsureFiltersInOrder(settings.SceneFilterOrder, FilterConfiguration.SceneFilters);
            }
            else
            {
                settings.SceneFilterOrder = new List<string>(FilterConfiguration.SceneFilters);
            }

            // Ensure Preset filters
            if (settings.PresetFilterOrder != null)
            {
                FilterConfiguration.EnsureFiltersInOrder(settings.PresetFilterOrder, FilterConfiguration.PresetFilters);
            }
            else
            {
                settings.PresetFilterOrder = new List<string>(FilterConfiguration.PresetFilters);
            }
        }

        /// <summary>
        /// Saves settings immediately (synchronous)
        /// </summary>
        public void SaveSettingsImmediate()
        {
            lock (_saveLock)
            {
                try
                {
                    var json = JsonSerializer.Serialize(Settings, _jsonOptions);
                    
                    // Ensure directory exists
                    var directory = Path.GetDirectoryName(_settingsFilePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Test write permissions before attempting to save
                    var tempFile = Path.Combine(directory ?? ".", ".write_test_" + Guid.NewGuid().ToString("N")[..8]);
                    try
                    {
                        File.WriteAllText(tempFile, "test");
                        File.Delete(tempFile);
                    }
                    catch
                    {
                        throw new UnauthorizedAccessException($"Cannot write to settings directory: {directory}");
                    }

                    File.WriteAllText(_settingsFilePath, json);
                    _hasUnsavedChanges = false;

                    // Save playlists to cache
                    if (_playlistCache != null && Settings.Playlists != null)
                    {
                        _playlistCache.SavePlaylists(Settings.Playlists);
                    }
                }
                catch
                {
                    throw; // Re-throw to let caller handle the error
                }
            }
        }

        /// <summary>
        /// Saves settings asynchronously with proper async I/O
        /// </summary>
        public async Task SaveSettingsAsync()
        {
            try
            {
                var json = JsonSerializer.Serialize(Settings, _jsonOptions);
                
                // Ensure directory exists
                var directory = Path.GetDirectoryName(_settingsFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(_settingsFilePath, json);
                _hasUnsavedChanges = false;

                // Save playlists to cache
                if (_playlistCache != null && Settings.Playlists != null)
                {
                    await Task.Run(() => _playlistCache.SavePlaylists(Settings.Playlists));
                }
            }
            catch (Exception)
            {
                // Swallow exception - non-critical operation
            }
        }

        /// <summary>
        /// Schedules an auto-save after a short delay
        /// </summary>
        private void ScheduleAutoSave()
        {
            _hasUnsavedChanges = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>
        /// Handles the save timer tick event
        /// </summary>
        private void SaveTimer_Tick(object sender, EventArgs e)
        {
            _saveTimer.Stop();
            
            if (_hasUnsavedChanges)
            {
                SaveSettingsImmediate();
            }
        }

        /// <summary>
        /// Handles property changes in the settings object
        /// </summary>
        private void Settings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            ScheduleAutoSave();
            SettingsChanged?.Invoke(this, Settings);
        }

        /// <summary>
        /// Updates a setting value and triggers auto-save (optimized with cached delegates)
        /// </summary>
        public void UpdateSetting<T>(string propertyName, T value)
        {
            try
            {
                var setter = GetOrCreatePropertySetter(propertyName);
                if (setter != null)
                {
                    var getter = GetOrCreatePropertyGetter(propertyName);
                    var currentValue = getter?.Invoke(Settings);
                    
                    if (!Equals(currentValue, value))
                    {
                        setter.Invoke(Settings, value);
                    }
                }
            }
            catch (Exception)
            {
                // Swallow exception - non-critical operation
            }
        }
        
        /// <summary>
        /// Gets or creates a cached property getter delegate for fast access
        /// </summary>
        private static Func<AppSettings, object> GetOrCreatePropertyGetter(string propertyName)
        {
            return _propertyGetters.GetOrAdd(propertyName, name =>
            {
                var property = typeof(AppSettings).GetProperty(name);
                if (property == null || !property.CanRead)
                    return null;
                    
                // Create compiled expression for fast property access
                var parameter = Expression.Parameter(typeof(AppSettings), "settings");
                var propertyAccess = Expression.Property(parameter, property);
                var convertToObject = Expression.Convert(propertyAccess, typeof(object));
                return Expression.Lambda<Func<AppSettings, object>>(convertToObject, parameter).Compile();
            });
        }
        
        /// <summary>
        /// Gets or creates a cached property setter delegate for fast access
        /// </summary>
        private static Action<AppSettings, object> GetOrCreatePropertySetter(string propertyName)
        {
            return _propertySetters.GetOrAdd(propertyName, name =>
            {
                var property = typeof(AppSettings).GetProperty(name);
                if (property == null || !property.CanWrite)
                    return null;
                    
                // Create compiled expression for fast property setting
                var instanceParam = Expression.Parameter(typeof(AppSettings), "settings");
                var valueParam = Expression.Parameter(typeof(object), "value");
                var convertedValue = Expression.Convert(valueParam, property.PropertyType);
                var propertyAccess = Expression.Property(instanceParam, property);
                var assign = Expression.Assign(propertyAccess, convertedValue);
                return Expression.Lambda<Action<AppSettings, object>>(assign, instanceParam, valueParam).Compile();
            });
        }

        /// <summary>
        /// Gets a setting value (optimized with cached delegates)
        /// </summary>
        public T GetSetting<T>(string propertyName, T defaultValue = default(T))
        {
            try
            {
                var getter = GetOrCreatePropertyGetter(propertyName);
                if (getter != null)
                {
                    var value = getter.Invoke(Settings);
                    if (value is T typedValue)
                    {
                        return typedValue;
                    }
                }
            }
            catch (Exception)
            {
                // Swallow exception - return default value
            }
            
            return defaultValue;
        }

        /// <summary>
        /// Resets all settings to defaults
        /// </summary>
        public void ResetToDefaults()
        {
            
            // Unsubscribe from current settings
            Settings.PropertyChanged -= Settings_PropertyChanged;
            
            // Create new default settings
            Settings = AppSettings.CreateDefault();
            
            // Subscribe to new settings
            Settings.PropertyChanged += Settings_PropertyChanged;
            
            // Save immediately
            SaveSettingsImmediate();
            
            // Notify listeners
            SettingsChanged?.Invoke(this, Settings);
        }

        /// <summary>
        /// Exports settings to a specified file
        /// </summary>
        public void ExportSettings(string filePath)
        {
            try
            {
                var json = JsonSerializer.Serialize(Settings, _jsonOptions);
                File.WriteAllText(filePath, json);
                
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>
        /// Imports settings from a specified file
        /// </summary>
        public void ImportSettings(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"Settings file not found: {filePath}");
                }

                var json = File.ReadAllText(filePath);
                var importedSettings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
                
                if (importedSettings != null)
                {
                    // Unsubscribe from current settings
                    Settings.PropertyChanged -= Settings_PropertyChanged;
                    
                    Settings = importedSettings;
                    
                    // Subscribe to new settings
                    Settings.PropertyChanged += Settings_PropertyChanged;
                    
                    // Save the imported settings
                    SaveSettingsImmediate();
                    
                    // Notify listeners
                    SettingsChanged?.Invoke(this, Settings);
                    
                }
                else
                {
                    throw new InvalidOperationException("Failed to deserialize imported settings");
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>
        /// Disposes the settings manager and saves any pending changes
        /// </summary>
        public void Dispose()
        {
            _saveTimer?.Stop();
            
            if (_hasUnsavedChanges)
            {
                SaveSettingsImmediate();
            }
            
            if (Settings != null)
            {
                Settings.PropertyChanged -= Settings_PropertyChanged;
            }
        }
    }
}

