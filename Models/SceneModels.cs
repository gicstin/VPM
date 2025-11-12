using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VPM.Models
{
    /// <summary>
    /// Represents a VAM scene file with metadata and preview information
    /// </summary>
    public class SceneItem : INotifyPropertyChanged
    {
        private string _name = "";
        private string _displayName = "";
        private string _filePath = "";
        private string _thumbnailPath = "";
        private string _creator = "";
        private DateTime? _modifiedDate = null;
        private long _fileSize = 0;
        private int _atomCount = 0;
        private string _source = "";
        private string _sourcePackage = "";
        private List<string> _dependencies = new List<string>();
        private List<string> _atomTypes = new List<string>();
        private string _sceneType = "Unknown";
        private int _hairCount = 0;
        private int _clothingCount = 0;
        private int _morphCount = 0;
        private List<string> _hairItems = new List<string>();
        private List<string> _clothingItems = new List<string>();
        private List<string> _morphItems = new List<string>();
        private bool _isOptimized = false;
        private bool _isFavorite = false;
        private bool _isHidden = false;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Full name of the scene file (with extension)
        /// </summary>
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>
        /// Display name (without extension, cleaned up)
        /// </summary>
        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }

        /// <summary>
        /// Full path to the scene JSON file
        /// </summary>
        public string FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value);
        }

        /// <summary>
        /// Path to the thumbnail image (if exists)
        /// </summary>
        public string ThumbnailPath
        {
            get => _thumbnailPath;
            set
            {
                if (SetProperty(ref _thumbnailPath, value))
                {
                    OnPropertyChanged(nameof(HasThumbnail));
                }
            }
        }

        /// <summary>
        /// Creator name extracted from filename or metadata
        /// </summary>
        public string Creator
        {
            get => _creator;
            set => SetProperty(ref _creator, value);
        }

        /// <summary>
        /// Last modified date of the scene file
        /// </summary>
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

        /// <summary>
        /// File size in bytes
        /// </summary>
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

        /// <summary>
        /// Number of atoms in the scene
        /// </summary>
        public int AtomCount
        {
            get => _atomCount;
            set => SetProperty(ref _atomCount, value);
        }

        /// <summary>
        /// Source type: "Local" or "VAR"
        /// </summary>
        public string Source
        {
            get => _source;
            set
            {
                if (SetProperty(ref _source, value))
                {
                    OnPropertyChanged(nameof(IsLocal));
                    OnPropertyChanged(nameof(SourceIcon));
                }
            }
        }

        /// <summary>
        /// Source VAR package name (if from VAR)
        /// </summary>
        public string SourcePackage
        {
            get => _sourcePackage;
            set => SetProperty(ref _sourcePackage, value);
        }

        /// <summary>
        /// Whether the scene has a thumbnail image
        /// </summary>
        public bool HasThumbnail
        {
            get => !string.IsNullOrEmpty(_thumbnailPath);
        }

        /// <summary>
        /// Whether the scene is from local Saves folder
        /// </summary>
        public bool IsLocal
        {
            get => _source == "Local";
        }

        /// <summary>
        /// List of VAR package dependencies
        /// </summary>
        public List<string> Dependencies
        {
            get => _dependencies;
            set
            {
                if (SetProperty(ref _dependencies, value ?? new List<string>()))
                {
                    OnPropertyChanged(nameof(DependencyCount));
                }
            }
        }

        /// <summary>
        /// List of atom types in the scene (Person, CustomUnityAsset, etc.)
        /// </summary>
        public List<string> AtomTypes
        {
            get => _atomTypes;
            set => SetProperty(ref _atomTypes, value ?? new List<string>());
        }

        /// <summary>
        /// Scene type classification (e.g., "Person Scene", "Environment", etc.)
        /// </summary>
        public string SceneType
        {
            get => _sceneType;
            set => SetProperty(ref _sceneType, value);
        }

        /// <summary>
        /// Number of hair items in the scene
        /// </summary>
        public int HairCount
        {
            get => _hairCount;
            set => SetProperty(ref _hairCount, value);
        }

        /// <summary>
        /// Number of clothing items in the scene
        /// </summary>
        public int ClothingCount
        {
            get => _clothingCount;
            set => SetProperty(ref _clothingCount, value);
        }

        /// <summary>
        /// Number of morphs in the scene
        /// </summary>
        public int MorphCount
        {
            get => _morphCount;
            set => SetProperty(ref _morphCount, value);
        }

        /// <summary>
        /// List of hair items in the scene
        /// </summary>
        public List<string> HairItems
        {
            get => _hairItems;
            set => SetProperty(ref _hairItems, value ?? new List<string>());
        }

        /// <summary>
        /// List of clothing items in the scene
        /// </summary>
        public List<string> ClothingItems
        {
            get => _clothingItems;
            set => SetProperty(ref _clothingItems, value ?? new List<string>());
        }

        /// <summary>
        /// List of morph items in the scene
        /// </summary>
        public List<string> MorphItems
        {
            get => _morphItems;
            set => SetProperty(ref _morphItems, value ?? new List<string>());
        }

        /// <summary>
        /// Whether the scene has been optimized
        /// </summary>
        public bool IsOptimized
        {
            get => _isOptimized;
            set
            {
                if (SetProperty(ref _isOptimized, value))
                {
                    OnPropertyChanged(nameof(OptimizationIcon));
                }
            }
        }

        /// <summary>
        /// Whether the scene is marked as favorite
        /// </summary>
        public bool IsFavorite
        {
            get => _isFavorite;
            set => SetProperty(ref _isFavorite, value);
        }

        /// <summary>
        /// Whether the scene is marked as hidden
        /// </summary>
        public bool IsHidden
        {
            get => _isHidden;
            set => SetProperty(ref _isHidden, value);
        }

        // Display properties
        public string FileSizeFormatted => FormatFileSize(FileSize);
        public string DateFormatted => ModifiedDate?.ToString("MMM dd, yyyy") ?? "Unknown";
        public int DependencyCount => Dependencies?.Count ?? 0;
        
        public string SourceIcon => Source switch
        {
            "Local" => "📁",
            "VAR" => "📦",
            _ => "?"
        };

        public string OptimizationIcon => IsOptimized ? "⚡" : "";

        public string AtomCountDisplay => AtomCount > 0 ? $"{AtomCount} atoms" : "Unknown";

        public static string FormatFileSize(long bytes)
        {
            if (bytes == 0) return "0 B";
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.#} {sizes[order]}";
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
    /// Metadata extracted from scene JSON file
    /// </summary>
    public class SceneMetadata
    {
        public int AtomCount { get; set; }
        public int HairCount { get; set; }
        public int ClothingCount { get; set; }
        public int MorphCount { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();
        public List<string> AtomTypes { get; set; } = new List<string>();
        public Dictionary<string, int> AtomTypeCounts { get; set; } = new Dictionary<string, int>();
        public string SceneType { get; set; } = "Unknown";
        public bool HasPerson { get; set; }
        public bool HasEnvironment { get; set; }
        public bool HasCustomAssets { get; set; }
        public List<string> HairItems { get; set; } = new List<string>();
        public List<string> ClothingItems { get; set; } = new List<string>();
        public List<string> MorphItems { get; set; } = new List<string>();
    }

    /// <summary>
    /// Represents a custom atom person file (.vap) with metadata and preview information
    /// </summary>
    public class CustomAtomItem : INotifyPropertyChanged
    {
        private string _name = "";
        private string _displayName = "";
        private string _filePath = "";
        private string _thumbnailPath = "";
        private string _category = "";
        private string _subfolder = "";
        private DateTime? _modifiedDate = null;
        private long _fileSize = 0;
        private bool _isFavorite = false;
        private bool _isHidden = false;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Full name of the .vap file (with extension)
        /// </summary>
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>
        /// Display name (without extension, cleaned up)
        /// </summary>
        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }

        /// <summary>
        /// Full path to the .vap file
        /// </summary>
        public string FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value);
        }

        /// <summary>
        /// Path to the thumbnail image (if exists)
        /// </summary>
        public string ThumbnailPath
        {
            get => _thumbnailPath;
            set
            {
                if (SetProperty(ref _thumbnailPath, value))
                {
                    OnPropertyChanged(nameof(HasThumbnail));
                }
            }
        }

        /// <summary>
        /// Category (Hair, Clothing, Morphs, etc.)
        /// </summary>
        public string Category
        {
            get => _category;
            set => SetProperty(ref _category, value);
        }

        /// <summary>
        /// Subfolder path relative to Custom\Atom\Person
        /// </summary>
        public string Subfolder
        {
            get => _subfolder;
            set => SetProperty(ref _subfolder, value);
        }

        /// <summary>
        /// Last modified date of the file
        /// </summary>
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

        /// <summary>
        /// File size in bytes
        /// </summary>
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

        /// <summary>
        /// Whether the item has a thumbnail image
        /// </summary>
        public bool HasThumbnail
        {
            get => !string.IsNullOrEmpty(_thumbnailPath);
        }

        /// <summary>
        /// Whether the item is marked as favorite
        /// </summary>
        public bool IsFavorite
        {
            get => _isFavorite;
            set => SetProperty(ref _isFavorite, value);
        }

        /// <summary>
        /// Whether the item is marked as hidden
        /// </summary>
        public bool IsHidden
        {
            get => _isHidden;
            set => SetProperty(ref _isHidden, value);
        }

        // Display properties
        public string FileSizeFormatted => SceneItem.FormatFileSize(FileSize);
        public string DateFormatted => ModifiedDate?.ToString("MMM dd, yyyy") ?? "Unknown";

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
}

