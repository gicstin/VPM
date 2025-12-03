using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphShape.Controls;
using QuikGraph;
using VPM.Models;
using VPM.Services;

namespace VPM.Windows
{
    public partial class DependencyGraphWindow : Window
    {
        private readonly PackageManager _packageManager;
        private readonly PackageFileManager _packageFileManager;
        private readonly ImageManager _imageManager;
        private readonly string _rootPackageName;
        private readonly VarMetadata _rootMetadata;
        
        private int _maxDepth = 1;
        private string _viewMode = "Dependencies";
        private string _layoutAlgorithm = "Grid";
        private bool _isInitialized = false;
        
        // Zoom and pan
        private bool _isPanning = false;
        private Point _panStart;
        private double _panStartX, _panStartY;
        
        private PocGraph _graph;
        private readonly Dictionary<string, PocVertex> _vertices = new(StringComparer.OrdinalIgnoreCase);
        
        public DependencyGraphWindow(PackageManager packageManager, PackageFileManager packageFileManager, ImageManager imageManager, VarMetadata rootMetadata)
        {
            InitializeComponent();
            
            _packageManager = packageManager;
            _packageFileManager = packageFileManager;
            _imageManager = imageManager;
            _rootMetadata = rootMetadata;
            _rootPackageName = $"{rootMetadata.CreatorName}.{rootMetadata.PackageName}.{rootMetadata.Version}";
            
            PackageNameText.Text = _rootPackageName;
            
            Loaded += async (s, e) =>
            {
                _isInitialized = true;
                await BuildAndDisplayGraphAsync();
            };
        }
        
