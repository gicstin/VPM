using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;

namespace VPM
{
    public partial class BatchSubfolderSelectionWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private readonly ObservableCollection<PackageSelectionGroup> _packageGroups = new ObservableCollection<PackageSelectionGroup>();

        public Dictionary<string, string> SelectedFilesToKeep { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public BatchSubfolderSelectionWindow(Dictionary<string, List<string>> packageInstances)
        {
            InitializeComponent();
            ApplyDarkTitleBar();
            LoadPackageGroups(packageInstances ?? new Dictionary<string, List<string>>());
        }

        private void ApplyDarkTitleBar()
        {
            Loaded += (s, e) =>
            {
                try
                {
                    var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (handle == IntPtr.Zero)
                        return;

                    int darkMode = 1;
                    if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int)) != 0)
                        DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
                }
                catch { }
            };
        }

        private void LoadPackageGroups(Dictionary<string, List<string>> packageInstances)
        {
            _packageGroups.Clear();

            foreach (var entry in packageInstances.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                var instances = new ObservableCollection<PackageVersionItem>();
                foreach (var filePath in entry.Value.OrderByDescending(GetLastWriteTimeSafe))
                {
                    var item = CreateVersionItem(filePath);
                    if (item != null)
                        instances.Add(item);
                }

                if (instances.Count <= 1)
                    continue;

                _packageGroups.Add(new PackageSelectionGroup
                {
                    PackageName = entry.Key,
                    Instances = instances,
                    SelectedInstance = instances[0]
                });
            }

            PackageGroupsItemsControl.ItemsSource = _packageGroups;
            UpdateStatusText();
        }

        private static PackageVersionItem CreateVersionItem(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists)
                    return null;

                return new PackageVersionItem
                {
                    FullPath = filePath,
                    RelativeLocation = GetRelativePath(filePath),
                    FileSize = fileInfo.Length,
                    FileSizeFormatted = FormatFileSize(fileInfo.Length),
                    ModifiedDate = fileInfo.LastWriteTime,
                    ModifiedDateFormatted = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                };
            }
            catch
            {
                return null;
            }
        }

        private static DateTime GetLastWriteTimeSafe(string filePath)
        {
            try
            {
                return new FileInfo(filePath).LastWriteTime;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static string GetRelativePath(string fullPath)
        {
            try
            {
                var pathParts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                for (int i = 0; i < pathParts.Length; i++)
                {
                    if (pathParts[i].Equals("AddonPackages", StringComparison.OrdinalIgnoreCase) ||
                        pathParts[i].Equals("AllPackages", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i < pathParts.Length - 1)
                            return string.Join(Path.DirectorySeparatorChar.ToString(), pathParts.Skip(i));
                        break;
                    }
                }

                return Path.GetFileName(fullPath);
            }
            catch
            {
                return Path.GetFileName(fullPath);
            }
        }

        private static string FormatFileSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB" };
            int counter = 0;
            decimal number = bytes;

            while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
            {
                number /= 1024;
                counter++;
            }

            return $"{number:n1} {suffixes[counter]}";
        }

        private void UpdateStatusText()
        {
            StatusText.Text = $"{_packageGroups.Count} package(s) need a copy selected. Newest copy pre-selected for each.";
        }

        private void KeepSelected_Click(object sender, RoutedEventArgs e)
        {
            SelectedFilesToKeep.Clear();

            var missingSelections = new List<string>();
            foreach (var group in _packageGroups)
            {
                if (group.SelectedInstance == null || string.IsNullOrEmpty(group.SelectedInstance.FullPath))
                {
                    missingSelections.Add(group.PackageName);
                    continue;
                }

                SelectedFilesToKeep[group.PackageName] = group.SelectedInstance.FullPath;
            }

            if (missingSelections.Count > 0)
            {
                DarkMessageBox.Show(
                    $"Select a copy to keep for:\n{string.Join("\n", missingSelections.Take(10))}" +
                    (missingSelections.Count > 10 ? $"\n... and {missingSelections.Count - 10} more" : string.Empty),
                    "Selection Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public class PackageSelectionGroup : INotifyPropertyChanged
    {
        private PackageVersionItem _selectedInstance;

        public string PackageName { get; set; } = string.Empty;
        public ObservableCollection<PackageVersionItem> Instances { get; set; } = new ObservableCollection<PackageVersionItem>();

        public PackageVersionItem SelectedInstance
        {
            get => _selectedInstance;
            set
            {
                if (_selectedInstance != value)
                {
                    _selectedInstance = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
