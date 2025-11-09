using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace VPM.Models
{
    /// <summary>
    /// Lazy loading collection that initially shows only a subset of items
    /// and loads more on demand - designed for massive datasets (19K+ items)
    /// </summary>
    public class LazyLoadingCollection<T> : ObservableCollection<T>
    {
        private readonly List<T> _allItems = new List<T>();
        private readonly int _initialLoadCount;
        private readonly int _loadMoreCount;
        private bool _suppressNotification = false;

        public LazyLoadingCollection(int initialLoadCount = 1000, int loadMoreCount = 500)
        {
            _initialLoadCount = initialLoadCount;
            _loadMoreCount = loadMoreCount;
        }

        /// <summary>
        /// Total number of items available (including not yet loaded)
        /// </summary>
        public int TotalCount => _allItems.Count;

        /// <summary>
        /// Number of items currently loaded in the UI
        /// </summary>
        public int LoadedCount => Items.Count;

        /// <summary>
        /// Whether all items have been loaded
        /// </summary>
        public bool IsFullyLoaded => LoadedCount >= TotalCount;

        /// <summary>
        /// Sets all available items and loads initial subset
        /// </summary>
        public void SetAllItems(IEnumerable<T> items)
        {
            if (items == null) return;

            _allItems.Clear();
            _allItems.AddRange(items);

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(TotalCount)));

            // Load initial subset
            LoadInitialItems();
        }

        /// <summary>
        /// Adds items to the collection (for progressive loading)
        /// </summary>
        public void AddItems(IEnumerable<T> items)
        {
            if (items == null) return;

            var incomingItems = items.ToList();
            if (incomingItems.Count == 0) return;

            _allItems.AddRange(incomingItems);

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(TotalCount)));

            var newlyAvailable = _allItems.Count - Items.Count;
            if (newlyAvailable <= 0) return;

            var desiredVisibleIncrease = Math.Max(_loadMoreCount, incomingItems.Count);
            var toLoad = Math.Min(newlyAvailable, desiredVisibleIncrease);
            LoadNextItems(toLoad);
        }

        /// <summary>
        /// Loads initial subset of items for immediate display
        /// </summary>
        private void LoadInitialItems()
        {
            _suppressNotification = true;
            try
            {
                Items.Clear();
            }
            finally
            {
                _suppressNotification = false;
            }

            LoadNextItems(_initialLoadCount);
        }

        /// <summary>
        /// Loads more items (called when user scrolls near end)
        /// </summary>
        public void LoadMoreItems()
        {
            LoadNextItems(_loadMoreCount);
        }

        /// <summary>
        /// Loads all remaining items at once
        /// </summary>
        public void LoadAllItems()
        {
            if (IsFullyLoaded) return;
            var remaining = _allItems.Count - Items.Count;
            LoadNextItems(remaining);
        }

        private void LoadNextItems(int count)
        {
            if (count <= 0 || IsFullyLoaded)
            {
                return;
            }

            var currentCount = Items.Count;
            var itemsToLoad = _allItems.Skip(currentCount).Take(count).ToList();
            if (itemsToLoad.Count == 0)
            {
                return;
            }

            _suppressNotification = true;
            try
            {
                foreach (var item in itemsToLoad)
                {
                    Items.Add(item);
                }
            }
            finally
            {
                _suppressNotification = false;
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(LoadedCount)));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        /// <summary>
        /// Loads all items asynchronously with time-slicing
        /// </summary>
        public async Task LoadAllItemsAsync(int chunkSize = 500, int delayMs = 10)
        {
            if (IsFullyLoaded) return;

            var currentCount = Items.Count;
            var remainingItems = _allItems.Skip(currentCount).ToList();

            // Process in chunks
            for (int i = 0; i < remainingItems.Count; i += chunkSize)
            {
                var chunk = remainingItems.Skip(i).Take(chunkSize);

                _suppressNotification = true;
                try
                {
                    foreach (var item in chunk)
                    {
                        Items.Add(item);
                    }
                }
                finally
                {
                    _suppressNotification = false;
                }

                // Fire notification for this chunk
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(LoadedCount)));
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

                // Yield control back to UI thread
                if (i + chunkSize < remainingItems.Count)
                {
                    await Task.Delay(delayMs);
                }
            }
        }

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (!_suppressNotification)
            {
                base.OnCollectionChanged(e);
            }
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (!_suppressNotification)
            {
                base.OnPropertyChanged(e);
            }
        }
    }
}