        private async Task BuildAndDisplayGraphAsync()
        {
            // Show loading overlay
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingText.Text = "Building dependency graph...";
            
            try
            {
                // Build graph data on background thread
                await Task.Run(() =>
                {
                    _vertices.Clear();
                    _graph = new PocGraph();
                    
                    // Build graph based on view mode
                    switch (_viewMode)
                    {
                        case "Dependencies":
                            BuildDependenciesGraph();
                            break;
                        case "Dependents":
                            BuildDependentsGraph();
                            break;
                        case "Both":
                            BuildBothGraph();
                            break;
                    }
                });
                
                // Update UI on main thread
                LoadingText.Text = "Rendering graph...";
                await Task.Delay(50); // Allow UI to update
                
                // Set graph and layout
                GraphLayout.Graph = _graph;
                
                if (_layoutAlgorithm == "Grid")
                {
                    // Use true grid layout with WrapPanel
                    ApplyGridLayout();
                }
                else
                {
                    // Use GraphShape layout with edges
                    ShowGraphShapeLayout();
                    GraphLayout.LayoutAlgorithmType = _layoutAlgorithm;
                    
                    // Trigger relayout
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            GraphLayout.Relayout();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Layout error: {ex.Message}");
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
                
                UpdateStats();
            }
            catch (Exception ex)
            {
                LoadingText.Text = $"Error: {ex.Message}";
                await Task.Delay(2000);
            }
            finally
            {
                // Hide loading overlay
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }
        
        #region Graph Building
        
        private void BuildDependenciesGraph()
        {
            var rootVertex = GetOrCreateVertex(_rootPackageName, "Root");
            _graph.AddVertex(rootVertex);
            AddDependencies(_rootPackageName, _rootMetadata, 1);
        }
        
        private void AddDependencies(string packageName, VarMetadata metadata, int depth)
        {
            if (depth > _maxDepth || metadata?.Dependencies == null) return;
            
            var sourceVertex = _vertices.GetValueOrDefault(packageName);
            if (sourceVertex == null) return;
            
            // Ensure source vertex is in the graph before adding edges
            if (!_graph.ContainsVertex(sourceVertex))
                _graph.AddVertex(sourceVertex);
            
            foreach (var dep in metadata.Dependencies)
            {
                if (string.IsNullOrEmpty(dep)) continue;
                
                var depName = NormalizeDependencyName(dep);
                var depMetadata = FindPackageMetadata(depName);
                var vertexType = depMetadata != null ? "Dependency" : "Missing";
                
                var targetVertex = GetOrCreateVertex(depName, vertexType);
                if (!_graph.ContainsVertex(targetVertex))
                    _graph.AddVertex(targetVertex);
                
                var edge = new PocEdge(sourceVertex, targetVertex);
                if (!_graph.ContainsEdge(edge))
                    _graph.AddEdge(edge);
                
                if (depMetadata != null && depth < _maxDepth)
                    AddDependencies(depName, depMetadata, depth + 1);
            }
        }
        
        private void BuildDependentsGraph()
        {
            var rootVertex = GetOrCreateVertex(_rootPackageName, "Root");
            _graph.AddVertex(rootVertex);
            AddDependents(_rootPackageName, 1);
        }
        
        private void AddDependents(string packageName, int depth)
        {
            if (depth > _maxDepth) return;
            
            var targetVertex = _vertices.GetValueOrDefault(packageName);
            if (targetVertex == null) return;
            
            var dependents = _packageManager.GetPackageDependents(packageName);
            foreach (var dependent in dependents)
            {
                var sourceVertex = GetOrCreateVertex(dependent, "Dependent");
                if (!_graph.ContainsVertex(sourceVertex))
                    _graph.AddVertex(sourceVertex);
                
                var edge = new PocEdge(sourceVertex, targetVertex);
                if (!_graph.ContainsEdge(edge))
                    _graph.AddEdge(edge);
                
                if (depth < _maxDepth)
                    AddDependents(dependent, depth + 1);
            }
        }
        
        private void BuildBothGraph()
        {
            var rootVertex = GetOrCreateVertex(_rootPackageName, "Root");
            _graph.AddVertex(rootVertex);
            
            // Add dependencies
            if (_rootMetadata?.Dependencies != null)
            {
                foreach (var dep in _rootMetadata.Dependencies)
                {
                    if (string.IsNullOrEmpty(dep)) continue;
                    var depName = NormalizeDependencyName(dep);
                    var depMetadata = FindPackageMetadata(depName);
                    var vertexType = depMetadata != null ? "Dependency" : "Missing";
                    
                    var targetVertex = GetOrCreateVertex(depName, vertexType);
                    if (!_graph.ContainsVertex(targetVertex))
                        _graph.AddVertex(targetVertex);
                    
                    var edge = new PocEdge(rootVertex, targetVertex);
                    if (!_graph.ContainsEdge(edge))
                        _graph.AddEdge(edge);
                }
            }
            
            // Add dependents
            var dependents = _packageManager.GetPackageDependents(_rootPackageName);
            foreach (var dependent in dependents)
            {
                var sourceVertex = GetOrCreateVertex(dependent, "Dependent");
                if (!_graph.ContainsVertex(sourceVertex))
                    _graph.AddVertex(sourceVertex);
                
                var edge = new PocEdge(sourceVertex, rootVertex);
                if (!_graph.ContainsEdge(edge))
                    _graph.AddEdge(edge);
            }
        }
        
        private PocVertex GetOrCreateVertex(string fullName, string vertexType)
        {
            if (_vertices.TryGetValue(fullName, out var existing))
                return existing;
            
            // Find preview image for this package
            var previewImagePath = GetPreviewImageForPackage(fullName);
            
            var vertex = new PocVertex(fullName, vertexType, previewImagePath);
            _vertices[fullName] = vertex;
            
            // Load image asynchronously if path is available
            if (!string.IsNullOrEmpty(previewImagePath))
            {
                LoadVertexImageAsync(vertex, previewImagePath);
            }
            
            return vertex;
        }
        
        /// <summary>
        /// Gets the first preview image path for a package from the ImageManager
        /// Handles .latest suffix and partial name matching for dependencies
        /// </summary>
        private string GetPreviewImageForPackage(string packageName)
        {
            try
            {
                if (_imageManager?.ImageIndex == null) return null;
                
                // 1. Try exact match first
                if (TryGetImageFromIndex(packageName, out var result))
                    return result;
                
                // 2. Handle .latest suffix - find the actual latest version
                if (packageName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                {
                    var baseName = packageName.Substring(0, packageName.Length - 7); // Remove ".latest"
                    
                    // Create a snapshot of keys to avoid "Collection was modified" exception
                    List<string> keySnapshot;
                    try
                    {
                        keySnapshot = _imageManager.ImageIndex.Keys.ToList();
                    }
                    catch
                    {
                        // If snapshot fails, return null to avoid crash
                        return null;
                    }
                    
                    // Find all matching packages and get the one with highest version
                    var matchingKey = keySnapshot
                        .Where(k => k.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(k => 
                        {
                            var parts = k.Split('.');
                            if (parts.Length >= 1 && int.TryParse(parts.Last(), out var ver))
                                return ver;
                            return 0;
                        })
                        .FirstOrDefault();
                    
                    if (matchingKey != null && TryGetImageFromIndex(matchingKey, out result))
                        return result;
                }
                
                // 3. Try prefix match (for cases where version differs)
                // Create a snapshot of keys to avoid "Collection was modified" exception
                List<string> prefixKeySnapshot;
                try
                {
                    prefixKeySnapshot = _imageManager.ImageIndex.Keys.ToList();
                }
                catch
                {
                    // If snapshot fails, return null to avoid crash
                    return null;
                }
                
                var prefixMatch = prefixKeySnapshot
                    .FirstOrDefault(k => k.StartsWith(packageName + ".", StringComparison.OrdinalIgnoreCase) ||
                                         packageName.StartsWith(k + ".", StringComparison.OrdinalIgnoreCase));
                
                if (prefixMatch != null && TryGetImageFromIndex(prefixMatch, out result))
                    return result;
                
                // 4. Also check PreviewImageIndex with same logic
                if (_imageManager.PreviewImageIndex != null)
                {
                    try
                    {
                        if (_imageManager.PreviewImageIndex.TryGetValue(packageName, out var previewLocations) &&
                            previewLocations != null && previewLocations.Count > 0)
                        {
                            var firstImage = previewLocations[0];
                            return $"{firstImage.VarFilePath}::{firstImage.InternalPath}";
                        }
                    }
                    catch
                    {
                        // Ignore errors from PreviewImageIndex access
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting preview image for {packageName}: {ex.Message}");
            }
            
            return null;
        }
        
        /// <summary>
        /// Helper to try getting image from ImageIndex
        /// </summary>
        private bool TryGetImageFromIndex(string key, out string result)
        {
            result = null;
            if (_imageManager?.ImageIndex != null && 
                _imageManager.ImageIndex.TryGetValue(key, out var locations) &&
                locations != null && locations.Count > 0)
            {
                var firstImage = locations[0];
                result = $"{firstImage.VarFilePath}::{firstImage.InternalPath}";
                return true;
            }
            return false;
        }
        
        /// <summary>
        /// Loads the preview image for a vertex asynchronously
        /// </summary>
        private async void LoadVertexImageAsync(PocVertex vertex, string imagePath)
        {
            try
            {
                var parts = imagePath.Split(new[] { "::" }, 2, StringSplitOptions.None);
                if (parts.Length != 2) return;
                
                var varPath = parts[0];
                var internalPath = parts[1];
                
                if (!File.Exists(varPath)) return;
                
                var bitmap = await _imageManager.LoadImageAsync(varPath, internalPath, 80, 80);
                if (bitmap != null)
                {
                    // Update on UI thread
                    await Dispatcher.InvokeAsync(() =>
                    {
                        vertex.PreviewImage = bitmap;
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading vertex image: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Helpers
        
        private string NormalizeDependencyName(string dep)
        {
            var name = dep;
            if (name.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                name = System.IO.Path.GetFileNameWithoutExtension(name);
            var colonIndex = name.IndexOf(':');
            if (colonIndex > 0) name = name.Substring(0, colonIndex);
            return name;
        }
        
        private VarMetadata FindPackageMetadata(string packageName)
        {
            foreach (var kvp in _packageManager.PackageMetadata)
            {
                var meta = kvp.Value;
                var fullName = $"{meta.CreatorName}.{meta.PackageName}.{meta.Version}";
                if (fullName.Equals(packageName, StringComparison.OrdinalIgnoreCase))
                    return meta;
            }
            
            foreach (var kvp in _packageManager.PackageMetadata)
            {
                var filename = System.IO.Path.GetFileNameWithoutExtension(kvp.Value.Filename);
                if (filename.Equals(packageName, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
            
            if (packageName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = packageName.Substring(0, packageName.Length - 7);
                VarMetadata latestVersion = null;
                int highestVersion = -1;
                foreach (var kvp in _packageManager.PackageMetadata)
                {
                    var meta = kvp.Value;
                    var metaBaseName = $"{meta.CreatorName}.{meta.PackageName}";
                    if (metaBaseName.Equals(baseName, StringComparison.OrdinalIgnoreCase) && meta.Version > highestVersion)
                    {
                        highestVersion = meta.Version;
                        latestVersion = meta;
                    }
                }
                return latestVersion;
            }
            return null;
        }
        
        private void UpdateStats()
        {
            if (_graph != null)
                StatsText.Text = $"Nodes: {_graph.VertexCount} │ Edges: {_graph.EdgeCount}";
        }
        
        /// <summary>
        /// Applies a true grid layout using WrapPanel - no edges, just nodes in a flowing grid
        /// </summary>
        private void ApplyGridLayout()
        {
            if (_graph == null || _graph.VertexCount == 0) return;
            
            // Hide GraphShape layout, show Grid layout
            GraphLayout.Visibility = Visibility.Collapsed;
            GridItemsControl.Visibility = Visibility.Visible;
            
            // Sort vertices: Root first, then by type, then alphabetically
            var sortedVertices = _graph.Vertices
                .OrderBy(v => v.VertexType == "Root" ? 0 : 
                              v.VertexType == "Dependency" ? 1 : 
                              v.VertexType == "Dependent" ? 2 : 3)
                .ThenBy(v => v.FullName)
                .ToList();
            
            // Bind to ItemsControl
            GridItemsControl.ItemsSource = sortedVertices;
        }
        
        /// <summary>
        /// Shows the GraphShape layout (for tree/hierarchical views with edges)
        /// </summary>
        private void ShowGraphShapeLayout()
        {
            // Hide Grid layout, show GraphShape layout
            GridItemsControl.Visibility = Visibility.Collapsed;
            GraphLayout.Visibility = Visibility.Visible;
        }
        
        #endregion
        
        #region Event Handlers
        
        private void LayoutComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            if (LayoutComboBox?.SelectedItem is ComboBoxItem item)
            {
                _layoutAlgorithm = item.Content.ToString();
                if (GraphLayout != null && _graph != null)
                {
                    if (_layoutAlgorithm == "Grid")
                    {
                        ApplyGridLayout();
                    }
                    else
                    {
                        ShowGraphShapeLayout();
                        GraphLayout.LayoutAlgorithmType = _layoutAlgorithm;
                        GraphLayout.Relayout();
                    }
                }
            }
        }
        
        private async void DepthComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            if (DepthComboBox?.SelectedItem is ComboBoxItem item && int.TryParse(item.Content.ToString(), out var depth))
            {
                _maxDepth = depth;
                await BuildAndDisplayGraphAsync();
            }
        }
        
        private async void ViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            if (ViewModeComboBox?.SelectedItem is ComboBoxItem item)
            {
                _viewMode = item.Content.ToString();
                await BuildAndDisplayGraphAsync();
            }
        }
        
        private async void Relayout_Click(object sender, RoutedEventArgs e)
        {
            await BuildAndDisplayGraphAsync();
        }
        
        #endregion
        
        #region Zoom and Pan
        
        private void GraphArea_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double zoomFactor = e.Delta > 0 ? 1.2 : 1 / 1.2;
            
            if (_layoutAlgorithm == "Grid")
            {
                var mousePos = e.GetPosition(GridItemsControl);
                double oldScale = GridZoomTransform.ScaleX;
                double newScale = oldScale * zoomFactor;
                newScale = Math.Max(0.15, Math.Min(4.0, newScale));
                
                double scaleDiff = newScale - oldScale;
                GridPanTransform.X -= mousePos.X * scaleDiff;
                GridPanTransform.Y -= mousePos.Y * scaleDiff;
                
                GridZoomTransform.ScaleX = newScale;
                GridZoomTransform.ScaleY = newScale;
            }
            else
            {
                var mousePos = e.GetPosition(GraphLayout);
                double oldScale = ZoomTransform.ScaleX;
                double newScale = oldScale * zoomFactor;
                newScale = Math.Max(0.15, Math.Min(4.0, newScale));
                
                double scaleDiff = newScale - oldScale;
                PanTransform.X -= mousePos.X * scaleDiff;
                PanTransform.Y -= mousePos.Y * scaleDiff;
                
                ZoomTransform.ScaleX = newScale;
                ZoomTransform.ScaleY = newScale;
            }
            
            e.Handled = true;
        }
        
        private void GraphArea_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.MiddleButton == MouseButtonState.Pressed || 
                (e.LeftButton == MouseButtonState.Pressed && Keyboard.Modifiers == ModifierKeys.None))
            {
                _isPanning = true;
                _panStart = e.GetPosition(GraphAreaBorder);
                _panStartX = _layoutAlgorithm == "Grid" ? GridPanTransform.X : PanTransform.X;
                _panStartY = _layoutAlgorithm == "Grid" ? GridPanTransform.Y : PanTransform.Y;
                GraphAreaBorder.CaptureMouse();
                Cursor = Cursors.SizeAll;
                e.Handled = true;
            }
        }
        
        private void GraphArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                var current = e.GetPosition(GraphAreaBorder);
                if (_layoutAlgorithm == "Grid")
                {
                    GridPanTransform.X = _panStartX + (current.X - _panStart.X);
                    GridPanTransform.Y = _panStartY + (current.Y - _panStart.Y);
                }
                else
                {
                    PanTransform.X = _panStartX + (current.X - _panStart.X);
                    PanTransform.Y = _panStartY + (current.Y - _panStart.Y);
                }
                e.Handled = true;
            }
        }
        
        private void GraphArea_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                GraphAreaBorder.ReleaseMouseCapture();
                Cursor = Cursors.Arrow;
                e.Handled = true;
            }
        }
        
        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            if (_layoutAlgorithm == "Grid")
            {
                double newScale = Math.Min(GridZoomTransform.ScaleX * 1.3, 4.0);
                GridZoomTransform.ScaleX = newScale;
                GridZoomTransform.ScaleY = newScale;
            }
            else
            {
                double newScale = Math.Min(ZoomTransform.ScaleX * 1.3, 4.0);
                ZoomTransform.ScaleX = newScale;
                ZoomTransform.ScaleY = newScale;
            }
        }
        
        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            if (_layoutAlgorithm == "Grid")
            {
                double newScale = Math.Max(GridZoomTransform.ScaleX / 1.3, 0.15);
                GridZoomTransform.ScaleX = newScale;
                GridZoomTransform.ScaleY = newScale;
            }
            else
            {
                double newScale = Math.Max(ZoomTransform.ScaleX / 1.3, 0.15);
                ZoomTransform.ScaleX = newScale;
                ZoomTransform.ScaleY = newScale;
            }
        }
        
        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            if (_layoutAlgorithm == "Grid")
            {
                GridZoomTransform.ScaleX = 1.0;
                GridZoomTransform.ScaleY = 1.0;
                GridPanTransform.X = 0;
                GridPanTransform.Y = 0;
            }
            else
            {
                ZoomTransform.ScaleX = 1.0;
                ZoomTransform.ScaleY = 1.0;
                PanTransform.X = 0;
                PanTransform.Y = 0;
            }
        }
        
        #endregion
        
        #region Window Controls
        
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeRestoreWindow_Click(sender, e);
            }
            else
            {
                DragMove();
            }
        }
        
