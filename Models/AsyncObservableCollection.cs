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
    /// Advanced ObservableCollection that supports time-sliced async operations
    /// to prevent UI thread blocking with massive datasets (19K+ items)
    /// </summary>
    public class AsyncObservableCollection<T> : ObservableCollection<T>
    {
        private bool _suppressNotification = false;
        private readonly Dispatcher _dispatcher;

        public AsyncObservableCollection()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        /// <summary>
        /// Gets a value indicating whether the collection is read-only
        /// </summary>
        public bool IsReadOnly => false;

        /// <summary>
        /// Adds items in time-sliced chunks to prevent UI freezing
        /// </summary>
        public async Task AddRangeAsync(IEnumerable<T> items, int chunkSize = 100, int delayMs = 1)
        {
            if (items == null) return;

            var itemsList = items.ToList();
            if (itemsList.Count == 0) return;

            // Process in chunks to keep UI responsive
            for (int i = 0; i < itemsList.Count; i += chunkSize)
            {
                var chunk = itemsList.Skip(i).Take(chunkSize);
                
                // Add chunk without notifications
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
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

                // Yield control back to UI thread
                if (i + chunkSize < itemsList.Count)
                {
                    await Task.Delay(delayMs);
                }
            }
        }

        /// <summary>
        /// Replaces all items with time-slicing for large datasets
        /// </summary>
        public async Task ReplaceAllAsync(IEnumerable<T> items, int chunkSize = 500, int delayMs = 1)
        {
            if (items == null) return;

            var itemsList = items.ToList();

            // Clear existing items
            _suppressNotification = true;
            try
            {
                Items.Clear();
            }
            finally
            {
                _suppressNotification = false;
            }

            // Fire clear notification
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

            // Add new items in chunks
            await AddRangeAsync(itemsList, chunkSize, delayMs);
        }

        /// <summary>
        /// Synchronous bulk operation for smaller datasets
        /// </summary>
        public void ReplaceAll(IEnumerable<T> items)
        {
            if (items == null) return;

            var itemsList = items.ToList();

            _suppressNotification = true;
            try
            {
                Items.Clear();
                foreach (var item in itemsList)
                {
                    Items.Add(item);
                }
            }
            finally
            {
                _suppressNotification = false;
            }

            // Fire single notification for complete replacement
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
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

