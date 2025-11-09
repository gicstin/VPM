using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    public partial class MainWindow
    {
        private AutoInstallManager _autoInstallManager;

        private void InitializeAutoInstallManager()
        {
            if (!string.IsNullOrEmpty(_settingsManager.Settings.SelectedFolder))
            {
                _autoInstallManager = new AutoInstallManager(_settingsManager.Settings.SelectedFolder);
                _autoInstallManager.LoadAutoInstall();
                
                _autoInstallManager.AutoInstallChanged += OnAutoInstallChanged;
                
                if (_filterManager != null)
                {
                    _filterManager.AutoInstallManager = _autoInstallManager;
                }
                
                UpdateAutoInstallInPackages();
            }
        }

        private void OnAutoInstallChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateAutoInstallInPackages();
                
                if (_reactiveFilterManager != null)
                {
                    UpdateFilterCountsLive();
                }
            }));
        }

        private void UpdateAutoInstallInPackages()
        {
            if (_autoInstallManager == null) return;

            foreach (var package in Packages)
            {
                package.IsAutoInstall = _autoInstallManager.IsAutoInstall(package.Name);
            }
        }

        private void AutoInstallToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_autoInstallManager == null)
            {
                return;
            }

            var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>().ToList();
            if (selectedPackages.Count == 0)
            {
                return;
            }

            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                OpenAutoInstallFile();
                return;
            }

            ExecuteWithPreservedSelections(() =>
            {
                _autoInstallManager.AddAutoInstallBatch(selectedPackages.Select(p => p.Name));
                
                foreach (var package in selectedPackages)
                {
                    package.IsAutoInstall = true;
                }

                bool autoInstallFilterActive = _filterManager?.SelectedAutoInstallStatuses?.Count > 0;
                
                if (autoInstallFilterActive)
                {
                    RefreshFilterLists();
                    ApplyFilters();
                }
                else
                {
                    if (_reactiveFilterManager != null)
                    {
                        UpdateFilterCountsLive();
                    }
                }
            });
        }

        private void AutoInstallToggleButton_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_autoInstallManager == null)
            {
                return;
            }

            var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>().ToList();
            if (selectedPackages.Count == 0)
            {
                return;
            }

            ExecuteWithPreservedSelections(() =>
            {
                _autoInstallManager.RemoveAutoInstallBatch(selectedPackages.Select(p => p.Name));
                
                foreach (var package in selectedPackages)
                {
                    package.IsAutoInstall = false;
                }

                bool autoInstallFilterActive = _filterManager?.SelectedAutoInstallStatuses?.Count > 0;
                
                if (autoInstallFilterActive)
                {
                    RefreshFilterLists();
                    ApplyFilters();
                }
                else
                {
                    if (_reactiveFilterManager != null)
                    {
                        UpdateFilterCountsLive();
                    }
                }
            });

            e.Handled = true;
        }

        private void OpenAutoInstallFile()
        {
            try
            {
                string autoInstallPath = Path.Combine(_settingsManager.Settings.SelectedFolder, "Custom", "PluginData", "sfishere", "AutoInstall.txt");
                
                if (File.Exists(autoInstallPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select, \"{autoInstallPath}\"",
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception)
            {
            }
        }
    }
}