        private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }
        
        private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                MaximizeRestoreButton.Content = "□";
            }
            else
            {
                WindowState = WindowState.Maximized;
                MaximizeRestoreButton.Content = "❐";
            }
        }
        
        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
        
        #endregion
    }
    
    #region Graph Models
    
    /// <summary>
    /// Vertex representing a package
    /// </summary>
    public class PocVertex : System.ComponentModel.INotifyPropertyChanged
    {
        public string FullName { get; }
        public string VertexType { get; }
        public string DisplayName { get; }
        public string SubText { get; }
        public string PreviewImagePath { get; }
        
        private BitmapImage _previewImage;
        public BitmapImage PreviewImage
        {
            get => _previewImage;
            set
            {
                if (_previewImage != value)
                {
                    _previewImage = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PreviewImage)));
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasPreviewImage)));
                }
            }
        }
        
        public bool HasPreviewImage => PreviewImage != null;
        
        /// <summary>
        /// Border color based on vertex type for Grid mode binding
        /// </summary>
        public Brush BorderColor => VertexType switch
        {
            "Root" => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),      // Green
            "Dependency" => new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)), // Blue
            "Dependent" => new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)),  // Orange
            "Missing" => new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)),    // Red
            _ => new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60))             // Gray
        };
        
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        
        public PocVertex(string fullName, string vertexType, string previewImagePath = null)
        {
            FullName = fullName;
            VertexType = vertexType;
            PreviewImagePath = previewImagePath;
            
            var parts = fullName.Split('.');
            if (parts.Length >= 3)
            {
                var creator = parts[0];
                var name = string.Join(".", parts.Skip(1).Take(parts.Length - 2));
                var version = parts.Last();
                DisplayName = name.Length > 20 ? name.Substring(0, 17) + "..." : name;
                // Format version with space: "v 1" or "v latest"
                SubText = $"{creator} • V.{version}";
            }
            else
            {
                DisplayName = fullName.Length > 23 ? fullName.Substring(0, 20) + "..." : fullName;
                SubText = vertexType;
            }
        }
        
        public override string ToString() => FullName;
        public override int GetHashCode() => FullName.GetHashCode(StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object obj) => obj is PocVertex other && 
            FullName.Equals(other.FullName, StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Edge representing a dependency relationship
    /// </summary>
    public class PocEdge : Edge<PocVertex>
    {
        public PocEdge(PocVertex source, PocVertex target) : base(source, target) { }
    }
    
    /// <summary>
    /// Graph type for package dependencies
    /// </summary>
    public class PocGraph : BidirectionalGraph<PocVertex, PocEdge>
    {
        public PocGraph() : base(true) { }
    }
    
    /// <summary>
    /// Custom GraphLayout for our vertex/edge types
    /// </summary>
    public class PocGraphLayout : GraphLayout<PocVertex, PocEdge, PocGraph>
    {
    }
    
    /// <summary>
    /// Converter that returns Visible when false, Collapsed when true
    /// </summary>
    public class InverseBooleanToVisibilityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is bool boolValue)
                return boolValue ? Visibility.Collapsed : Visibility.Visible;
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    #endregion
}
