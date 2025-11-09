using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VPM
{
    public partial class MainWindow
    {
        #region Drag Selection and Drag Check for Optimize Window

        private bool _optimizeWindowDragging = false;
        private Point _optimizeWindowDragStartPoint;
        private DataGridRow _optimizeWindowDragStartRow;
        private CheckBox _optimizeWindowDragStartCheckbox;
        private bool? _optimizeWindowDragCheckState;
        private int _optimizeWindowDragColumnIndex = -1;
        private Border _optimizeWindowDragStartBubble;
        private System.Windows.Threading.DispatcherTimer _optimizeWindowAutoScrollTimer;
        private DataGrid _optimizeWindowAutoScrollDataGrid;
        private Point _optimizeWindowLastMousePoint;

        private void OptimizeDataGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                var dataGrid = sender as DataGrid;
                var hitTest = VisualTreeHelper.HitTest(dataGrid, e.GetPosition(dataGrid));
                
                var bubble = FindBubbleBorder(hitTest?.VisualHit as DependencyObject);
                if (bubble != null)
                {
                    var cell = FindParent<DataGridCell>(bubble);
                    if (cell != null && cell.Column != null)
                    {
                        _optimizeWindowDragColumnIndex = dataGrid.Columns.IndexOf(cell.Column);
                        _optimizeWindowDragStartBubble = bubble;
                        _optimizeWindowDragStartPoint = e.GetPosition(dataGrid);
                        _optimizeWindowDragStartRow = FindParent<DataGridRow>(bubble);
                        _optimizeWindowDragging = false;
                        
                        var ellipse = FindEllipseInBorder(bubble);
                        bool currentBubbleState = ellipse?.Fill != Brushes.Transparent;
                        _optimizeWindowDragCheckState = !currentBubbleState;
                        
                        InitializeAutoScrollTimer(dataGrid);
                        return;
                    }
                }
                
                var checkbox = FindParent<CheckBox>(hitTest?.VisualHit as DependencyObject);
                if (checkbox != null && checkbox.IsEnabled)
                {
                    _optimizeWindowDragStartCheckbox = checkbox;
                    bool currentCheckboxState = checkbox.IsChecked == true;
                    _optimizeWindowDragCheckState = !currentCheckboxState;
                    
                    // Don't toggle here - let the CheckBox handle single-click naturally
                    // Only toggle during drag operations in PreviewMouseMove
                    
                    _optimizeWindowDragStartPoint = e.GetPosition(dataGrid);
                    _optimizeWindowDragStartRow = FindParent<DataGridRow>(hitTest?.VisualHit as DependencyObject);
                    _optimizeWindowDragging = false;
                    InitializeAutoScrollTimer(dataGrid);
                    return;
                }
                
                _optimizeWindowDragStartCheckbox = null;
                _optimizeWindowDragCheckState = null;
                _optimizeWindowDragStartRow = null;
                _optimizeWindowDragStartBubble = null;
                _optimizeWindowDragColumnIndex = -1;
            }
        }
        
        private Border FindBubbleBorder(DependencyObject obj)
        {
            if (obj == null) return null;
            
            if (obj is System.Windows.Shapes.Ellipse)
            {
                var parent = VisualTreeHelper.GetParent(obj);
                if (parent is Border border && border.Cursor == System.Windows.Input.Cursors.Hand)
                {
                    return border;
                }
            }
            
            if (obj is Border border2 && border2.Cursor == System.Windows.Input.Cursors.Hand)
            {
                if (FindEllipseInBorder(border2) != null)
                {
                    return border2;
                }
            }
            
            return null;
        }
        
        private System.Windows.Shapes.Ellipse FindEllipseInBorder(Border border)
        {
            if (border == null) return null;
            
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(border); i++)
            {
                var child = VisualTreeHelper.GetChild(border, i);
                if (child is System.Windows.Shapes.Ellipse ellipse)
                    return ellipse;
            }
            return null;
        }

        private void OptimizeDataGrid_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _optimizeWindowDragging = false;
                _optimizeWindowDragStartRow = null;
                _optimizeWindowDragStartCheckbox = null;
                _optimizeWindowDragCheckState = null;
                _optimizeWindowDragStartBubble = null;
                _optimizeWindowDragColumnIndex = -1;
                
                if (_optimizeWindowAutoScrollTimer != null)
                {
                    _optimizeWindowAutoScrollTimer.Stop();
                    _optimizeWindowAutoScrollTimer = null;
                }
                _optimizeWindowAutoScrollDataGrid = null;
            }
        }

        private void OptimizeDataGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            var dataGrid = sender as DataGrid;
            var currentPoint = e.GetPosition(dataGrid);
            _optimizeWindowLastMousePoint = currentPoint;
            
            if (e.LeftButton == MouseButtonState.Pressed && _optimizeWindowDragStartRow != null && 
                (_optimizeWindowDragStartBubble != null || _optimizeWindowDragStartCheckbox != null))
            {
                
                if (Math.Abs(currentPoint.X - _optimizeWindowDragStartPoint.X) > 3 || 
                    Math.Abs(currentPoint.Y - _optimizeWindowDragStartPoint.Y) > 3)
                {
                    if (!_optimizeWindowDragging)
                    {
                        _optimizeWindowDragging = true;
                        
                        // Apply state to the starting row when drag begins
                        if (_optimizeWindowDragStartBubble != null && _optimizeWindowDragColumnIndex >= 0)
                        {
                            var startBubble = FindBubbleInRowAtColumn(_optimizeWindowDragStartRow, dataGrid, _optimizeWindowDragColumnIndex);
                            if (startBubble != null)
                            {
                                var ellipse = FindEllipseInBorder(startBubble);
                                if (ellipse != null)
                                {
                                    bool currentState = ellipse.Fill != Brushes.Transparent;
                                    
                                    if (currentState != _optimizeWindowDragCheckState)
                                    {
                                        startBubble.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                                            Mouse.PrimaryDevice, 0, MouseButton.Left)
                                        {
                                            RoutedEvent = Border.MouseLeftButtonDownEvent
                                        });
                                    }
                                }
                            }
                        }
                        else if (_optimizeWindowDragStartCheckbox != null)
                        {
                            var startCheckbox = FindCheckboxInRow(_optimizeWindowDragStartRow);
                            
                            if (startCheckbox != null && startCheckbox.IsEnabled && 
                                startCheckbox.IsChecked != _optimizeWindowDragCheckState)
                            {
                                startCheckbox.IsChecked = _optimizeWindowDragCheckState;
                            }
                        }
                    }
                    
                    var hitTest = VisualTreeHelper.HitTest(dataGrid, currentPoint);
                    var currentRow = FindParent<DataGridRow>(hitTest?.VisualHit as DependencyObject);
                    
                    if (currentRow != null && currentRow != _optimizeWindowDragStartRow)
                    {
                        if (_optimizeWindowDragStartBubble != null && _optimizeWindowDragColumnIndex >= 0)
                        {
                            var bubble = FindBubbleInRowAtColumn(currentRow, dataGrid, _optimizeWindowDragColumnIndex);
                            if (bubble != null)
                            {
                                var ellipse = FindEllipseInBorder(bubble);
                                if (ellipse != null)
                                {
                                    bool currentState = ellipse.Fill != Brushes.Transparent;
                                    
                                    if (currentState != _optimizeWindowDragCheckState)
                                    {
                                        bubble.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                                            Mouse.PrimaryDevice, 0, MouseButton.Left)
                                        {
                                            RoutedEvent = Border.MouseLeftButtonDownEvent
                                        });
                                    }
                                }
                            }
                        }
                        else if (_optimizeWindowDragStartCheckbox != null)
                        {
                            var currentCheckbox = FindCheckboxInRow(currentRow);
                            
                            if (currentCheckbox != null && currentCheckbox.IsEnabled && 
                                currentCheckbox.IsChecked != _optimizeWindowDragCheckState)
                            {
                                currentCheckbox.IsChecked = _optimizeWindowDragCheckState;
                            }
                        }
                    }
                }
            }
        }
        
        private Border FindBubbleInRowAtColumn(DataGridRow row, DataGrid dataGrid, int columnIndex)
        {
            if (row == null || columnIndex < 0 || columnIndex >= dataGrid.Columns.Count) return null;
            
            try
            {
                var cellsPresenter = FindVisualChild<DataGridCellsPresenter>(row);
                if (cellsPresenter != null)
                {
                    var cell = cellsPresenter.ItemContainerGenerator.ContainerFromIndex(columnIndex) as DataGridCell;
                    if (cell != null)
                    {
                        return FindBubbleBorderInVisualTree(cell);
                    }
                }
            }
            catch
            {
            }
            
            return null;
        }
        
        private Border FindBubbleBorderInVisualTree(DependencyObject obj)
        {
            if (obj == null) return null;
            
            if (obj is Border border && border.Cursor == System.Windows.Input.Cursors.Hand)
            {
                if (FindEllipseInBorder(border) != null)
                    return border;
            }
            
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = VisualTreeHelper.GetChild(obj, i);
                var result = FindBubbleBorderInVisualTree(child);
                if (result != null) return result;
            }
            
            return null;
        }
        
        private T FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
        {
            if (obj == null) return null;
            
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = VisualTreeHelper.GetChild(obj, i);
                if (child is T typedChild)
                    return typedChild;
                
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            
            return null;
        }

        private void SelectRowsBetween(DataGrid dataGrid, DataGridRow startRow, DataGridRow endRow)
        {
            try
            {
                var startIndex = dataGrid.ItemContainerGenerator.IndexFromContainer(startRow);
                var endIndex = dataGrid.ItemContainerGenerator.IndexFromContainer(endRow);
                
                if (startIndex == -1 || endIndex == -1) return;
                
                if (startIndex > endIndex)
                {
                    var temp = startIndex;
                    startIndex = endIndex;
                    endIndex = temp;
                }
                
                if (!Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl))
                {
                    dataGrid.SelectedItems.Clear();
                }
                
                for (int i = startIndex; i <= endIndex; i++)
                {
                    var item = dataGrid.Items[i];
                    if (item != null && !dataGrid.SelectedItems.Contains(item))
                    {
                        dataGrid.SelectedItems.Add(item);
                    }
                }
            }
            catch
            {
            }
        }

        private CheckBox FindCheckboxInRow(DataGridRow row)
        {
            if (row == null) return null;
            
            try
            {
                var checkboxes = new List<CheckBox>();
                CollectCheckboxesInVisualTree(row, checkboxes);
                
                if (_optimizeWindowDragStartCheckbox != null && checkboxes.Count > 0)
                {
                    var startCheckboxColumn = GetCheckboxColumnIndex(_optimizeWindowDragStartCheckbox);
                    
                    foreach (var cb in checkboxes)
                    {
                        var cbColumn = GetCheckboxColumnIndex(cb);
                        if (cbColumn == startCheckboxColumn)
                        {
                            return cb;
                        }
                    }
                }
                
                return checkboxes.FirstOrDefault();
            }
            catch
            {
            }
            
            return null;
        }
        
        private int GetCheckboxColumnIndex(CheckBox checkbox)
        {
            try
            {
                var cell = FindParent<DataGridCell>(checkbox);
                if (cell != null && cell.Column != null)
                {
                    var dataGrid = FindParent<DataGrid>(cell);
                    if (dataGrid != null)
                    {
                        return dataGrid.Columns.IndexOf(cell.Column);
                    }
                }
            }
            catch
            {
            }
            return -1;
        }
        
        private void CollectCheckboxesInVisualTree(DependencyObject obj, List<CheckBox> checkboxes)
        {
            if (obj == null) return;
            
            if (obj is CheckBox checkbox)
            {
                checkboxes.Add(checkbox);
            }
            
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = VisualTreeHelper.GetChild(obj, i);
                CollectCheckboxesInVisualTree(child, checkboxes);
            }
        }

        private CheckBox FindCheckboxInVisualTree(DependencyObject obj)
        {
            if (obj == null) return null;
            
            if (obj is CheckBox checkbox)
                return checkbox;
            
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = VisualTreeHelper.GetChild(obj, i);
                var result = FindCheckboxInVisualTree(child);
                if (result != null) return result;
            }
            
            return null;
        }

        private void InitializeAutoScrollTimer(DataGrid dataGrid)
        {
            if (_optimizeWindowAutoScrollTimer == null)
            {
                _optimizeWindowAutoScrollTimer = new System.Windows.Threading.DispatcherTimer();
                _optimizeWindowAutoScrollTimer.Interval = TimeSpan.FromMilliseconds(50);
                _optimizeWindowAutoScrollTimer.Tick += (s, e) => OptimizeDataGrid_AutoScroll();
                _optimizeWindowAutoScrollTimer.Start();
            }
            _optimizeWindowAutoScrollDataGrid = dataGrid;
        }

        private void OptimizeDataGrid_AutoScroll()
        {
            if (_optimizeWindowAutoScrollDataGrid == null || !_optimizeWindowDragging) return;
            
            var dataGrid = _optimizeWindowAutoScrollDataGrid;
            var scrollViewer = FindVisualChild<ScrollViewer>(dataGrid);
            if (scrollViewer == null) return;
            
            double scrollMargin = 40;
            double scrollSpeed = 2;
            
            bool scrolled = false;
            
            if (_optimizeWindowLastMousePoint.Y < scrollMargin && scrollViewer.VerticalOffset > 0)
            {
                scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset - scrollSpeed));
                scrolled = true;
            }
            else if (_optimizeWindowLastMousePoint.Y > dataGrid.ActualHeight - scrollMargin && 
                     scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight)
            {
                scrollViewer.ScrollToVerticalOffset(Math.Min(scrollViewer.ScrollableHeight, scrollViewer.VerticalOffset + scrollSpeed));
                scrolled = true;
            }
            
            if (scrolled)
            {
                HandleAutoScrollSelection(dataGrid);
            }
        }

        private void HandleAutoScrollSelection(DataGrid dataGrid)
        {
            if (_optimizeWindowDragging && _optimizeWindowDragStartRow != null && dataGrid.Items.Count > 0)
            {
                try
                {
                    int startIndex = dataGrid.ItemContainerGenerator.IndexFromContainer(_optimizeWindowDragStartRow);
                    if (startIndex == -1) return;
                    
                    var hitTest = VisualTreeHelper.HitTest(dataGrid, _optimizeWindowLastMousePoint);
                    var currentRow = FindParent<DataGridRow>(hitTest?.VisualHit as DependencyObject);
                    
                    if (currentRow == null)
                    {
                        return;
                    }
                    
                    int currentIndex = dataGrid.ItemContainerGenerator.IndexFromContainer(currentRow);
                    if (currentIndex == -1) return;
                    
                    int minIndex = Math.Min(startIndex, currentIndex);
                    int maxIndex = Math.Max(startIndex, currentIndex);
                    
                    if (_optimizeWindowDragStartBubble != null && _optimizeWindowDragColumnIndex >= 0)
                    {
                        if (_optimizeWindowDragCheckState == false)
                        {
                            if (!CanUncheckBubbleColumn(dataGrid, _optimizeWindowDragColumnIndex, minIndex, maxIndex))
                                return;
                        }
                        
                        for (int i = minIndex; i <= maxIndex; i++)
                        {
                            var row = dataGrid.ItemContainerGenerator.ContainerFromIndex(i) as DataGridRow;
                            if (row == null) continue;
                            
                            var bubble = FindBubbleInRowAtColumn(row, dataGrid, _optimizeWindowDragColumnIndex);
                            if (bubble != null)
                            {
                                var ellipse = FindEllipseInBorder(bubble);
                                if (ellipse != null)
                                {
                                    bool currentState = ellipse.Fill != Brushes.Transparent;
                                    
                                    if (currentState != _optimizeWindowDragCheckState)
                                    {
                                        bubble.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                                            Mouse.PrimaryDevice, 0, MouseButton.Left)
                                        {
                                            RoutedEvent = Border.MouseLeftButtonDownEvent
                                        });
                                    }
                                }
                            }
                        }
                    }
                    else if (_optimizeWindowDragStartCheckbox != null)
                    {
                        if (_optimizeWindowDragCheckState == false)
                        {
                            if (!CanUncheckCheckboxColumn(dataGrid, minIndex, maxIndex))
                                return;
                        }
                        
                        for (int i = minIndex; i <= maxIndex; i++)
                        {
                            var row = dataGrid.ItemContainerGenerator.ContainerFromIndex(i) as DataGridRow;
                            if (row == null) continue;
                            
                            var checkbox = FindCheckboxInRow(row);
                            
                            if (checkbox != null && checkbox.IsEnabled && 
                                checkbox.IsChecked != _optimizeWindowDragCheckState)
                            {
                                checkbox.IsChecked = _optimizeWindowDragCheckState;
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private bool CanUncheckBubbleColumn(DataGrid dataGrid, int columnIndex, int minIndex, int maxIndex)
        {
            for (int i = 0; i < dataGrid.Items.Count; i++)
            {
                if (i >= minIndex && i <= maxIndex) continue;
                
                var row = dataGrid.ItemContainerGenerator.ContainerFromIndex(i) as DataGridRow;
                if (row == null) continue;
                
                var bubble = FindBubbleInRowAtColumn(row, dataGrid, columnIndex);
                if (bubble != null)
                {
                    var ellipse = FindEllipseInBorder(bubble);
                    if (ellipse != null && ellipse.Fill != Brushes.Transparent)
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }

        private bool CanUncheckCheckboxColumn(DataGrid dataGrid, int minIndex, int maxIndex)
        {
            for (int i = 0; i < dataGrid.Items.Count; i++)
            {
                if (i >= minIndex && i <= maxIndex) continue;
                
                var row = dataGrid.ItemContainerGenerator.ContainerFromIndex(i) as DataGridRow;
                if (row == null) continue;
                
                var checkbox = FindCheckboxInRow(row);
                if (checkbox != null && checkbox.IsChecked == true)
                {
                    return true;
                }
            }
            
            return false;
        }

        private void TextureDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var dataGrid = sender as DataGrid;
            if (dataGrid?.SelectedItem == null) return;

            try
            {
                var textureInfo = dataGrid.SelectedItem as Services.TextureValidator.TextureInfo;
                if (textureInfo == null) return;

                // Get the package path
                var pkgInfo = _packageFileManager?.GetPackageFileInfo(textureInfo.PackageName);
                if (pkgInfo == null || string.IsNullOrEmpty(pkgInfo.CurrentPath))
                {
                    MessageBox.Show($"Could not find package: {textureInfo.PackageName}", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                string packagePath = pkgInfo.CurrentPath;

                // Only support VAR files
                if (!packagePath.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Texture viewing is only supported for .var files.", 
                                  "Unsupported Format", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Extract texture to temp folder and open in default image viewer
                string tempFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VPM", "Textures");
                System.IO.Directory.CreateDirectory(tempFolder);

                // Create a unique temp file name
                string extension = System.IO.Path.GetExtension(textureInfo.ReferencedPath);
                string tempFileName = $"{textureInfo.PackageName}_{System.IO.Path.GetFileName(textureInfo.ReferencedPath)}";
                string tempFilePath = System.IO.Path.Combine(tempFolder, tempFileName);

                // Extract the texture from the VAR
                using (var archive = ZipFile.OpenRead(packagePath))
                {
                    var entry = archive.GetEntry(textureInfo.ReferencedPath);
                    if (entry == null)
                    {
                        MessageBox.Show($"Texture not found in package: {textureInfo.ReferencedPath}", "Error", 
                                      MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    // Extract to temp file manually
                    using (var entryStream = entry.Open())
                    using (var fileStream = File.Create(tempFilePath))
                    {
                        entryStream.CopyTo(fileStream);
                    }
                }

                // Open with default image viewer
                var processStartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tempFilePath,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(processStartInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening texture: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}

