using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VPM.Services;

namespace VPM.Models
{
    /// <summary>
    /// Represents a package item in the package manager
    /// </summary>
    public class PackageItem : INotifyPropertyChanged, IComparable<PackageItem>
    {
        private string _name = "";
        private string _status = "";
        private string _creator = "";
        private long _fileSize = 0;
        private DateTime? _modifiedDate = null;
        private bool _isLatestVersion = true;
        private int _dependencyCount = 0;
        private int _dependentsCount = 0;
        private bool _isOptimized = false;
        private bool _isDuplicate = false;
        private int _duplicateLocationCount = 1;
        private bool _isOldVersion = false;
        private int _latestVersionNumber = 1;
        private bool _isFavorite = false;
        private bool _isAutoInstall = false;
        private int _morphCount = 0;
        private int _hairCount = 0;
        private int _clothingCount = 0;
        private int _sceneCount = 0;
        private int _looksCount = 0;
        private int _posesCount = 0;
        private int _assetsCount = 0;
        private int _scriptsCount = 0;
        private int _pluginsCount = 0;
        private int _subScenesCount = 0;
        private int _skinsCount = 0;
        private bool _isDamaged = false;
        private string _damageReason = "";
        private int _missingDependencyCount = 0;

        public event PropertyChangedEventHandler PropertyChanged;

        // Store the metadata dictionary key for fast lookup
        public string MetadataKey { get; set; } = "";

