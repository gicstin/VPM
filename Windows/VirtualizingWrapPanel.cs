using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace VPM.Windows
{
    public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
    {
        public static readonly DependencyProperty UseDynamicItemHeightProperty = DependencyProperty.Register(
            nameof(UseDynamicItemHeight),
            typeof(bool),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty ThumbnailAspectRatioProperty = DependencyProperty.Register(
            nameof(ThumbnailAspectRatio),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(0.75d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty ThumbnailMinHeightProperty = DependencyProperty.Register(
            nameof(ThumbnailMinHeight),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(140d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty ThumbnailMaxHeightProperty = DependencyProperty.Register(
            nameof(ThumbnailMaxHeight),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(280d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty DetailsHeightProperty = DependencyProperty.Register(
            nameof(DetailsHeight),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(120d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty MaxItemWidthProperty = DependencyProperty.Register(
            nameof(MaxItemWidth),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(340d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
            nameof(ItemWidth),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(240d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
            nameof(ItemHeight),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(320d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public double ItemWidth
        {
            get => (double)GetValue(ItemWidthProperty);
            set => SetValue(ItemWidthProperty, value);
        }

        public double MaxItemWidth
        {
            get => (double)GetValue(MaxItemWidthProperty);
            set => SetValue(MaxItemWidthProperty, value);
        }

        public double ItemHeight
        {
            get => (double)GetValue(ItemHeightProperty);
            set => SetValue(ItemHeightProperty, value);
        }

        public bool UseDynamicItemHeight
        {
            get => (bool)GetValue(UseDynamicItemHeightProperty);
            set => SetValue(UseDynamicItemHeightProperty, value);
        }

        public double ThumbnailAspectRatio
        {
            get => (double)GetValue(ThumbnailAspectRatioProperty);
            set => SetValue(ThumbnailAspectRatioProperty, value);
        }

        public double ThumbnailMinHeight
        {
            get => (double)GetValue(ThumbnailMinHeightProperty);
            set => SetValue(ThumbnailMinHeightProperty, value);
        }

        public double ThumbnailMaxHeight
        {
            get => (double)GetValue(ThumbnailMaxHeightProperty);
            set => SetValue(ThumbnailMaxHeightProperty, value);
        }

        public double DetailsHeight
        {
            get => (double)GetValue(DetailsHeightProperty);
            set => SetValue(DetailsHeightProperty, value);
        }

        private IItemContainerGenerator _generator;
        private Size _extent = new Size(0, 0);
        private Size _viewport = new Size(0, 0);
        private Point _offset;

        private double _computedItemWidth;
        private double _computedItemHeight;

        private int _firstRealizedIndex = -1;
        private int _lastRealizedIndex = -1;
        private int _columns = 1;

        private ScrollViewer _scrollOwner;

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            _generator = ItemContainerGenerator;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (_generator == null)
                _generator = ItemContainerGenerator;

            var itemsControl = ItemsControl.GetItemsOwner(this);
            if (itemsControl == null)
                return availableSize;

            int itemCount = itemsControl.Items.Count;
            if (itemCount == 0)
            {
                CleanUpItems(0, -1);
                SetExtentAndViewport(availableSize, 0, 0);
                return availableSize;
            }

            // ItemWidth is treated as the minimum width to preserve existing XAML usage.
            double minItemWidth = Math.Max(1, ItemWidth);
            double maxItemWidth = Math.Max(minItemWidth, MaxItemWidth);
            double fallbackItemHeight = Math.Max(1, ItemHeight);

            // Compute columns based on available width
            double availableWidth = double.IsInfinity(availableSize.Width) ? minItemWidth : Math.Max(1, availableSize.Width);

            // Choose a column count that yields a width in [minItemWidth, maxItemWidth], then compute width to fill the row.
            int columns = Math.Max(1, (int)Math.Floor(availableWidth / minItemWidth));
            while (columns > 1 && (availableWidth / columns) > maxItemWidth)
                columns++;
            while (columns > 1 && (availableWidth / columns) < minItemWidth)
                columns--;

            _computedItemWidth = availableWidth / columns;

            _computedItemHeight = UseDynamicItemHeight
                ? ComputeItemHeight(_computedItemWidth)
                : fallbackItemHeight;

            int totalRows = (int)Math.Ceiling((double)itemCount / columns);

            // visible row range based on vertical offset
            int firstVisibleRow = (int)Math.Floor(_offset.Y / _computedItemHeight);
            int visibleRowCount = (int)Math.Ceiling((double)Math.Max(availableSize.Height, 0) / _computedItemHeight) + 1;
            int lastVisibleRow = Math.Min(totalRows - 1, firstVisibleRow + visibleRowCount);

            int firstIndex = Math.Max(0, firstVisibleRow * columns);
            int lastIndex = Math.Min(itemCount - 1, ((lastVisibleRow + 1) * columns) - 1);

            CleanUpItems(firstIndex, lastIndex);

            var childAvailable = new Size(_computedItemWidth, _computedItemHeight);

            GeneratorPosition startPos = _generator.GeneratorPositionFromIndex(firstIndex);
            int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

            using (_generator.StartAt(startPos, GeneratorDirection.Forward, true))
            {
                for (int itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
                {
                    bool newlyRealized;
                    var child = (UIElement)_generator.GenerateNext(out newlyRealized);

                    if (newlyRealized)
                    {
                        if (childIndex >= Children.Count)
                            AddInternalChild(child);
                        else
                            InsertInternalChild(childIndex, child);

                        _generator.PrepareItemContainer(child);
                    }
                    else
                    {
                        // Ensure it is in the right place
                        if (Children[childIndex] != child)
                        {
                            RemoveInternalChildRange(childIndex, 1);
                            InsertInternalChild(childIndex, child);
                        }
                    }

                    child.Measure(childAvailable);
                }
            }

            _firstRealizedIndex = firstIndex;
            _lastRealizedIndex = lastIndex;
            _columns = columns;

            // Extent depends on total rows
            double extentHeight = totalRows * _computedItemHeight;
            double extentWidth = columns * _computedItemWidth;
            SetExtentAndViewport(availableSize, extentWidth, extentHeight);

            return availableSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Children.Count == 0 || _firstRealizedIndex < 0)
                return finalSize;

            double minItemWidth = Math.Max(1, ItemWidth);
            double maxItemWidth = Math.Max(minItemWidth, MaxItemWidth);
            double fallbackItemHeight = Math.Max(1, ItemHeight);

            double availableWidth = double.IsInfinity(finalSize.Width) ? minItemWidth : Math.Max(1, finalSize.Width);

            int columns = Math.Max(1, (int)Math.Floor(availableWidth / minItemWidth));
            while (columns > 1 && (availableWidth / columns) > maxItemWidth)
                columns++;
            while (columns > 1 && (availableWidth / columns) < minItemWidth)
                columns--;

            var itemWidth = availableWidth / columns;
            var itemHeight = UseDynamicItemHeight ? ComputeItemHeight(itemWidth) : fallbackItemHeight;

            // Children correspond to a contiguous realized range starting at _firstRealizedIndex
            for (int i = 0; i < Children.Count; i++)
            {
                int itemIndex = _firstRealizedIndex + i;
                int row = itemIndex / columns;
                int column = itemIndex % columns;

                double x = column * itemWidth;
                double y = (row * itemHeight) - _offset.Y;

                Children[i].Arrange(new Rect(new Point(x, y), new Size(itemWidth, itemHeight)));
            }

            return finalSize;
        }

        private double ComputeItemHeight(double itemWidth)
        {
            var ratio = ThumbnailAspectRatio;
            if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0)
                ratio = 0.75d;

            var minThumb = Math.Max(0, ThumbnailMinHeight);
            var maxThumb = Math.Max(minThumb, ThumbnailMaxHeight);

            var thumb = itemWidth * ratio;
            if (thumb < minThumb) thumb = minThumb;
            if (thumb > maxThumb) thumb = maxThumb;

            var details = Math.Max(0, DetailsHeight);
            return Math.Max(1, thumb + details);
        }

        private void CleanUpItems(int firstIndex, int lastIndex)
        {
            if (_firstRealizedIndex < 0 || _lastRealizedIndex < 0 || Children.Count == 0)
            {
                _firstRealizedIndex = firstIndex;
                _lastRealizedIndex = lastIndex;
                return;
            }

            // Remove from beginning
            if (firstIndex > _firstRealizedIndex)
            {
                int removeCount = Math.Min(firstIndex - _firstRealizedIndex, Children.Count);
                if (removeCount > 0)
                {
                    var genPos = new GeneratorPosition(0, 0);
                    _generator.Remove(genPos, removeCount);
                    RemoveInternalChildRange(0, removeCount);
                    _firstRealizedIndex += removeCount;
                }
            }

            // Remove from end
            if (lastIndex < _lastRealizedIndex)
            {
                int removeCount = Math.Min(_lastRealizedIndex - lastIndex, Children.Count);
                if (removeCount > 0)
                {
                    int startIndex = Children.Count - removeCount;
                    var genPos = new GeneratorPosition(startIndex, 0);
                    _generator.Remove(genPos, removeCount);
                    RemoveInternalChildRange(startIndex, removeCount);
                    _lastRealizedIndex -= removeCount;
                }
            }
        }

        private void SetExtentAndViewport(Size availableSize, double extentWidth, double extentHeight)
        {
            var newViewport = new Size(
                double.IsInfinity(availableSize.Width) ? extentWidth : Math.Max(0, availableSize.Width),
                double.IsInfinity(availableSize.Height) ? extentHeight : Math.Max(0, availableSize.Height));

            var newExtent = new Size(Math.Max(0, extentWidth), Math.Max(0, extentHeight));

            bool extentChanged = newExtent != _extent;
            bool viewportChanged = newViewport != _viewport;

            _extent = newExtent;
            _viewport = newViewport;

            if (extentChanged || viewportChanged)
                _scrollOwner?.InvalidateScrollInfo();
        }

        #region IScrollInfo

        public bool CanHorizontallyScroll { get; set; }
        public bool CanVerticallyScroll { get; set; } = true;

        public double ExtentWidth => _extent.Width;
        public double ExtentHeight => _extent.Height;

        public double ViewportWidth => _viewport.Width;
        public double ViewportHeight => _viewport.Height;

        public double HorizontalOffset => _offset.X;
        public double VerticalOffset => _offset.Y;

        public ScrollViewer ScrollOwner
        {
            get => _scrollOwner;
            set => _scrollOwner = value;
        }

        private const double LineScrollPixels = 32;

        public void LineDown() => SetVerticalOffset(VerticalOffset + LineScrollPixels);
        public void LineUp() => SetVerticalOffset(VerticalOffset - LineScrollPixels);
        public void LineLeft() => SetHorizontalOffset(HorizontalOffset - LineScrollPixels);
        public void LineRight() => SetHorizontalOffset(HorizontalOffset + LineScrollPixels);

        public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + (SystemParameters.WheelScrollLines * LineScrollPixels));
        public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - (SystemParameters.WheelScrollLines * LineScrollPixels));
        public void MouseWheelLeft() => SetHorizontalOffset(HorizontalOffset - (SystemParameters.WheelScrollLines * LineScrollPixels));
        public void MouseWheelRight() => SetHorizontalOffset(HorizontalOffset + (SystemParameters.WheelScrollLines * LineScrollPixels));

        public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
        public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
        public void PageLeft() => SetHorizontalOffset(HorizontalOffset - ViewportWidth);
        public void PageRight() => SetHorizontalOffset(HorizontalOffset + ViewportWidth);

        public Rect MakeVisible(Visual visual, Rect rectangle)
        {
            if (visual is UIElement child && InternalChildren.Contains(child))
            {
                int index = InternalChildren.IndexOf(child);
                if (index >= 0)
                {
                    // Best-effort: scroll so the current offset shows the row containing this child.
                    double itemHeight = Math.Max(1, UseDynamicItemHeight ? _computedItemHeight : ItemHeight);
                    double targetY = Math.Floor((_offset.Y + rectangle.Y) / itemHeight) * itemHeight;
                    SetVerticalOffset(targetY);
                }
            }

            return rectangle;
        }

        public void SetHorizontalOffset(double offset)
        {
            if (offset < 0)
                offset = 0;
            if (offset + ViewportWidth > ExtentWidth)
                offset = Math.Max(0, ExtentWidth - ViewportWidth);

            _offset.X = offset;
            _scrollOwner?.InvalidateScrollInfo();
            InvalidateMeasure();
        }

        public void SetVerticalOffset(double offset)
        {
            if (offset < 0)
                offset = 0;
            if (offset + ViewportHeight > ExtentHeight)
                offset = Math.Max(0, ExtentHeight - ViewportHeight);

            _offset.Y = offset;
            _scrollOwner?.InvalidateScrollInfo();
            InvalidateMeasure();
        }

        #endregion
    }
}
