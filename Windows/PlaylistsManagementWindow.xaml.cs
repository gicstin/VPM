using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using VPM.Models;
using VPM.Services;

namespace VPM.Windows
{
    public partial class PlaylistsManagementWindow : Window
    {
        private sealed class PlaylistPackageItem
        {
            public string Key { get; init; }
            public string Display { get; init; }
        }

        private readonly ISettingsManager _settingsManager;
        private readonly PackageManager _packageManager;
        private readonly DependencyGraph _dependencyGraph;
        private readonly string _vamRootFolder;
        private readonly PackageFileManager _packageFileManager;
        private readonly VPM.Services.Vpb.ScanControlService _scanControl;
        private PlaylistManager _playlistManager;
        private ObservableCollection<Playlist> _playlists;
        private ICollectionView _playlistsView;
        private Playlist _currentPlaylist;
        private bool _hasUnsavedChanges = false;

        private ObservableCollection<PlaylistPackageItem> _playlistPackages;
        private ICollectionView _playlistPackagesView;
        private bool _sortPlaylistPackagesAlphabetical = false;

        private Point _playlistDragStart;
        private Playlist _draggedPlaylist;

        private AdornerLayer _playlistsAdornerLayer;
        private PlaylistDropIndicatorAdorner _dropIndicator;
        private int _dropInsertIndex = -1;

        public PlaylistsManagementWindow(ISettingsManager settingsManager, PackageManager packageManager = null, 
            DependencyGraph dependencyGraph = null, string vamRootFolder = null, PackageFileManager packageFileManager = null,
            VPM.Services.Vpb.ScanControlService scanControl = null)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _packageManager = packageManager;
            _dependencyGraph = dependencyGraph;
            _vamRootFolder = vamRootFolder;
            _packageFileManager = packageFileManager;
            _scanControl = scanControl;
            
            if (packageManager != null && dependencyGraph != null && vamRootFolder != null)
            {
                _playlistManager = new PlaylistManager(packageManager, dependencyGraph, vamRootFolder, packageFileManager, scanControl);
            }
            
            InitializeComponent();
            if (_scanControl?.IsWhitelistMode == true && UnloadOthersCheckBox != null)
                UnloadOthersCheckBox.Content = "Exclusive: replace scan set";
            LoadPlaylists();
            _hasUnsavedChanges = false;
            UpdateUIState();
        }

        private void LoadPlaylists()
        {
            var playlists = _settingsManager?.Settings?.Playlists ?? new List<Playlist>();
            _playlists = new ObservableCollection<Playlist>(playlists.OrderBy(p => p.SortOrder));
            _playlistsView = CollectionViewSource.GetDefaultView(_playlists);
            _playlistsView.Filter = FilterPlaylists;
            PlaylistsListBox.ItemsSource = _playlistsView;
        }

        private bool FilterPlaylists(object item)
        {
            if (string.IsNullOrWhiteSpace(PlaylistSearchBox.Text)) return true;
            var playlist = item as Playlist;
            return playlist != null && playlist.Name.IndexOf(PlaylistSearchBox.Text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void PlaylistSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _playlistsView?.Refresh();
        }

        private void PlaylistsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _currentPlaylist = PlaylistsListBox.SelectedItem as Playlist;
            UpdateUIState();
        }

