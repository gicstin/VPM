using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VPM.Models
{
    public class DependencyItemModel : INotifyPropertyChanged
    {
        private string _name = "";
        private string _licenseType = "";
        private bool _isEnabled = true;
        private bool _hasSubDependencies = false;
        private int _depth = 0;
        private int _subDependencyCount = 0;
        private bool _forceLatest = false;
        private string _parentName = "";
        private bool _isDisabledByUser = false;
        private string _packageName = "";

        public event PropertyChangedEventHandler PropertyChanged;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string LicenseType
        {
            get => _licenseType;
            set => SetProperty(ref _licenseType, value);
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        public bool HasSubDependencies
        {
            get => _hasSubDependencies;
            set => SetProperty(ref _hasSubDependencies, value);
        }

        public int Depth
        {
            get => _depth;
            set => SetProperty(ref _depth, value);
        }

        public int SubDependencyCount
        {
            get => _subDependencyCount;
            set => SetProperty(ref _subDependencyCount, value);
        }

        public bool ForceLatest
        {
            get => _forceLatest;
            set
            {
                if (SetProperty(ref _forceLatest, value))
                {
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        public string ParentName
        {
            get => _parentName;
            set => SetProperty(ref _parentName, value);
        }

        public bool IsDisabledByUser
        {
            get => _isDisabledByUser;
            set
            {
                if (SetProperty(ref _isDisabledByUser, value))
                {
                    OnPropertyChanged(nameof(DisplayName));
                    OnPropertyChanged(nameof(IndentedName));
                }
            }
        }

        public string PackageName
        {
            get => _packageName;
            set => SetProperty(ref _packageName, value);
        }

        public bool WillBeConvertedToLatest => !Name.EndsWith(".latest", System.StringComparison.OrdinalIgnoreCase);

        public string DisplayName
        {
            get
            {
                string baseName = Name;
                if (IsDisabledByUser)
                {
                    baseName = "🔴 " + baseName + " [DISABLED - Can Re-enable]";
                }
                
                // Show .latest indicator even for disabled dependencies
                if (ForceLatest && WillBeConvertedToLatest)
                {
                    baseName += " → .latest";
                }
                
                return baseName;
            }
        }

        public string IndentedName => new string(' ', Depth * 4) + (Depth > 0 ? "└─ " : "") + DisplayName;

        public string SubDependencyCountDisplay => SubDependencyCount > 0 ? SubDependencyCount.ToString() : "";

        protected virtual bool SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(backingStore, value))
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

