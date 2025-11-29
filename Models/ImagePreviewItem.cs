using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace VPM.Models
{
    public class ImagePreviewItem : INotifyPropertyChanged
    {
        private ImageSource _image;
        public ImageSource Image
        {
            get => _image;
            set
            {
                if (_image != value)
                {
                    _image = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _packageName;
        public string PackageName
        {
            get => _packageName;
            set
            {
                if (_packageName != value)
                {
                    _packageName = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _internalPath;
        public string InternalPath
        {
            get => _internalPath;
            set
            {
                if (_internalPath != value)
                {
                    _internalPath = value;
                    OnPropertyChanged();
                    UpdateExtractionButtonState();
                }
            }
        }

        private Brush _statusBrush;
        public Brush StatusBrush
        {
            get => _statusBrush;
            set
            {
                if (_statusBrush != value)
                {
                    _statusBrush = value;
                    OnPropertyChanged();
                }
            }
        }

        private PackageItem _packageItem;
        public PackageItem PackageItem
        {
            get => _packageItem;
            set
            {
                if (_packageItem != value)
                {
                    _packageItem = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isExtracted;
        public bool IsExtracted
        {
            get => _isExtracted;
            set
            {
                if (_isExtracted != value)
                {
                    _isExtracted = value;
                    OnPropertyChanged();
                    UpdateExtractionButtonState();
                }
            }
        }

        private string _extractionStatusIcon;
        public string ExtractionStatusIcon
        {
            get => _extractionStatusIcon;
            set
            {
                if (_extractionStatusIcon != value)
                {
                    _extractionStatusIcon = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _extractionStatusText;
        public string ExtractionStatusText
        {
            get => _extractionStatusText;
            set
            {
                if (_extractionStatusText != value)
                {
                    _extractionStatusText = value;
                    OnPropertyChanged();
                }
            }
        }

        private Brush _extractionButtonBackground;
        public Brush ExtractionButtonBackground
        {
            get => _extractionButtonBackground;
            set
            {
                if (_extractionButtonBackground != value)
                {
                    _extractionButtonBackground = value;
                    OnPropertyChanged();
                }
            }
        }

        public void UpdateExtractionButtonState()
        {
            // Get category name
            var category = GetCategoryFromPath(InternalPath);
            
            ExtractionStatusText = category;

            if (IsExtracted)
            {
                // Show checkmark with label
                ExtractionStatusIcon = "✓";
                // Neutral green for extracted state (not too bright)
                var brush = new SolidColorBrush(Color.FromArgb(160, 60, 120, 70));
                brush.Freeze();
                ExtractionButtonBackground = brush;
            }
            else
            {
                // Determine icon based on category
                string iconText = "📥"; // Default
                if (string.Equals(category, "Hair", System.StringComparison.OrdinalIgnoreCase)) iconText = "✂️";
                else if (string.Equals(category, "Clothing", System.StringComparison.OrdinalIgnoreCase)) iconText = "👕";
                else if (string.Equals(category, "Skin", System.StringComparison.OrdinalIgnoreCase)) iconText = "🎨";
                else if (string.Equals(category, "Appearance", System.StringComparison.OrdinalIgnoreCase)) iconText = "👤";
                else if (string.Equals(category, "Scene", System.StringComparison.OrdinalIgnoreCase)) iconText = "🎬";
                
                // Show extract button with icon and label
                ExtractionStatusIcon = iconText; 
                // Transparent gray for available state
                var brush = new SolidColorBrush(Color.FromArgb(120, 80, 80, 80));
                brush.Freeze();
                ExtractionButtonBackground = brush;
            }
        }

        private string GetCategoryFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "Unknown";
            
            var parts = path.Split(new[] { '/', '\\' }, System.StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (string.Equals(part, "Hair", System.StringComparison.OrdinalIgnoreCase)) return "Hair";
                if (string.Equals(part, "Clothing", System.StringComparison.OrdinalIgnoreCase)) return "Clothing";
                if (string.Equals(part, "Skins", System.StringComparison.OrdinalIgnoreCase) || string.Equals(part, "Skin", System.StringComparison.OrdinalIgnoreCase)) return "Skin";
                if (string.Equals(part, "Looks", System.StringComparison.OrdinalIgnoreCase) || string.Equals(part, "Appearance", System.StringComparison.OrdinalIgnoreCase)) return "Appearance";
                if (string.Equals(part, "Saves", System.StringComparison.OrdinalIgnoreCase) || string.Equals(part, "Scene", System.StringComparison.OrdinalIgnoreCase)) return "Scene";
            }
            
            // Fallback to second folder if available (usually Custom/Category/...)
            if (parts.Length > 1 && string.Equals(parts[0], "Custom", System.StringComparison.OrdinalIgnoreCase))
                return parts[1];
                
            return "Content";
        }

        private int _imageWidth;
        public int ImageWidth
        {
            get => _imageWidth;
            set
            {
                if (_imageWidth != value)
                {
                    _imageWidth = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _imageHeight;
        public int ImageHeight
        {
            get => _imageHeight;
            set
            {
                if (_imageHeight != value)
                {
                    _imageHeight = value;
                    OnPropertyChanged();
                }
            }
        }

        public System.Func<System.Threading.Tasks.Task<System.Windows.Media.Imaging.BitmapImage>> LoadImageCallback { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
