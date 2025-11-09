using System;
using System.IO;
using System.Windows;
using VPM.Models;

namespace VPM
{
    public partial class FirstLaunchSetup : Window
    {
        private string _selectedPath = null;

        public string SelectedGamePath => _selectedPath;

        public FirstLaunchSetup()
        {
            InitializeComponent();
            
            // Try to auto-detect game folder
            TryAutoDetectGameFolder();
        }

        /// <summary>
        /// Attempts to auto-detect if the application is inside a VaM game folder
        /// </summary>
        private void TryAutoDetectGameFolder()
        {
            try
            {
                // Get the directory where the application is running
                string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                
                // Check if we're in the game folder by looking for VaM.exe and AddonPackages folder
                string vamExePath = Path.Combine(appDirectory, "VaM.exe");
                string addonPackagesPath = Path.Combine(appDirectory, "AddonPackages");
                
                if (File.Exists(vamExePath) && Directory.Exists(addonPackagesPath))
                {
                    // We're inside the game folder!
                    _selectedPath = appDirectory;
                    
                    // Show the auto-detected panel
                    AutoDetectedPanel.Visibility = Visibility.Visible;
                    DetectedPathText.Text = $"📝 {_selectedPath}";
                    
                    // Update manual selection title
                    ManualSelectionTitle.Text = "✗ Or Choose a Different Folder";
                    
                    // Enable continue button
                    ContinueButton.IsEnabled = true;
                    StatusText.Text = "✓ Ready to continue with detected path";
                }
                else
                {
                    // Not in game folder, check parent directory as well
                    DirectoryInfo parentDir = Directory.GetParent(appDirectory);
                    if (parentDir != null)
                    {
                        string parentVamExe = Path.Combine(parentDir.FullName, "VaM.exe");
                        string parentAddonPackages = Path.Combine(parentDir.FullName, "AddonPackages");
                        
                        if (File.Exists(parentVamExe) && Directory.Exists(parentAddonPackages))
                        {
                            // Parent directory is the game folder
                            _selectedPath = parentDir.FullName;
                            
                            AutoDetectedPanel.Visibility = Visibility.Visible;
                            DetectedPathText.Text = $"📝 {_selectedPath}";
                            ManualSelectionTitle.Text = "✗ Or Choose a Different Folder";
                            
                            ContinueButton.IsEnabled = true;
                            StatusText.Text = "✓ Ready to continue with detected path";
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Auto-detection failed, user will need to select manually
            }
        }

        /// <summary>
        /// Validates if the selected path is a valid VaM game folder
        /// </summary>
        private bool ValidateGameFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return false;

            string vamExePath = Path.Combine(path, "VaM.exe");
            string addonPackagesPath = Path.Combine(path, "AddonPackages");

            return File.Exists(vamExePath) && Directory.Exists(addonPackagesPath);
        }

        private void UseDetectedPath_Click(object sender, RoutedEventArgs e)
        {
            // User confirmed the auto-detected path
            DialogResult = true;
            Close();
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            // Use FolderBrowserDialog (Windows Forms) for better folder selection
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select your VaM game folder (contains VaM.exe and AddonPackages)";
                dialog.ShowNewFolderButton = false;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    string selectedPath = dialog.SelectedPath;

                    if (ValidateGameFolder(selectedPath))
                    {
                        _selectedPath = selectedPath;

                        // Show selected path
                        SelectedPathBorder.Visibility = Visibility.Visible;
                        SelectedPathText.Text = selectedPath;

                        // Enable continue button
                        ContinueButton.IsEnabled = true;
                        StatusText.Text = "✓ Valid game folder selected";
                    }
                    else
                    {
                        MessageBox.Show(
                            "The selected folder doesn't appear to be a valid VaM game folder.\n\n" +
                            "Please select the folder that contains:\n" +
                            "• VaM.exe\n" +
                            "• AddonPackages folder",
                            "Invalid Game Folder",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);

                        StatusText.Text = "– Invalid folder selected. Please try again.";
                    }
                }
            }
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_selectedPath) && ValidateGameFolder(_selectedPath))
            {
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show(
                    "Please select a valid VaM game folder before continuing.",
                    "No Folder Selected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
    }
}