        private void UpdateUIState()
        {
            if (_currentPlaylist != null)
            {
                PlaylistNameTextBox.IsEnabled = true;
                PlaylistDescriptionTextBox.IsEnabled = true;
                UnloadOthersCheckBox.IsEnabled = true;
                ActivatePlaylistBtn.IsEnabled = true;

                // Unsubscribe from change events to prevent marking as unsaved during UI update
                PlaylistNameTextBox.TextChanged -= PlaylistName_Changed;
                PlaylistDescriptionTextBox.TextChanged -= PlaylistDescription_Changed;
                UnloadOthersCheckBox.Checked -= UnloadOthers_Changed;
                UnloadOthersCheckBox.Unchecked -= UnloadOthers_Changed;

                // Only update text if it's different to avoid cursor jumping or loops
                if (PlaylistNameTextBox.Text != _currentPlaylist.Name)
                    PlaylistNameTextBox.Text = _currentPlaylist.Name;
                    
                if (PlaylistDescriptionTextBox.Text != _currentPlaylist.Description)
                    PlaylistDescriptionTextBox.Text = _currentPlaylist.Description;
                    
                UnloadOthersCheckBox.IsChecked = _currentPlaylist.UnloadOtherPackages;

                // Resubscribe to change events
                PlaylistNameTextBox.TextChanged += PlaylistName_Changed;
                PlaylistDescriptionTextBox.TextChanged += PlaylistDescription_Changed;
                UnloadOthersCheckBox.Checked += UnloadOthers_Changed;
                UnloadOthersCheckBox.Unchecked += UnloadOthers_Changed;
                
                RefreshPackagesList();
            }
            else
            {
                ClearDetails();
            }
        }

        private void RefreshPackagesList()
        {
            if (_currentPlaylist != null)
            {
                RebuildPlaylistPackagesViewModels();
            }
        }

        private bool FilterPlaylistPackages(object item)
        {
            string pkgKey;
            if (item is PlaylistPackageItem vm)
                pkgKey = vm.Key;
            else
                return false;

            var search = PlaylistPackagesSearchBox?.Text;
            if (string.IsNullOrWhiteSpace(search))
                return true;

            return pkgKey.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ApplyPlaylistPackagesSorting()
        {
            if (_playlistPackagesView == null)
                return;

            _playlistPackagesView.SortDescriptions.Clear();
            if (_sortPlaylistPackagesAlphabetical)
            {
                // Sort by display text when available; otherwise fall back to the raw key
                _playlistPackagesView.SortDescriptions.Add(new SortDescription(nameof(PlaylistPackageItem.Display), ListSortDirection.Ascending));
            }

            if (PlaylistPackagesSortToggleBtn != null)
            {
                PlaylistPackagesSortToggleBtn.Content = _sortPlaylistPackagesAlphabetical ? "Original" : "A–Z";
            }
        }

        private void UpdatePlaylistPackagesCount()
        {
            if (PackageCountTextBlock == null)
                return;

            if (_playlistPackagesView == null)
            {
                PackageCountTextBlock.Text = "0 items";
                return;
            }

            int total = _currentPlaylist?.PackageKeys?.Count ?? 0;
            int visible = _playlistPackagesView.Cast<object>().Count();

            PackageCountTextBlock.Text = visible == total
                ? $"{total} items"
                : $"{visible} / {total} items";

            if (_scanControl != null && _playlistManager != null && _currentPlaylist != null)
            {
                var closure = _playlistManager.GetAllPackagesToLoad(_currentPlaylist);
                long bytes = 0;
                int files = 0;
                foreach (var key in closure)
                {
                    if (_packageManager?.PackageMetadata != null && _packageManager.PackageMetadata.TryGetValue(key, out var meta) && meta != null)
                    {
                        bytes += meta.FileSize;
                        files += meta.FileCount;
                    }
                }
                var gb = bytes / (1024d * 1024d * 1024d);
                var size = gb >= 1 ? $"{gb:0.0} GB" : $"{bytes / (1024d * 1024d):0} MB";
                PackageCountTextBlock.Text += $"  ·  scan {closure.Count:N0} pkgs / {size} / {files:N0} files";
            }
        }

        private void ClearDetails()
        {
            PlaylistNameTextBox.IsEnabled = false;
            PlaylistDescriptionTextBox.IsEnabled = false;
            UnloadOthersCheckBox.IsEnabled = false;
            ActivatePlaylistBtn.IsEnabled = false;

            PlaylistNameTextBox.Text = "";
            PlaylistDescriptionTextBox.Text = "";
            UnloadOthersCheckBox.IsChecked = false;
            PlaylistPackagesListBox.ItemsSource = null;
            PackageCountTextBlock.Text = "0 items";

            if (PlaylistPackagesSearchBox != null)
                PlaylistPackagesSearchBox.Text = "";
            _sortPlaylistPackagesAlphabetical = false;
            ApplyPlaylistPackagesSorting();
        }

        private static string GetFriendlyPackageName(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "";

            // Common key format is Creator.PackageName.Version
            var parts = key.Split('.');
            if (parts.Length >= 2)
            {
                var creator = parts[0];
                var name = parts[1];
                if (!string.IsNullOrWhiteSpace(creator) && !string.IsNullOrWhiteSpace(name))
                    return $"{creator} - {name}";
            }

            return key;
        }

        private void RebuildPlaylistPackagesViewModels()
        {
            if (_currentPlaylist == null)
            {
                PlaylistPackagesListBox.ItemsSource = null;
                return;
            }

            var vms = _currentPlaylist.PackageKeys
                .Select(k => new PlaylistPackageItem { Key = k, Display = GetFriendlyPackageName(k) })
                .ToList();

            _playlistPackages = new ObservableCollection<PlaylistPackageItem>(vms);
            _playlistPackagesView = CollectionViewSource.GetDefaultView(_playlistPackages);
            _playlistPackagesView.Filter = FilterPlaylistPackages;
            ApplyPlaylistPackagesSorting();
            PlaylistPackagesListBox.ItemsSource = _playlistPackagesView;
            UpdatePlaylistPackagesCount();
        }

        private void PlaylistPackagesSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _playlistPackagesView?.Refresh();
            UpdatePlaylistPackagesCount();
        }

