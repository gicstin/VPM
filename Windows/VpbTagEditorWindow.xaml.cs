using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using VPM.Services.Vpb;

namespace VPM
{
    /// <summary>Bulk VPB tag editor. Library vocabulary as checkboxes; mixed selection starts indeterminate and leaving it means no change.</summary>
    public partial class VpbTagEditorWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public sealed class TagRow : INotifyPropertyChanged
        {
            private bool? _isChecked;

            public string Name { get; init; } = "";

            public int SelectedCarrying { get; init; }

            public int SelectionSize { get; init; }

            /// <summary>Where it started, so Apply can send only genuine changes.</summary>
            public bool? OriginalState { get; init; }

            public bool IsThreeState => OriginalState == null;

            public string CountLabel =>
                SelectionSize > 1 && SelectedCarrying > 0 && SelectedCarrying < SelectionSize
                    ? $"({SelectedCarrying} of {SelectionSize})"
                    : "";

            public bool? IsChecked
            {
                get => _isChecked;
                set
                {
                    if (_isChecked == value) return;
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly ObservableCollection<TagRow> _rows = new();
        private readonly List<TagRow> _allRows = new();
        private readonly int _selectionSize;

        public List<string> TagsToAdd { get; } = new();

        public List<string> TagsToRemove { get; } = new();

        public VpbTagEditorWindow(IReadOnlyList<string> packageUids, string scopeLabel, VpbLibraryData data)
        {
            InitializeComponent();
            ApplyDarkTitleBar();

            _selectionSize = packageUids?.Count ?? 0;
            ScopeText.Text = scopeLabel ?? "";

            BuildRows(packageUids, data);
            TagList.ItemsSource = _rows;
            UpdateHint();

            Loaded += (_, _) => TagInput.Focus();
        }

        private void BuildRows(IReadOnlyList<string> packageUids, VpbLibraryData data)
        {
            var vocabulary = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (data != null)
            {
                foreach (var tag in data.AllTags) vocabulary.Add(tag);
            }

            foreach (var name in vocabulary)
            {
                int carrying = 0;
                if (data != null && packageUids != null)
                {
                    foreach (var uid in packageUids)
                    {
                        if (data.HasTag(uid, name)) carrying++;
                    }
                }

                bool? state = carrying == 0 ? false
                    : carrying == _selectionSize ? true
                    : (bool?)null;

                var row = new TagRow
                {
                    Name = name,
                    SelectedCarrying = carrying,
                    SelectionSize = _selectionSize,
                    OriginalState = state
                };
                row.IsChecked = state;
                row.PropertyChanged += (_, _) => UpdateHint();
                _allRows.Add(row);
            }

            // Tags already on the selection float to the top; the rest stay alphabetical.
            foreach (var row in _allRows.OrderByDescending(r => r.SelectedCarrying > 0)
                                        .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                _rows.Add(row);
        }

        private void TagInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var query = TagInput.Text?.Trim() ?? "";
            _rows.Clear();

            IEnumerable<TagRow> matches = _allRows;
            if (query.Length > 0)
            {
                // Prefix matches first: they are what the user is most likely reaching for.
                matches = _allRows
                    .Where(r => r.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(r => r.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                matches = _allRows.OrderByDescending(r => r.SelectedCarrying > 0)
                                  .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
            }

            foreach (var row in matches) _rows.Add(row);
            UpdateHint();
        }

        private void TagInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            // Enter in the box means "add what I typed", not "accept the dialog".
            e.Handled = true;
            AddTypedTag();
        }

        private void AddButton_Click(object sender, RoutedEventArgs e) => AddTypedTag();

        private void AddTypedTag()
        {
            var normalized = VpbTagWriter.NormalizeTagName(TagInput.Text);
            if (normalized.Length == 0)
            {
                HintText.Text = string.IsNullOrWhiteSpace(TagInput.Text)
                    ? "Type a tag name first."
                    : "That name cannot be used as a tag (line breaks and control characters are not allowed).";
                return;
            }

            var existing = _allRows.FirstOrDefault(r => string.Equals(r.Name, normalized, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.IsChecked = true;
            }
            else
            {
                var row = new TagRow
                {
                    Name = normalized,
                    SelectedCarrying = 0,
                    SelectionSize = _selectionSize,
                    OriginalState = false
                };
                row.IsChecked = true;
                row.PropertyChanged += (_, _) => UpdateHint();
                _allRows.Add(row);
            }

            TagInput.Text = "";
            TagInput_TextChanged(this, null);
            TagInput.Focus();
        }

        private void UpdateHint()
        {
            var addCount = _allRows.Count(r => r.IsChecked == true && r.OriginalState != true);
            var removeCount = _allRows.Count(r => r.IsChecked == false && r.OriginalState != false);

            if (addCount == 0 && removeCount == 0)
            {
                HintText.Text = _allRows.Count == 0
                    ? "No tags exist yet — type a name above and press Enter to create one."
                    : "Type to search, Enter to create a new tag.";
                return;
            }

            var parts = new List<string>(2);
            if (addCount > 0) parts.Add($"add {addCount}");
            if (removeCount > 0) parts.Add($"remove {removeCount}");
            HintText.Text = "Will " + string.Join(" and ", parts) + (_selectionSize > 1 ? $" across {_selectionSize} packages." : ".");
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _allRows)
            {
                // An untouched indeterminate row means "leave these packages as they are".
                if (row.IsChecked == null) continue;
                if (row.IsChecked == row.OriginalState) continue;

                if (row.IsChecked == true) TagsToAdd.Add(row.Name);
                else TagsToRemove.Add(row.Name);
            }

            DialogResult = TagsToAdd.Count > 0 || TagsToRemove.Count > 0;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ApplyDarkTitleBar()
        {
            Loaded += (_, _) =>
            {
                try
                {
                    var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    int useDark = 1;
                    if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
                        DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDark, sizeof(int));
                }
                catch { }
            };
        }
    }
}
