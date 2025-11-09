using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VPM.Services
{
    /// <summary>
    /// Manages comprehensive keyboard navigation throughout the VAM Package Manager UI
    /// </summary>
    public class KeyboardNavigationManager
    {
        private readonly Window _mainWindow;
        private readonly Dictionary<string, Control> _navigableControls;
        private readonly List<string> _navigationOrder;
        
        // UI Control references
        private ListView _packageListView;
        private ListView _dependenciesListView;
        private ListBox _statusFilterList;
        private ListBox _contentTypesList;
        private ListBox _creatorsList;
        private TextBox _packageSearchBox;
        private TextBox _depsSearchBox;
        private TextBox _contentTypesFilterBox;
        private TextBox _creatorsFilterBox;
        
        // Events for communication with MainWindow
        public event Action RefreshRequested;
        public event Action<int> ImageColumnsChanged;
        
        public KeyboardNavigationManager(Window mainWindow)
        {
            _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
            _navigableControls = new Dictionary<string, Control>();
            _navigationOrder = new List<string>
            {
                "PackageSearchBox",
                "PackageListView", 
                "DepsSearchBox",
                "DependenciesListView",
                "StatusFilterList",
                "ContentTypesFilterBox",
                "ContentTypesList",
                "CreatorsFilterBox",
                "CreatorsList"
            };
            
            InitializeControlReferences();
            SetupKeyboardHandlers();
        }
        
        /// <summary>
        /// Initialize references to all navigable controls
        /// </summary>
        private void InitializeControlReferences()
        {
            // Find controls by name
            _packageListView = FindControl<ListView>("PackageListView");
            _dependenciesListView = FindControl<ListView>("DependenciesListView");
            _statusFilterList = FindControl<ListBox>("StatusFilterList");
            _contentTypesList = FindControl<ListBox>("ContentTypesList");
            _creatorsList = FindControl<ListBox>("CreatorsList");
            _packageSearchBox = FindControl<TextBox>("PackageSearchBox");
            _depsSearchBox = FindControl<TextBox>("DepsSearchBox");
            _contentTypesFilterBox = FindControl<TextBox>("ContentTypesFilterBox");
            _creatorsFilterBox = FindControl<TextBox>("CreatorsFilterBox");
            
            // Add to navigable controls dictionary
            if (_packageSearchBox != null) _navigableControls["PackageSearchBox"] = _packageSearchBox;
            if (_packageListView != null) _navigableControls["PackageListView"] = _packageListView;
            if (_depsSearchBox != null) _navigableControls["DepsSearchBox"] = _depsSearchBox;
            if (_dependenciesListView != null) _navigableControls["DependenciesListView"] = _dependenciesListView;
            if (_statusFilterList != null) _navigableControls["StatusFilterList"] = _statusFilterList;
            if (_contentTypesFilterBox != null) _navigableControls["ContentTypesFilterBox"] = _contentTypesFilterBox;
            if (_contentTypesList != null) _navigableControls["ContentTypesList"] = _contentTypesList;
            if (_creatorsFilterBox != null) _navigableControls["CreatorsFilterBox"] = _creatorsFilterBox;
            if (_creatorsList != null) _navigableControls["CreatorsList"] = _creatorsList;
        }
        
        /// <summary>
        /// Set up keyboard event handlers for the main window and controls
        /// </summary>
        private void SetupKeyboardHandlers()
        {
            // Main window key handlers
            _mainWindow.PreviewKeyDown += MainWindow_PreviewKeyDown;
            _mainWindow.KeyDown += MainWindow_KeyDown;
            
            // Set up individual control handlers
            SetupTextBoxHandlers();
            SetupListViewHandlers();
            SetupListBoxHandlers();
        }
        
        /// <summary>
        /// Set up keyboard handlers for TextBox controls
        /// </summary>
        private void SetupTextBoxHandlers()
        {
            var textBoxes = new[] { _packageSearchBox, _depsSearchBox, _contentTypesFilterBox, _creatorsFilterBox };
            
            foreach (var textBox in textBoxes.Where(tb => tb != null))
            {
                textBox.PreviewKeyDown += TextBox_PreviewKeyDown;
                textBox.KeyDown += TextBox_KeyDown;
            }
        }
        
        /// <summary>
        /// Set up keyboard handlers for ListView controls
        /// </summary>
        private void SetupListViewHandlers()
        {
            var listViews = new[] { _packageListView, _dependenciesListView };
            
            foreach (var listView in listViews.Where(lv => lv != null))
            {
                listView.PreviewKeyDown += ListView_PreviewKeyDown;
                listView.KeyDown += ListView_KeyDown;
            }
        }
        
        /// <summary>
        /// Set up keyboard handlers for ListBox controls
        /// </summary>
        private void SetupListBoxHandlers()
        {
            var listBoxes = new[] { _statusFilterList, _contentTypesList, _creatorsList };
            
            foreach (var listBox in listBoxes.Where(lb => lb != null))
            {
                listBox.PreviewKeyDown += ListBox_PreviewKeyDown;
                listBox.KeyDown += ListBox_KeyDown;
            }
        }
        
        /// <summary>
        /// Main window PreviewKeyDown handler for global shortcuts
        /// </summary>
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Handle global shortcuts that should work regardless of focus
            switch (e.Key)
            {
                case Key.F5:
                    if (Keyboard.Modifiers == ModifierKeys.None)
                    {
                        RefreshRequested?.Invoke();
                        e.Handled = true;
                    }
                    break;
                    
                case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                    FocusSearchBox();
                    e.Handled = true;
                    break;
                    
                case Key.Tab:
                    if (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift)
                    {
                        HandleTabNavigation(Keyboard.Modifiers == ModifierKeys.Shift);
                        e.Handled = true;
                    }
                    break;
                    
                case Key.Escape:
                    HandleEscapeKey();
                    e.Handled = true;
                    break;
            }
        }
        
        /// <summary>
        /// Main window KeyDown handler for additional shortcuts
        /// </summary>
        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            // Additional global shortcuts
            switch (e.Key)
            {
                    
                    
                case Key.OemMinus when Keyboard.Modifiers == ModifierKeys.Control: // Ctrl+-
                    ImageColumnsChanged?.Invoke(-1);
                    e.Handled = true;
                    break;
                    
                case Key.OemPlus when Keyboard.Modifiers == ModifierKeys.Control: // Ctrl++
                    ImageColumnsChanged?.Invoke(1);
                    e.Handled = true;
                    break;
            }
        }
        
        /// <summary>
        /// TextBox PreviewKeyDown handler
        /// </summary>
        private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null) return;
            
            switch (e.Key)
            {
                case Key.Escape:
                    ClearTextBox(textBox);
                    e.Handled = true;
                    break;
                    
                case Key.Enter:
                    // Move focus to next logical control
                    MoveFocusFromTextBox(textBox);
                    e.Handled = true;
                    break;
                    
                case Key.Down:
                    if (Keyboard.Modifiers == ModifierKeys.None)
                    {
                        MoveFocusFromTextBox(textBox);
                        e.Handled = true;
                    }
                    break;
            }
        }
        
        /// <summary>
        /// TextBox KeyDown handler
        /// </summary>
        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            // Additional TextBox-specific shortcuts can be added here
        }
        
        /// <summary>
        /// ListView PreviewKeyDown handler
        /// </summary>
        private void ListView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var listView = sender as ListView;
            if (listView == null) return;
            
            switch (e.Key)
            {
                case Key.Right:
                    if (Keyboard.Modifiers == ModifierKeys.Control)
                    {
                        // Ctrl+Right: Switch to next table
                        HandleRightArrowFromListView(listView);
                        e.Handled = true;
                    }
                    break;
                    
                case Key.Left:
                    if (Keyboard.Modifiers == ModifierKeys.Control)
                    {
                        // Ctrl+Left: Switch to previous table
                        HandleLeftArrowFromListView(listView);
                        e.Handled = true;
                    }
                    break;
                    
                case Key.Enter:
                    // Could trigger additional actions like opening details
                    break;
                    
                case Key.Delete:
                    // Could trigger package removal (with confirmation)
                    break;
            }
        }
        
        /// <summary>
        /// ListView KeyDown handler
        /// </summary>
        private void ListView_KeyDown(object sender, KeyEventArgs e)
        {
            // Additional ListView-specific shortcuts can be added here
        }
        
        /// <summary>
        /// ListBox PreviewKeyDown handler
        /// </summary>
        private void ListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox == null) return;
            
            switch (e.Key)
            {
                case Key.Right:
                    if (Keyboard.Modifiers == ModifierKeys.Control)
                    {
                        // Ctrl+Right: Switch to next control
                        HandleRightArrowFromListBox(listBox);
                        e.Handled = true;
                    }
                    break;
                    
                case Key.Left:
                    if (Keyboard.Modifiers == ModifierKeys.Control)
                    {
                        // Ctrl+Left: Switch to previous control
                        HandleLeftArrowFromListBox(listBox);
                        e.Handled = true;
                    }
                    break;
                    
                case Key.Space:
                    // Toggle selection for multi-select ListBoxes
                    ToggleListBoxItemSelection(listBox);
                    e.Handled = true;
                    break;
            }
        }
        
        /// <summary>
        /// ListBox KeyDown handler
        /// </summary>
        private void ListBox_KeyDown(object sender, KeyEventArgs e)
        {
            // Additional ListBox-specific shortcuts can be added here
        }
        
        /// <summary>
        /// Handle right arrow key navigation from ListView
        /// </summary>
        private void HandleRightArrowFromListView(ListView listView)
        {
            if (listView == _packageListView)
            {
                // From package list, move to dependencies if there are items
                if (_dependenciesListView != null && HasItems(_dependenciesListView))
                {
                    FocusAndSelectFirst(_dependenciesListView);
                }
                else
                {
                    // No dependencies, move to filters
                    FocusAndSelectFirst(_statusFilterList);
                }
            }
            else if (listView == _dependenciesListView)
            {
                // From dependencies, move to filters
                FocusAndSelectFirst(_statusFilterList);
            }
        }
        
        /// <summary>
        /// Handle left arrow key navigation from ListView
        /// </summary>
        private void HandleLeftArrowFromListView(ListView listView)
        {
            if (listView == _dependenciesListView)
            {
                // From dependencies, move back to packages
                if (_packageListView != null && HasItems(_packageListView))
                {
                    FocusAndSelectFirst(_packageListView);
                }
                else
                {
                    _packageSearchBox?.Focus();
                }
            }
            else if (listView == _packageListView)
            {
                // From packages, move to search box
                _packageSearchBox?.Focus();
                _packageSearchBox?.SelectAll();
            }
        }
        
        /// <summary>
        /// Handle right arrow key navigation from ListBox
        /// </summary>
        private void HandleRightArrowFromListBox(ListBox listBox)
        {
            if (listBox == _statusFilterList)
            {
                FocusAndSelectFirst(_contentTypesList);
            }
            else if (listBox == _contentTypesList)
            {
                FocusAndSelectFirst(_creatorsList);
            }
            else if (listBox == _creatorsList)
            {
                // Move to main content area
                if (_packageListView != null && HasItems(_packageListView))
                {
                    FocusAndSelectFirst(_packageListView);
                }
                else
                {
                    _packageSearchBox?.Focus();
                }
            }
        }
        
        /// <summary>
        /// Handle left arrow key navigation from ListBox
        /// </summary>
        private void HandleLeftArrowFromListBox(ListBox listBox)
        {
            if (listBox == _creatorsList)
            {
                FocusAndSelectFirst(_contentTypesList);
            }
            else if (listBox == _contentTypesList)
            {
                FocusAndSelectFirst(_statusFilterList);
            }
            else if (listBox == _statusFilterList)
            {
                // Move to dependencies or packages
                if (_dependenciesListView != null && HasItems(_dependenciesListView))
                {
                    FocusAndSelectFirst(_dependenciesListView);
                }
                else if (_packageListView != null && HasItems(_packageListView))
                {
                    FocusAndSelectFirst(_packageListView);
                }
            }
        }
        
        /// <summary>
        /// Handle Tab navigation between controls
        /// </summary>
        private void HandleTabNavigation(bool reverse)
        {
            var focusedElement = Keyboard.FocusedElement as FrameworkElement;
            if (focusedElement == null) return;
            
            var currentControlName = GetControlName(focusedElement);
            if (string.IsNullOrEmpty(currentControlName)) return;
            
            var currentIndex = _navigationOrder.IndexOf(currentControlName);
            if (currentIndex == -1) return;
            
            int nextIndex;
            if (reverse)
            {
                nextIndex = currentIndex - 1;
                if (nextIndex < 0) nextIndex = _navigationOrder.Count - 1;
            }
            else
            {
                nextIndex = currentIndex + 1;
                if (nextIndex >= _navigationOrder.Count) nextIndex = 0;
            }
            
            var nextControlName = _navigationOrder[nextIndex];
            if (_navigableControls.TryGetValue(nextControlName, out var nextControl))
            {
                if (nextControl is Selector selector)
                {
                    FocusAndSelectFirst(selector);
                }
                else
                {
                    nextControl.Focus();
                }
            }
        }
        
        /// <summary>
        /// Handle Escape key - clear focused textbox or remove focus
        /// </summary>
        private void HandleEscapeKey()
        {
            var focusedElement = Keyboard.FocusedElement;
            
            if (focusedElement is TextBox textBox)
            {
                ClearTextBox(textBox);
            }
            else
            {
                // Remove focus from current element
                _mainWindow.Focus();
            }
        }
        
        /// <summary>
        /// Focus the main search box (package search)
        /// </summary>
        private void FocusSearchBox()
        {
            _packageSearchBox?.Focus();
            _packageSearchBox?.SelectAll();
        }
        
        /// <summary>
        /// Clear a TextBox and restore placeholder text if needed
        /// </summary>
        private void ClearTextBox(TextBox textBox)
        {
            if (textBox == null) return;
            
            // Clear the text
            textBox.Text = "";
            
            // Restore placeholder text based on the textbox
            if (textBox == _packageSearchBox)
            {
                textBox.Text = "📦 Filter packages, descriptions, tags...";
                textBox.Foreground = SystemColors.GrayTextBrush;
            }
            else if (textBox == _depsSearchBox)
            {
                textBox.Text = "📝 Filter dependencies...";
                textBox.Foreground = SystemColors.GrayTextBrush;
            }
            else if (textBox == _contentTypesFilterBox)
            {
                textBox.Text = "📝 Filter content types...";
                textBox.Foreground = SystemColors.GrayTextBrush;
            }
            else if (textBox == _creatorsFilterBox)
            {
                textBox.Text = "😣 Filter creators...";
                textBox.Foreground = SystemColors.GrayTextBrush;
            }
        }
        
        /// <summary>
        /// Move focus from a TextBox to its logical next control
        /// </summary>
        private void MoveFocusFromTextBox(TextBox textBox)
        {
            if (textBox == _packageSearchBox && _packageListView != null)
            {
                FocusAndSelectFirst(_packageListView);
            }
            else if (textBox == _depsSearchBox && _dependenciesListView != null)
            {
                FocusAndSelectFirst(_dependenciesListView);
            }
            else if (textBox == _contentTypesFilterBox && _contentTypesList != null)
            {
                FocusAndSelectFirst(_contentTypesList);
            }
            else if (textBox == _creatorsFilterBox && _creatorsList != null)
            {
                FocusAndSelectFirst(_creatorsList);
            }
        }
        
        /// <summary>
        /// Focus a control and select its first item if it's a selector
        /// </summary>
        private void FocusAndSelectFirst(Control control)
        {
            if (control == null) return;
            
            control.Focus();
            
            if (control is Selector selector && HasItems(selector))
            {
                // Only select first item if nothing is currently selected
                if (selector.SelectedIndex == -1)
                {
                    selector.SelectedIndex = 0;
                }
                
                // Ensure the selected item is visible and focused
                if (selector.SelectedIndex >= 0)
                {
                    var container = selector.ItemContainerGenerator.ContainerFromIndex(selector.SelectedIndex) as FrameworkElement;
                    container?.Focus();
                }
            }
        }
        
        /// <summary>
        /// Toggle selection of current item in multi-select ListBox
        /// </summary>
        private void ToggleListBoxItemSelection(ListBox listBox)
        {
            if (listBox?.SelectedItem == null) return;
            
            var selectedIndex = listBox.SelectedIndex;
            if (selectedIndex >= 0)
            {
                var item = listBox.ItemContainerGenerator.ContainerFromIndex(selectedIndex) as ListBoxItem;
                if (item != null)
                {
                    item.IsSelected = !item.IsSelected;
                }
            }
        }
        
        /// <summary>
        /// Check if a selector has items
        /// </summary>
        private bool HasItems(Selector selector)
        {
            return selector?.Items?.Count > 0;
        }
        
        /// <summary>
        /// Get the name of a control for navigation purposes
        /// </summary>
        private string GetControlName(FrameworkElement element)
        {
            if (element == null) return null;
            
            // Try to match by reference first
            foreach (var kvp in _navigableControls)
            {
                if (ReferenceEquals(kvp.Value, element))
                {
                    return kvp.Key;
                }
            }
            
            // Fallback to Name property
            return element.Name;
        }
        
        /// <summary>
        /// Find a control by name in the visual tree
        /// </summary>
        private T FindControl<T>(string name) where T : FrameworkElement
        {
            return FindChildByName<T>(_mainWindow, name);
        }
        
        /// <summary>
        /// Recursively find a child control by name
        /// </summary>
        private T FindChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            if (parent == null) return null;
            
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                
                if (child is T element && element.Name == name)
                {
                    return element;
                }
                
                var result = FindChildByName<T>(child, name);
                if (result != null)
                {
                    return result;
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Get keyboard shortcut help text
        /// </summary>
        public string GetKeyboardShortcutsHelp()
        {
            return @"KEYBOARD SHORTCUTS:

NAVIGATION:
• Tab / Shift+Tab - Navigate between controls
• Up/Down Arrow - Navigate within lists (preserves selection)
• Ctrl+Left/Right - Switch between related tables/lists
• Enter - Confirm selection / move to next control
• Escape - Clear focused textbox or remove focus

GLOBAL SHORTCUTS:
• F5 - Refresh packages
• Ctrl+F - Focus main search box
• Ctrl+B - Build cache
• Ctrl+, - Open settings
• Ctrl+- / Ctrl++ - Decrease/increase image columns

LIST NAVIGATION:
• Up/Down - Navigate within current list
• Ctrl+Left/Right - Move between related lists/tables
• Space - Toggle selection (multi-select lists)
• Enter - Confirm selection

TABLE SWITCHING:
• Ctrl+Right from Packages †’ Dependencies †’ Filters
• Ctrl+Left reverses the direction
• Normal arrow keys work within each table

SEARCH & FILTERS:
• Escape - Clear current textbox
• Enter/Down - Move from search to results
• Type to search in any focused textbox

The interface supports full keyboard navigation - you can operate the entire application without using a mouse!";
        }
    }
}

