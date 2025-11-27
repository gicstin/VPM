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

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
