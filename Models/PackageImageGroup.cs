using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace VPM.Models
{
    public class PackageImageGroup : INotifyPropertyChanged
    {
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

        private string _displayName;
        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName != value)
                {
                    _displayName = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _status;
        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
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

        public ObservableCollection<ImagePreviewItem> Images { get; set; } = new ObservableCollection<ImagePreviewItem>();
        
        private int _columnCount = 3;
        public int ColumnCount
        {
            get => _columnCount;
            set
            {
                if (_columnCount != value)
                {
                    _columnCount = value;
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