        public string Name
        {
            get => _name ?? "";
            set
            {
                if (SetProperty(ref _name, value ?? ""))
                {
                    // Notify that DisplayName has also changed
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        public string Status
        {
            get => _status ?? "";
            set
            {
                if (SetProperty(ref _status, value ?? ""))
                {
                    // Notify dependent properties
                    OnPropertyChanged(nameof(StatusIcon));
                    OnPropertyChanged(nameof(StatusColor));
                }
            }
        }

        public string Creator
        {
            get => _creator ?? "";
            set => SetProperty(ref _creator, value ?? "");
        }

        public long FileSize
        {
            get => _fileSize;
            set
            {
                if (SetProperty(ref _fileSize, value))
                {
                    OnPropertyChanged(nameof(FileSizeFormatted));
                }
            }
        }

        public DateTime? ModifiedDate
        {
            get => _modifiedDate;
            set
            {
                if (SetProperty(ref _modifiedDate, value))
                {
                    OnPropertyChanged(nameof(DateFormatted));
                }
            }
        }

        public bool IsLatestVersion
        {
            get => _isLatestVersion;
            set
            {
                if (SetProperty(ref _isLatestVersion, value))
                {
                    OnPropertyChanged(nameof(VersionStatus));
                    OnPropertyChanged(nameof(VersionStatusColor));
                }
            }
        }

        public int DependencyCount
        {
            get => _dependencyCount;
            set => SetProperty(ref _dependencyCount, value);
        }

        public int DependentsCount
        {
            get => _dependentsCount;
            set => SetProperty(ref _dependentsCount, value);
        }
        
        public int MissingDependencyCount
        {
            get => _missingDependencyCount;
            set => SetProperty(ref _missingDependencyCount, value);
        }
        
        public bool HasMissingDependencies => _missingDependencyCount > 0;
        
        public bool IsOptimized
        {
            get => _isOptimized;
            set => SetProperty(ref _isOptimized, value);
        }
        
        public bool IsDuplicate
        {
            get => _isDuplicate;
            set
            {
                if (SetProperty(ref _isDuplicate, value))
                {
                    OnPropertyChanged(nameof(DuplicateIndicator));
                    OnPropertyChanged(nameof(DuplicateTooltip));
                }
            }
        }

        public int DuplicateLocationCount
        {
            get => _duplicateLocationCount;
            set
            {
                if (SetProperty(ref _duplicateLocationCount, value))
                {
                    OnPropertyChanged(nameof(DuplicateIndicator));
                    OnPropertyChanged(nameof(DuplicateTooltip));
                }
            }
        }

        public bool IsOldVersion
        {
            get => _isOldVersion;
            set
            {
                if (SetProperty(ref _isOldVersion, value))
                {
                    OnPropertyChanged(nameof(VersionIndicator));
                    OnPropertyChanged(nameof(VersionTooltip));
                }
            }
        }

        public int LatestVersionNumber
        {
            get => _latestVersionNumber;
            set
            {
                if (SetProperty(ref _latestVersionNumber, value))
                {
                    OnPropertyChanged(nameof(VersionTooltip));
                }
            }
        }

        public bool IsFavorite
        {
            get => _isFavorite;
            set => SetProperty(ref _isFavorite, value);
        }

        public bool IsAutoInstall
        {
            get => _isAutoInstall;
            set => SetProperty(ref _isAutoInstall, value);
        }

        public int MorphCount
        {
            get => _morphCount;
            set => SetProperty(ref _morphCount, value);
        }

        public int HairCount
        {
            get => _hairCount;
            set => SetProperty(ref _hairCount, value);
        }

        public int ClothingCount
        {
            get => _clothingCount;
            set => SetProperty(ref _clothingCount, value);
        }

        public int SceneCount
        {
            get => _sceneCount;
            set => SetProperty(ref _sceneCount, value);
        }

        public int LooksCount
        {
            get => _looksCount;
            set => SetProperty(ref _looksCount, value);
        }

        public int PosesCount
        {
            get => _posesCount;
            set => SetProperty(ref _posesCount, value);
        }

        public int AssetsCount
        {
            get => _assetsCount;
            set => SetProperty(ref _assetsCount, value);
        }

        public int ScriptsCount
        {
            get => _scriptsCount;
            set => SetProperty(ref _scriptsCount, value);
        }

        public int PluginsCount
        {
            get => _pluginsCount;
            set => SetProperty(ref _pluginsCount, value);
        }

        public int SubScenesCount
        {
            get => _subScenesCount;
            set => SetProperty(ref _subScenesCount, value);
        }

        public int SkinsCount
        {
            get => _skinsCount;
            set => SetProperty(ref _skinsCount, value);
        }

        public bool IsDamaged
        {
            get => _isDamaged;
            set
            {
                if (SetProperty(ref _isDamaged, value))
                {
                    OnPropertyChanged(nameof(DamageIndicator));
                    OnPropertyChanged(nameof(DamageTooltip));
                }
            }
        }

        public string DamageReason
        {
            get => _damageReason ?? "";
            set
            {
                if (SetProperty(ref _damageReason, value ?? ""))
                {
                    OnPropertyChanged(nameof(DamageTooltip));
                }
            }
        }

        // Display properties for the modern UI
        public string DisplayName
        {
            get
            {
                if (Name.EndsWith("#archived", StringComparison.OrdinalIgnoreCase))
                    return Name.Substring(0, Name.Length - 9);
                if (Name.EndsWith("#loaded", StringComparison.OrdinalIgnoreCase))
                    return Name.Substring(0, Name.Length - 7);
                return Name;
            }
        }
        public string FileSizeFormatted => FormatHelper.FormatFileSize(FileSize);
        public string DateFormatted => ModifiedDate?.ToString("MMM dd, yyyy") ?? "Unknown";
        
        /// <summary>
        /// Pre-computed metadata line to reduce visual tree complexity in DataGrid
        /// Combines Deps, Dependents, Size, and Date into a single string
        /// </summary>
        public string MetadataLine => $"Deps: {DependencyCount}  •  Dependents: {DependentsCount}  •  Size: {FileSizeFormatted}  •  Date: {DateFormatted}";
        
        public string VersionStatus => IsLatestVersion ? "" : "Outdated";
        
        public System.Windows.Media.Color VersionStatusColor => IsLatestVersion ? 
            System.Windows.Media.Color.FromRgb(158, 158, 158) : // Gray (hidden)
            System.Windows.Media.Color.FromRgb(244, 67, 54);    // Red for outdated
        
        public string DuplicateTooltip => DuplicateLocationCount <= 1
            ? "Package exists in a single location"
            : $"Duplicate found in {DuplicateLocationCount} locations";

        public string DuplicateIndicator => IsDuplicate ? "²" : "";

        public string VersionIndicator => IsOldVersion ? "³" : "";
        
        public string VersionTooltip => IsOldVersion 
            ? $"Old version detected. Latest version is {LatestVersionNumber}"
            : "Latest version";

        public string DamageIndicator => IsDamaged ? "–" : "";
        
        public string DamageTooltip => IsDamaged 
            ? $"Damaged package: {DamageReason}"
            : "Package integrity verified";

        public string StatusIcon
        {
            get
            {
                // Show duplicate indicator if this is a duplicate
                if (IsDuplicate)
                {
                    return "⚡"; // Lightning bolt for duplicates
                }
                
                return Status switch
                {
                    "Loaded" => "✓",
                    "Available" => "📦",
                    "Missing" => "✗",
                    "Outdated" => "⚠",
                    "Updating" => "↻",
                    "Archived" => "📁",
                    _ => "?"
                };
            }
        }
        
        public System.Windows.Media.Color StatusColor
        {
            get
            {
                // Show duplicate color if this is a duplicate
                if (IsDuplicate)
                {
                    return System.Windows.Media.Color.FromRgb(255, 235, 59); // Yellow for duplicates
                }
                
                return Status switch
                {
                    "Loaded" => System.Windows.Media.Color.FromRgb(76, 175, 80),      // Green
                    "Available" => System.Windows.Media.Color.FromRgb(33, 150, 243),  // Blue
                    "Missing" => System.Windows.Media.Color.FromRgb(244, 67, 54),     // Red
                    "Outdated" => System.Windows.Media.Color.FromRgb(255, 152, 0),    // Orange
                    "Updating" => System.Windows.Media.Color.FromRgb(156, 39, 176),   // Purple
                    "Archived" => System.Windows.Media.Color.FromRgb(139, 69, 19),    // Brown
                    _ => System.Windows.Media.Color.FromRgb(158, 158, 158)            // Gray
                };
            }
        }
        
        /// <summary>
        /// Implements IComparable to support safe sorting by name
        /// </summary>
        public int CompareTo(PackageItem other)
        {
            if (other == null) return 1;
            return string.Compare(Name ?? "", other.Name ?? "", StringComparison.OrdinalIgnoreCase);
        }

        protected virtual bool SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(backingStore, value))
                return false;

            backingStore = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    
    /// <summary>
    /// Represents a file item in a package for display in category tabs
    /// </summary>
    public class PackageFileItem : INotifyPropertyChanged
    {
        private string _filePath = "";
        private string _fileName = "";
        private string _fileExtension = "";

        public event PropertyChangedEventHandler PropertyChanged;

        public string FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value);
        }

        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value);
        }

