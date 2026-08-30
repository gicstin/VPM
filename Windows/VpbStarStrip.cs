using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VPM.Windows
{
    /// <summary>Inline 5-star rating. Hover previews; click commits; ✕ clears. Bubbling routed event because the strip lives in a virtualized DataTemplate.</summary>
    public sealed class VpbStarStrip : StackPanel
    {
        public const int MaxStars = 5;

        /// <summary>Returned by <see cref="ValueAt"/> when the cursor is over the clear button.</summary>
        private const int ClearZone = -1;

        private const string FilledGlyph = "★";
        private const string HollowGlyph = "☆";
        private const string ClearGlyph = "✕";

        private static readonly Brush RatedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)));
        private static readonly Brush PreviewBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x82)));
        private static readonly Brush EmptyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
        private static readonly Brush ClearBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)));
        private static readonly Brush ClearHotBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)));

        private readonly TextBlock[] _stars = new TextBlock[MaxStars];
        private readonly TextBlock _clear;
        private int _hoverValue;

        public static readonly DependencyProperty RatingProperty = DependencyProperty.Register(
            nameof(Rating),
            typeof(int),
            typeof(VpbStarStrip),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnVisualStateChanged));

        /// <summary>True when the stars come from rated content inside the package, not the package itself.</summary>
        public static readonly DependencyProperty IsInheritedProperty = DependencyProperty.Register(
            nameof(IsInherited),
            typeof(bool),
            typeof(VpbStarStrip),
            new FrameworkPropertyMetadata(false, OnVisualStateChanged));

        public static readonly RoutedEvent RatingCommittedEvent = EventManager.RegisterRoutedEvent(
            "RatingCommitted",
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(VpbStarStrip));

        public int Rating
        {
            get => (int)GetValue(RatingProperty);
            set => SetValue(RatingProperty, value);
        }

        public bool IsInherited
        {
            get => (bool)GetValue(IsInheritedProperty);
            set => SetValue(IsInheritedProperty, value);
        }

        /// <summary>The value the user just committed. Read by the DataGrid-level handler.</summary>
        public int CommittedRating { get; private set; }

        public event RoutedEventHandler RatingCommitted
        {
            add => AddHandler(RatingCommittedEvent, value);
            remove => RemoveHandler(RatingCommittedEvent, value);
        }

        public VpbStarStrip()
        {
            Orientation = Orientation.Horizontal;
            Background = Brushes.Transparent; // hit-testable gaps, so moving between stars does not flicker
            VerticalAlignment = VerticalAlignment.Center;

            for (int i = 0; i < MaxStars; i++)
            {
                var star = new TextBlock
                {
                    Text = HollowGlyph,
                    FontSize = 13,
                    // Generous padding keeps each star an easy target and removes dead gaps between them.
                    Padding = new Thickness(1, 0, 1, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = EmptyBrush
                };
                _stars[i] = star;
                Children.Add(star);
            }

            _clear = new TextBlock
            {
                Text = ClearGlyph,
                FontSize = 10,
                Padding = new Thickness(4, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ClearBrush,
                Visibility = Visibility.Collapsed
            };
            Children.Add(_clear);

            Cursor = Cursors.Hand;
            MouseMove += OnMouseMove;
            MouseLeave += OnMouseLeave;
            MouseLeftButtonUp += OnMouseLeftButtonUp;
            Redraw();
        }

        private static void OnVisualStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as VpbStarStrip)?.Redraw();
        }

        private int ValueAt(Point position)
        {
            if (_clear.Visibility == Visibility.Visible)
            {
                var clearOrigin = _clear.TranslatePoint(new Point(0, 0), this);
                if (position.X >= clearOrigin.X) return ClearZone;
            }

            for (int i = 0; i < MaxStars; i++)
            {
                var star = _stars[i];
                var origin = star.TranslatePoint(new Point(0, 0), this);
                if (position.X >= origin.X && position.X <= origin.X + star.ActualWidth)
                    return i + 1;
            }

            // Past the last star still counts as five; short of the first, nothing.
            return position.X > 0 ? MaxStars : 0;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var value = ValueAt(e.GetPosition(this));
            if (value == _hoverValue) return;
            _hoverValue = value;
            Redraw();
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            if (_hoverValue == 0) return;
            _hoverValue = 0;
            Redraw();
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var value = ValueAt(e.GetPosition(this));

            if (value == ClearZone)
            {
                if (Rating <= 0) return;
                CommittedRating = 0;
            }
            else if (value > 0)
            {
                // Clicking the current rating also clears it, for anyone who reaches for that first.
                CommittedRating = value == Rating ? 0 : value;
            }
            else
            {
                return;
            }

            e.Handled = true;
            RaiseEvent(new RoutedEventArgs(RatingCommittedEvent, this));
        }

        private void Redraw()
        {
            var overClear = _hoverValue == ClearZone;
            var previewing = _hoverValue > 0;
            var showing = previewing ? _hoverValue : Rating;

            for (int i = 0; i < MaxStars; i++)
            {
                // Hovering the clear button previews the result: every star empty.
                var filled = !overClear && i < showing;
                var star = _stars[i];
                star.Text = filled ? FilledGlyph : HollowGlyph;
                star.Foreground = filled ? (previewing ? PreviewBrush : RatedBrush) : EmptyBrush;
            }

            _clear.Visibility = Rating > 0 ? Visibility.Visible : Visibility.Collapsed;
            _clear.Foreground = overClear ? ClearHotBrush : ClearBrush;

            // Unrated rows stay legible but recede; hovering brings the whole strip forward.
            Opacity = _hoverValue != 0 || Rating > 0 ? 1.0 : 0.3;
            ToolTip = BuildToolTip();
        }

        private string BuildToolTip()
        {
            if (_hoverValue == ClearZone) return "Clear this rating";
            if (_hoverValue > 0)
            {
                return _hoverValue == Rating
                    ? $"Click to clear this {Rating}-star rating"
                    : $"Rate {_hoverValue}/5 in VPB";
            }
            if (Rating <= 0) return "Not rated in VPB — click to rate";
            return IsInherited
                ? $"{Rating}/5, inherited from rated content inside this package"
                : $"Rated {Rating}/5 in VPB";
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
