using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace VPM
{
    public partial class SubfolderSelectionWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private ObservableCollection<PackageVersionItem> _packageVersions;
        public string SelectedFilePath { get; private set; }

        public SubfolderSelectionWindow(string packageName, List<string> filePaths)
        {
            InitializeComponent();
            
            // Enable dark mode for title bar when window is loaded
            Loaded += (s, e) =>
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    int darkMode = 1;
                    // Try Windows 11/10 20H1+ attribute first, then fall back to older Windows 10 attribute
                    if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
                    }
                }
            };
            
            LoadPackageVersions(packageName, filePaths);
        }

        private void LoadPackageVersions(string packageName, List<string> filePaths)
        {
            _packageVersions = new ObservableCollection<PackageVersionItem>();
            
            TitleText.Text = $"Multiple Versions of '{packageName}' Found";
            
            foreach (var filePath in filePaths.OrderBy(f => f))
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Exists)
                    {
                        // Get relative path from the main folder (AddonPackages or AllPackages)
                        string relativePath = GetRelativePath(filePath);
                        
                        var versionItem = new PackageVersionItem
                        {
                            FullPath = filePath,
                            RelativeLocation = relativePath,
                            FileSize = fileInfo.Length,
                            FileSizeFormatted = FormatFileSize(fileInfo.Length),
                            ModifiedDate = fileInfo.LastWriteTime,
                            ModifiedDateFormatted = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                        };
                        
                        _packageVersions.Add(versionItem);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SubfolderSelection] Error processing {filePath}: {ex.Message}");
                }
            }
            
            VersionsDataGrid.ItemsSource = _packageVersions;
            UpdateStatusText();
            
            // Select the first item by default
            if (_packageVersions.Count > 0)
            {
                VersionsDataGrid.SelectedIndex = 0;
            }
        }
        
        private string GetRelativePath(string fullPath)
        {
            try
            {
                // Find the main folder (AddonPackages or AllPackages) in the path
                var pathParts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                
                int mainFolderIndex = -1;
                for (int i = 0; i < pathParts.Length; i++)
                {
                    if (pathParts[i].Equals("AddonPackages", StringComparison.OrdinalIgnoreCase) ||
                        pathParts[i].Equals("AllPackages", StringComparison.OrdinalIgnoreCase))
                    {
                        mainFolderIndex = i;
                        break;
                    }
                }
                
                if (mainFolderIndex >= 0 && mainFolderIndex < pathParts.Length - 1)
                {
                    // Return path relative to the main folder
                    return string.Join(Path.DirectorySeparatorChar.ToString(), pathParts.Skip(mainFolderIndex));
                }
                
                return Path.GetFileName(fullPath);
            }
            catch
            {
                return Path.GetFileName(fullPath);
            }
        }
        
        private string FormatFileSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB" };
            int counter = 0;
            decimal number = bytes;
            
            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }
            
            return $"{number:n1} {suffixes[counter]}";
        }
        
        private void UpdateStatusText()
        {
            StatusText.Text = $"Found {_packageVersions.Count} version(s). Select one to keep - others will be deleted.";
        }
        
        private void KeepSelected_Click(object sender, RoutedEventArgs e)
        {
            if (VersionsDataGrid.SelectedItem is PackageVersionItem selectedVersion)
            {
                SelectedFilePath = selectedVersion.FullPath;
                DialogResult = true;
                Close();
            }
            else
            {
                DarkMessageBox.Show("Please select a version to keep.", "No Selection", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
    
    public class PackageVersionItem
    {
        public string FullPath { get; set; }
        public string RelativeLocation { get; set; }
        public long FileSize { get; set; }
        public string FileSizeFormatted { get; set; }
        public DateTime ModifiedDate { get; set; }
        public string ModifiedDateFormatted { get; set; }
    }
}