        public string FileExtension
        {
            get => _fileExtension;
            set => SetProperty(ref _fileExtension, value);
        }

        protected virtual bool SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(backingStore, value))
                return false;

            backingStore = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    
    /// <summary>
    /// Represents a dependency item in the package manager
    /// </summary>
    public class DependencyItem : INotifyPropertyChanged
    {
        private string _name = "";
        private string _status = "";
        private string _version = "";
        private bool _isEnabled = true;
        private bool _forceLatest = false;

        public event PropertyChangedEventHandler PropertyChanged;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    // Notify dependent properties
                    OnPropertyChanged(nameof(StatusIcon));
                    OnPropertyChanged(nameof(StatusColor));
                }
            }
        }

        public string Version
        {
            get => _version;
            set
            {
                if (SetProperty(ref _version, value))
                {
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        public bool ForceLatest
        {
            get => _forceLatest;
            set => SetProperty(ref _forceLatest, value);
        }
        
        // Display properties for the modern UI
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(_version))
                {
                    return $"{_name}.{_version}";
                }
                return _name;
            }
        }
        
        public string StatusIcon
        {
            get
            {
                return Status switch
                {
                    "Loaded" => "✓",
                    "Available" => "📦",
                    "Missing" => "✗",
                    "Unknown" => "?",
                    "Outdated" => "⚠",
                    "Updating" => "↻",
                    "Downloading" => "⬇",
                    "Archived" => "📁",
                    _ => "?"
                };
            }
        }
        
        public System.Windows.Media.Color StatusColor
        {
            get
            {
                var color = Status switch
                {
                    "Loaded" => System.Windows.Media.Color.FromRgb(76, 175, 80),     // Green
                    "Available" => System.Windows.Media.Color.FromRgb(33, 150, 243),  // Blue
                    "Missing" => System.Windows.Media.Color.FromRgb(244, 67, 54),     // Red
                    "Unknown" => System.Windows.Media.Color.FromRgb(244, 67, 54),     // Red for Unknown (treat as Missing)
                    "Outdated" => System.Windows.Media.Color.FromRgb(255, 152, 0),    // Orange
                    "Updating" => System.Windows.Media.Color.FromRgb(156, 39, 176),   // Purple
                    "Downloading" => System.Windows.Media.Color.FromRgb(3, 169, 244), // Light Blue
                    "Archived" => System.Windows.Media.Color.FromRgb(139, 69, 19),    // Brown
                    _ => System.Windows.Media.Color.FromRgb(158, 158, 158)            // Gray
                };
                
                
                return color;
            }
        }

        protected virtual bool SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(backingStore, value))
                return false;

            backingStore = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Represents a content item from a scene (hair, clothing, morph, atom)
    /// </summary>
    public class SceneContentItem
    {
        public string Content { get; set; } = "";
    }
}

