using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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

        public sealed class HubTagRow : INotifyPropertyChanged
        {
            private static readonly Brush VisibleBrush = MakeBrush(0xE2, 0xB3, 0x41);
            private static readonly Brush HiddenBrush = MakeBrush(0x7A, 0x7A, 0x7A);

            private bool _hiddenHere;
            private bool _hiddenEverywhere;

            public string Name { get; init; } = "";

            public int Carrying { get; init; }

            public int SelectionSize { get; init; }

            public bool OriginallyHiddenHere { get; init; }

            public bool OriginallyHiddenEverywhere { get; init; }

            public bool HiddenHere
            {
                get => _hiddenHere;
                set { if (_hiddenHere != value) { _hiddenHere = value; RaiseAll(); } }
            }

            public bool HiddenEverywhere
            {
                get => _hiddenEverywhere;
                set { if (_hiddenEverywhere != value) { _hiddenEverywhere = value; RaiseAll(); } }
            }

            public bool IsHidden => HiddenHere || HiddenEverywhere;

            public string Label
            {
                get
                {
                    var suffix = SelectionSize > 1 && Carrying > 0 && Carrying < SelectionSize
                        ? $" ({Carrying} of {SelectionSize})"
                        : "";
                    if (HiddenEverywhere) return Name + suffix + " — hidden everywhere";
                    if (HiddenHere) return Name + suffix + " — hidden here";
                    return Name + suffix;
                }
            }

            public Brush Brush => IsHidden ? HiddenBrush : VisibleBrush;

            public string StateTip => IsHidden
                ? "Hidden by a local preference. The data pack still carries it; VPM just stops showing it."
                : "From the creator's Hub resource listing. Read-only — type the same word above to add it as your own tag.";

            public string ScopeActionLabel => HiddenHere ? "Show here" : "Hide here";

            public string ScopeActionTip => SelectionSize > 1
                ? "Hide this tag on the selected packages only."
                : "Hide this tag on this package only — for when the creator's tag is wrong here.";

            public string GlobalActionLabel => HiddenEverywhere ? "Show everywhere" : "Hide everywhere";

            public string GlobalActionTip => "Hide this tag across the whole library.";

            public event PropertyChangedEventHandler PropertyChanged;

            private void RaiseAll()
            {
                foreach (var name in new[]
                         {
                             nameof(HiddenHere), nameof(HiddenEverywhere), nameof(IsHidden),
                             nameof(Label), nameof(Brush), nameof(StateTip),
                             nameof(ScopeActionLabel), nameof(GlobalActionLabel)
                         })
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }

            private static Brush MakeBrush(byte r, byte g, byte b)
            {
                var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                brush.Freeze();
                return brush;
            }
        }

        private readonly ObservableCollection<TagRow> _rows = new();
        private readonly List<TagRow> _allRows = new();
        private readonly ObservableCollection<HubTagRow> _hubRows = new();
        private readonly int _selectionSize;

        public List<string> TagsToAdd { get; } = new();

        public List<string> TagsToRemove { get; } = new();

        public List<string> HubTagsToHideHere { get; } = new();

        public List<string> HubTagsToShowHere { get; } = new();

        public List<string> HubTagsToHideEverywhere { get; } = new();

        public List<string> HubTagsToShowEverywhere { get; } = new();

        public bool HasHubTagChanges =>
            HubTagsToHideHere.Count > 0 || HubTagsToShowHere.Count > 0
            || HubTagsToHideEverywhere.Count > 0 || HubTagsToShowEverywhere.Count > 0;

        public VpbTagEditorWindow(IReadOnlyList<string> packageUids, string scopeLabel, VpbLibraryData data)
            : this(packageUids, scopeLabel, data, null)
        {
        }

        public VpbTagEditorWindow(
            IReadOnlyList<string> packageUids,
            string scopeLabel,
            VpbLibraryData data,
            VpbLookData lookData)
        {
            InitializeComponent();
            ApplyDarkTitleBar();

            _selectionSize = packageUids?.Count ?? 0;
            ScopeText.Text = scopeLabel ?? "";

            BuildRows(packageUids, data);
            TagList.ItemsSource = _rows;

            BuildHubRows(packageUids, lookData);
            HubTagList.ItemsSource = _hubRows;
            HubSection.Visibility = _hubRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateHint();

            Loaded += (_, _) => TagInput.Focus();
        }

        private void BuildHubRows(IReadOnlyList<string> packageUids, VpbLookData lookData)
        {
            if (lookData == null || packageUids == null || packageUids.Count == 0) return;
            if (!lookData.HubTagsEnabled) return;

            var carrying = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var hiddenHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var uid in packageUids)
            {
                if (string.IsNullOrEmpty(uid)) continue;

                foreach (var tag in lookData.HubTagsFor(uid))
                {
                    carrying.TryGetValue(tag, out var n);
                    carrying[tag] = n + 1;
                }

                foreach (var tag in lookData.HiddenHubTagsFor(uid))
                {
                    carrying.TryGetValue(tag, out var n);
                    carrying[tag] = n + 1;
                    if (lookData.TagPrefs.IsHiddenOnPackage(uid, tag)) hiddenHere.Add(tag);
                }
            }

            foreach (var kvp in carrying)
            {
                var row = new HubTagRow
                {
                    Name = kvp.Key,
                    Carrying = kvp.Value,
                    SelectionSize = _selectionSize,
                    OriginallyHiddenHere = hiddenHere.Contains(kvp.Key),
                    OriginallyHiddenEverywhere = lookData.TagPrefs.IsHiddenGlobally(kvp.Key)
                };
                row.HiddenHere = row.OriginallyHiddenHere;
                row.HiddenEverywhere = row.OriginallyHiddenEverywhere;
                row.PropertyChanged += (_, _) => UpdateHint();
                _hubRows.Add(row);
            }
        }

        private void HubScopeButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not HubTagRow row) return;
            row.HiddenHere = !row.HiddenHere;
        }

        private void HubGlobalButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not HubTagRow row) return;
            row.HiddenEverywhere = !row.HiddenEverywhere;
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
            var hubHideCount = _hubRows.Count(r =>
                (r.HiddenHere && !r.OriginallyHiddenHere) || (r.HiddenEverywhere && !r.OriginallyHiddenEverywhere));
            var hubShowCount = _hubRows.Count(r =>
                (!r.HiddenHere && r.OriginallyHiddenHere) || (!r.HiddenEverywhere && r.OriginallyHiddenEverywhere));

            if (addCount == 0 && removeCount == 0 && hubHideCount == 0 && hubShowCount == 0)
            {
                HintText.Text = _allRows.Count == 0
                    ? "No tags exist yet — type a name above and press Enter to create one."
                    : "Type to search, Enter to create a new tag.";
                return;
            }

            var parts = new List<string>(4);
            if (addCount > 0) parts.Add($"add {addCount}");
            if (removeCount > 0) parts.Add($"remove {removeCount}");
            if (hubHideCount > 0) parts.Add($"hide {hubHideCount} hub tag(s)");
            if (hubShowCount > 0) parts.Add($"unhide {hubShowCount} hub tag(s)");
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

            foreach (var row in _hubRows)
            {
                if (row.HiddenHere != row.OriginallyHiddenHere)
                    (row.HiddenHere ? HubTagsToHideHere : HubTagsToShowHere).Add(row.Name);

                if (row.HiddenEverywhere != row.OriginallyHiddenEverywhere)
                    (row.HiddenEverywhere ? HubTagsToHideEverywhere : HubTagsToShowEverywhere).Add(row.Name);
            }

            DialogResult = TagsToAdd.Count > 0 || TagsToRemove.Count > 0 || HasHubTagChanges;
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