        private void PlaylistPackagesSortToggle_Click(object sender, RoutedEventArgs e)
        {
            _sortPlaylistPackagesAlphabetical = !_sortPlaylistPackagesAlphabetical;
            ApplyPlaylistPackagesSorting();
            _playlistPackagesView?.Refresh();
            UpdatePlaylistPackagesCount();
        }

        private void CopySelectedPlaylistPackageKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = PlaylistPackagesListBox?.SelectedItem;
                var key = selected is PlaylistPackageItem vm ? vm.Key : selected as string;
                if (string.IsNullOrWhiteSpace(key))
                    return;
                Clipboard.SetText(key);
            }
            catch
            {
            }
        }

        private void PlaylistsListBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _playlistDragStart = e.GetPosition(null);
            _draggedPlaylist = null;
        }

        private void PlaylistsListBox_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
                return;

            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _playlistDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _playlistDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            if (PlaylistsListBox?.SelectedItem is not Playlist selected)
                return;

            _draggedPlaylist = selected;
            DragDrop.DoDragDrop(PlaylistsListBox, selected, DragDropEffects.Move);
        }

        private void PlaylistsListBox_Drop(object sender, DragEventArgs e)
        {
            if (_draggedPlaylist == null)
                return;

            if (sender is not ListBox listBox)
                return;

            int oldIndex = _playlists.IndexOf(_draggedPlaylist);
            int newIndex = _dropInsertIndex;

            ClearDropIndicator();

            if (oldIndex < 0)
                return;

            if (newIndex < 0 || newIndex > _playlists.Count)
                return;

            // Adjust insert index after removing the dragged item
            if (newIndex > oldIndex)
                newIndex--;

            if (newIndex < 0 || newIndex >= _playlists.Count)
            {
                // If inserting at end, move to last
                newIndex = _playlists.Count - 1;
            }

            if (newIndex == oldIndex)
                return;

            _playlists.Move(oldIndex, newIndex);

            for (int i = 0; i < _playlists.Count; i++)
            {
                _playlists[i].SortOrder = i;
            }

            // Force UI refresh since Playlist doesn't raise property-changed for SortOrder
            _playlistsView?.Refresh();
            PlaylistsListBox?.Items?.Refresh();

            _hasUnsavedChanges = true;
            UpdateSaveButton();

            PlaylistsListBox.SelectedItem = _draggedPlaylist;
            PlaylistsListBox.ScrollIntoView(_draggedPlaylist);
        }

        private void PlaylistsListBox_DragLeave(object sender, DragEventArgs e)
        {
            ClearDropIndicator();
        }

        private void PlaylistsListBox_DragOver(object sender, DragEventArgs e)
        {
            if (_draggedPlaylist == null)
                return;

            if (sender is not ListBox listBox)
                return;

            var pos = e.GetPosition(listBox);
            var targetItem = GetListBoxItemUnderMouse(listBox, pos);
            if (targetItem == null)
            {
                // over empty area: drop at end
                _dropInsertIndex = _playlists.Count;
                ClearDropIndicator();
                return;
            }

            var playlist = targetItem.DataContext as Playlist;
            if (playlist == null)
                return;

            int targetIndex = _playlists.IndexOf(playlist);
            if (targetIndex < 0)
                return;

            bool insertBefore;
            var itemPos = e.GetPosition(targetItem);
            insertBefore = itemPos.Y < (targetItem.ActualHeight / 2.0);
            _dropInsertIndex = insertBefore ? targetIndex : targetIndex + 1;

            ShowDropIndicator(targetItem, insertBefore);
            e.Handled = true;
        }

        private static ListBoxItem GetListBoxItemUnderMouse(ListBox listBox, Point position)
        {
            var element = listBox.InputHitTest(position) as DependencyObject;
            while (element != null)
            {
                if (element is ListBoxItem lbi)
                    return lbi;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private void ShowDropIndicator(ListBoxItem item, bool insertBefore)
        {
            try
            {
                _playlistsAdornerLayer ??= AdornerLayer.GetAdornerLayer(item);
                if (_playlistsAdornerLayer == null)
                    return;

                if (_dropIndicator != null)
                {
                    _playlistsAdornerLayer.Remove(_dropIndicator);
                    _dropIndicator = null;
                }

                _dropIndicator = new PlaylistDropIndicatorAdorner(item, insertBefore);
                _playlistsAdornerLayer.Add(_dropIndicator);
            }
            catch
            {
            }
        }

        private void ClearDropIndicator()
        {
            try
            {
                if (_dropIndicator != null && _playlistsAdornerLayer != null)
                {
                    _playlistsAdornerLayer.Remove(_dropIndicator);
                }
            }
            catch
            {
            }
            finally
            {
                _dropIndicator = null;
                _playlistsAdornerLayer = null;
                _dropInsertIndex = -1;
            }
        }

        private sealed class PlaylistDropIndicatorAdorner : Adorner
        {
            private readonly bool _insertBefore;

            public PlaylistDropIndicatorAdorner(UIElement adornedElement, bool insertBefore)
                : base(adornedElement)
            {
                _insertBefore = insertBefore;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                base.OnRender(drawingContext);

                var rect = new Rect(AdornedElement.RenderSize);
                double y = _insertBefore ? 0 : rect.Height;

                var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)), 2);
                pen.Freeze();

                drawingContext.DrawLine(pen, new Point(0, y), new Point(rect.Width, y));
            }
        }

        private void NewPlaylist_Click(object sender, RoutedEventArgs e)
        {
            var newPlaylist = new Playlist
            {
                Name = $"Playlist {_playlists.Count + 1}",
                SortOrder = _playlists.Count
            };

            _playlists.Add(newPlaylist);
            _hasUnsavedChanges = true;
            UpdateSaveButton();
            
            // Clear search to show the new playlist
            if (!string.IsNullOrEmpty(PlaylistSearchBox.Text))
            {
                PlaylistSearchBox.Text = "";
            }
            
            PlaylistsListBox.SelectedItem = newPlaylist;
            PlaylistsListBox.ScrollIntoView(newPlaylist);
        }

        private void DeletePlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPlaylist == null)
                return;

            var result = DarkMessageBox.Show(
                $"Are you sure you want to delete the playlist \"{_currentPlaylist.Name}\"?",
                "Delete Playlist",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            _playlists.Remove(_currentPlaylist);

            for (int i = 0; i < _playlists.Count; i++)
            {
                _playlists[i].SortOrder = i;
            }

            // Force UI refresh since Playlist doesn't raise property-changed for SortOrder
            _playlistsView?.Refresh();
            PlaylistsListBox?.Items?.Refresh();

            _hasUnsavedChanges = true;
            UpdateSaveButton();
            // Selection change will handle clearing details
        }

        private void DeletePlaylistInline_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not Playlist playlist)
                return;

            var result = DarkMessageBox.Show(
                $"Are you sure you want to delete the playlist \"{playlist.Name}\"?",
                "Delete Playlist",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            bool wasSelected = ReferenceEquals(_currentPlaylist, playlist);
            _playlists.Remove(playlist);

            for (int i = 0; i < _playlists.Count; i++)
            {
                _playlists[i].SortOrder = i;
            }

            // Force UI refresh since Playlist doesn't raise property-changed for SortOrder
            _playlistsView?.Refresh();
            PlaylistsListBox?.Items?.Refresh();

            _hasUnsavedChanges = true;
            UpdateSaveButton();

            if (wasSelected)
            {
                _currentPlaylist = null;
                PlaylistsListBox.SelectedItem = null;
                UpdateUIState();
            }
        }

        private void PlaylistName_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_currentPlaylist != null && _currentPlaylist.Name != PlaylistNameTextBox.Text)
            {
                _currentPlaylist.Name = PlaylistNameTextBox.Text;
                _hasUnsavedChanges = true;
                UpdateSaveButton();
                
                // Refresh list to show new name
                _playlistsView.Refresh();
            }
        }

        private void PlaylistDescription_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_currentPlaylist != null && _currentPlaylist.Description != PlaylistDescriptionTextBox.Text)
            {
                _currentPlaylist.Description = PlaylistDescriptionTextBox.Text;
                _hasUnsavedChanges = true;
                UpdateSaveButton();
            }
        }

        private void UnloadOthers_Changed(object sender, RoutedEventArgs e)
        {
            if (_currentPlaylist != null)
            {
                _currentPlaylist.UnloadOtherPackages = UnloadOthersCheckBox.IsChecked ?? true;
                _hasUnsavedChanges = true;
                UpdateSaveButton();
            }
        }

        private async void ActivatePlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPlaylist == null)
                return;

            if (_hasUnsavedChanges)
            {
                var saveResult = DarkMessageBox.Show(
                    "You have unsaved changes.\n\nYes = Save & Activate\nNo = Activate Without Saving\nCancel = Go Back",
                    "Unsaved Changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (saveResult == MessageBoxResult.Cancel)
                    return;

                if (saveResult == MessageBoxResult.Yes)
                {
                    if (_settingsManager != null)
                    {
                        for (int i = 0; i < _playlists.Count; i++)
                        {
                            _playlists[i].SortOrder = i;
                        }

                        _settingsManager.Settings.Playlists = _playlists
                            .OrderBy(p => p?.SortOrder ?? int.MaxValue)
                            .ToList();
                        _settingsManager.SaveSettingsImmediate();
                    }
                    _hasUnsavedChanges = false;
                    UpdateSaveButton();
                }
            }

            var result = DarkMessageBox.Show(
                $"Activate playlist \"{_currentPlaylist.Name}\"?\n\n" +
                (_scanControl?.IsWhitelistMode == true
                    ? $"This writes the VaM scan set from {_currentPlaylist.PackageKeys.Count} packages and their dependencies."
                        + (_currentPlaylist.UnloadOtherPackages ? "\n\nExclusive: replaces the current scan set." : "\n\nAdditive: unions into the current scan set.")
                    : $"This will load {_currentPlaylist.PackageKeys.Count} packages and their dependencies."
                        + (_currentPlaylist.UnloadOtherPackages ? "\n\nPackages not in this playlist will be unloaded." : "")),
                "Activate Playlist",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            ActivatePlaylistBtn.IsEnabled = false;
            try
            {
                // Show busy UI while activation runs
                if (MainContentGrid != null)
                    MainContentGrid.IsEnabled = false;
                if (ActivationBusyOverlay != null)
                    ActivationBusyOverlay.Visibility = Visibility.Visible;

                // Let the UI render the overlay before doing the heavy work
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                var activationResult = await ActivatePlaylistAsync(_currentPlaylist);

                var unloadText = _currentPlaylist.UnloadOtherPackages
                    ? (_scanControl?.IsWhitelistMode == true ? "Exclusive: Yes" : "Unload others: Yes")
                    : (_scanControl?.IsWhitelistMode == true ? "Exclusive: No" : "Unload others: No");
                var details = _scanControl?.IsWhitelistMode == true
                    ? $"{activationResult.Message}\n{unloadText}"
                    : $"Loaded: {activationResult.LoadedCount:N0}\nUnloaded: {activationResult.UnloadedCount:N0}\n{unloadText}";

                DarkMessageBox.Show(
                    $"Playlist activated successfully!\n\n{details}",
                    "Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Close();
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show($"Error activating playlist: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ActivatePlaylistBtn.IsEnabled = true;

                if (ActivationBusyOverlay != null)
                    ActivationBusyOverlay.Visibility = Visibility.Collapsed;
                if (MainContentGrid != null)
                    MainContentGrid.IsEnabled = true;
            }
        }

        private async System.Threading.Tasks.Task<PlaylistActivationResult> ActivatePlaylistAsync(Playlist playlist)
        {
            if (_playlistManager == null)
            {
                throw new InvalidOperationException("PlaylistManager is not initialized. Cannot activate playlist without package manager access.");
            }

            var progress = new Progress<PlaylistActivationProgress>(p =>
            {
                try
                {
                    if (ActivationBusyStatusText != null)
                    {
                        var phase = string.IsNullOrWhiteSpace(p?.Phase) ? "Working" : p.Phase;
                        var pkg = string.IsNullOrWhiteSpace(p?.CurrentPackageKey) ? "" : $"\n{p.CurrentPackageKey}";
                        ActivationBusyStatusText.Text = $"{phase}...{pkg}";
                    }

                    if (ActivationBusyProgressText != null)
                    {
                        var total = p?.Total ?? 0;
                        var completed = p?.Completed ?? 0;
                        ActivationBusyProgressText.Text = $"{completed:N0} / {total:N0}";
                    }

                    if (ActivationBusyProgressBar != null)
                    {
                        var total = p?.Total ?? 0;
                        var completed = p?.Completed ?? 0;
                        if (total > 0)
                        {
                            ActivationBusyProgressBar.IsIndeterminate = false;
                            ActivationBusyProgressBar.Minimum = 0;
                            ActivationBusyProgressBar.Maximum = total;
                            ActivationBusyProgressBar.Value = Math.Max(0, Math.Min(total, completed));
                        }
                        else
                        {
                            ActivationBusyProgressBar.IsIndeterminate = true;
                        }
                    }
                }
                catch
                {
                }
            });

            var result = await _playlistManager.ActivatePlaylistAsync(playlist, playlist.UnloadOtherPackages, progress);
            
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
            }

            return result;
        }

        private void SavePlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsManager != null)
            {
                for (int i = 0; i < _playlists.Count; i++)
                {
                    _playlists[i].SortOrder = i;
                }

                _settingsManager.Settings.Playlists = _playlists
                    .OrderBy(p => p?.SortOrder ?? int.MaxValue)
                    .ToList();
                _settingsManager.SaveSettingsImmediate();
            }
            _hasUnsavedChanges = false;
            UpdateSaveButton();
        }

        private void UpdateSaveButton()
        {
            SavePlaylistBtn.IsEnabled = _hasUnsavedChanges;
        }

        private void RemovePackage_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            string packageKey = null;
            if (button?.DataContext is PlaylistPackageItem vm)
                packageKey = vm.Key;
            else if (button?.DataContext is string s)
                packageKey = s;

            if (!string.IsNullOrWhiteSpace(packageKey) && _currentPlaylist != null)
            {
                _currentPlaylist.PackageKeys.Remove(packageKey);
                _hasUnsavedChanges = true;
                UpdateSaveButton();
                RebuildPlaylistPackagesViewModels();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_hasUnsavedChanges)
            {
                var result = DarkMessageBox.Show(
                    "You have unsaved changes. Do you want to save before closing?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    SavePlaylist_Click(null, null);
                }
                else if (result == MessageBoxResult.Cancel)
                {
                    return;
                }
            }

            Close();
        }
    }
}
