using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VPM.Windows
{
    /// <summary>
    /// Custom WrapPanel that stretches items in partial rows when ImageMatchWidth is enabled
    /// </summary>
    public class ImageWrapPanel : WrapPanel
    {
        public static readonly DependencyProperty ImageMatchWidthProperty = DependencyProperty.Register(
            "ImageMatchWidth", typeof(bool), typeof(ImageWrapPanel), 
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
            "Columns", typeof(int), typeof(ImageWrapPanel), 
            new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public bool ImageMatchWidth
        {
            get { return (bool)GetValue(ImageMatchWidthProperty); }
            set { SetValue(ImageMatchWidthProperty, value); }
        }

        public int Columns
        {
            get { return (int)GetValue(ColumnsProperty); }
            set { SetValue(ColumnsProperty, value); }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (!ImageMatchWidth || Columns <= 0)
            {
                return base.MeasureOverride(availableSize);
            }

            var children = this.InternalChildren;
            if (children.Count == 0)
                return new Size(0, 0);

            double itemMargin = 3.0; // Must match the Grid Margin in the template
            
            // Account for scrollbar width - use SystemParameters to get actual scrollbar width
            double scrollbarWidth = SystemParameters.VerticalScrollBarWidth;
            
            // Calculate available width (accounting for margins on both sides)
            double totalMarginWidth = 2 * itemMargin;
            double availableRowWidth = availableSize.Width - totalMarginWidth - scrollbarWidth;
            
            // First pass: determine how many items are in each row
            // We need to know the row distribution before we can calculate actual item widths
            List<int> rowItemCounts = new List<int>();
            double currentRowWidth = itemMargin;  // Start with left margin
            int itemsInCurrentRow = 0;
            
            // Estimate: assume items are roughly square with width = availableRowWidth / Columns
            double estimatedItemWidth = availableRowWidth / Columns;

            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child == null) continue;

                double itemTotalWidth = estimatedItemWidth + itemMargin;  // Item width + right margin
                
                // Check if this item fits in the current row
                if (currentRowWidth + itemTotalWidth > availableRowWidth && itemsInCurrentRow > 0)
                {
                    // Row is full, start a new row
                    rowItemCounts.Add(itemsInCurrentRow);
                    currentRowWidth = itemMargin;  // Reset to left margin
                    itemsInCurrentRow = 0;
                }

                currentRowWidth += itemTotalWidth;
                itemsInCurrentRow++;
            }

            // Add the last row if it has items
            if (itemsInCurrentRow > 0)
            {
                rowItemCounts.Add(itemsInCurrentRow);
            }

            // Second pass: calculate total height based on rows
            double totalHeight = itemMargin;  // Start with top margin
            double bottomPadding = 20.0; // Extra padding at bottom to avoid overlay controls
            int childIndex = 0;
            
            foreach (int itemsInRow in rowItemCounts)
            {
                double rowHeight = 0;
                
                // Calculate the width each item will have in this row
                double gapWidth = (itemsInRow - 1) * itemMargin;
                double itemWidth = (availableRowWidth - gapWidth) / itemsInRow;
                
                // Since we enforce square aspect ratio in ArrangeOverride, use itemWidth as height
                rowHeight = itemWidth;
                
                // Measure children with the calculated size
                for (int i = 0; i < itemsInRow && childIndex < children.Count; i++)
                {
                    var child = children[childIndex];
                    if (child != null)
                    {
                        // Measure with the actual width this item will have and square height
                        child.Measure(new Size(itemWidth, itemWidth));
                    }
                    childIndex++;
                }
                
                totalHeight += rowHeight + itemMargin;  // Add row height plus margin after row
            }

            return new Size(availableSize.Width, totalHeight + bottomPadding);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (!ImageMatchWidth || Columns <= 0)
            {
                return base.ArrangeOverride(finalSize);
            }

            // When match width is enabled, calculate row widths based on actual items per row
            var children = this.InternalChildren;
            if (children.Count == 0)
                return finalSize;

            double itemMargin = 3.0; // Must match the Grid Margin in the template
            
            // Account for scrollbar width - use SystemParameters to get actual scrollbar width
            double scrollbarWidth = SystemParameters.VerticalScrollBarWidth;
            
            // Calculate available width (accounting for margins on both sides)
            double totalMarginWidth = 2 * itemMargin;
            double availableRowWidth = finalSize.Width - totalMarginWidth - scrollbarWidth;
            
            // First pass: determine how many items are in each row by simulating layout
            List<int> rowItemCounts = new List<int>();
            double currentRowWidth = itemMargin;  // Start with left margin
            int itemsInCurrentRow = 0;

            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child == null) continue;

                var childSize = child.DesiredSize;
                double itemTotalWidth = childSize.Width + itemMargin;  // Item width + right margin
                
                // Check if this item fits in the current row
                if (currentRowWidth + itemTotalWidth > availableRowWidth && itemsInCurrentRow > 0)
                {
                    // Row is full, start a new row
                    rowItemCounts.Add(itemsInCurrentRow);
                    currentRowWidth = itemMargin;  // Reset to left margin
                    itemsInCurrentRow = 0;
                }

                currentRowWidth += itemTotalWidth;
                itemsInCurrentRow++;
            }

            // Add the last row if it has items
            if (itemsInCurrentRow > 0)
            {
                rowItemCounts.Add(itemsInCurrentRow);
            }

            // Second pass: arrange items with proper widths based on items per row
            double x = 0;
            double y = itemMargin;  // Start with top margin
            double rowHeight = 0;
            int rowIndex = 0;
            int itemsProcessedInRow = 0;

            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child == null) continue;

                // Check if we're starting a new row
                if (rowIndex < rowItemCounts.Count && itemsProcessedInRow >= rowItemCounts[rowIndex])
                {
                    y += rowHeight + itemMargin;  // Add row height plus margin between rows
                    x = 0;
                    rowHeight = 0;
                    rowIndex++;
                    itemsProcessedInRow = 0;
                }

                // Calculate width for this row based on actual item count in row
                int itemsInThisRow = rowIndex < rowItemCounts.Count ? rowItemCounts[rowIndex] : 1;
                double gapWidth = (itemsInThisRow - 1) * itemMargin;
                double itemWidth = (availableRowWidth - gapWidth) / itemsInThisRow;
                itemsProcessedInRow++;

                // Set the Grid wrapper width and height to create square items
                // Also set the LazyLoadImage width/height directly to bypass binding
                if (child is FrameworkElement fe)
                {
                    fe.Width = itemWidth;
                    fe.Height = itemWidth;  // Square aspect ratio
                    
                    // Find and set LazyLoadImage dimensions
                    var lazyImage = FindVisualChild<Windows.LazyLoadImage>(fe);
                    if (lazyImage != null)
                    {
                        // Clear all width/height related bindings
                        lazyImage.ClearValue(FrameworkElement.WidthProperty);
                        lazyImage.ClearValue(FrameworkElement.HeightProperty);
                        lazyImage.ClearValue(FrameworkElement.MaxWidthProperty);
                        lazyImage.ClearValue(FrameworkElement.MaxHeightProperty);
                        lazyImage.ClearValue(FrameworkElement.MinWidthProperty);
                        lazyImage.ClearValue(FrameworkElement.MinHeightProperty);
                        
                        // Set explicit values
                        lazyImage.Width = itemWidth;
                        lazyImage.Height = itemWidth;
                        lazyImage.MaxWidth = itemWidth;
                        lazyImage.MaxHeight = itemWidth;
                    }
                }

                rowHeight = Math.Max(rowHeight, itemWidth);  // Use itemWidth for height since items are square
                child.Arrange(new Rect(x + itemMargin, y, itemWidth, itemWidth));
                x += itemWidth + itemMargin;
            }

            // Calculate actual content height (y is at start of last row, add last row height + bottom margin)
            double bottomPadding = 20.0; // Extra padding at bottom to avoid overlay controls
            double actualHeight = y + rowHeight + itemMargin + bottomPadding;
            
            // Return the actual content size so ScrollViewer knows the full extent
            return new Size(finalSize.Width, actualHeight);
        }

        private T FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(obj, i);
                if (child is T t)
                    return t;
                
                T childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                    return childOfChild;
            }
            return null;
        }
    }
}
