using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace VPM.Models
{
    /// <summary>
    /// Optimized ObservableCollection that supports bulk operations without triggering
    /// individual change notifications for each item, designed for large datasets
    /// </summary>
    public class OptimizedObservableCollection<T> : ObservableCollection<T>
    {
        private bool _suppressNotification = false;

        /// <summary>
        /// Adds multiple items efficiently without triggering individual notifications
        /// </summary>
        public void AddRange(IEnumerable<T> items)
        {
            if (items == null) return;

            // Use ToList() only once and check count
            var itemsList = items as List<T> ?? items.ToList();
            if (itemsList.Count == 0) return;

            _suppressNotification = true;
            try
            {
                // Optimize for List<T> by using capacity
                if (Items is List<T> list)
                {
                    var newCapacity = list.Count + itemsList.Count;
                    if (list.Capacity < newCapacity)
                    {
                        list.Capacity = newCapacity;
                    }
                }

                // Use AddRange if available for better performance
                if (Items is List<T> targetList)
                {
                    targetList.AddRange(itemsList);
                }
                else
                {
                    foreach (var item in itemsList)
                    {
                        Items.Add(item);
                    }
                }
            }
            finally
            {
                _suppressNotification = false;
            }

            // Fire single notification for all added items
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        /// <summary>
        /// Replaces all items efficiently with a single notification
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

        /// <summary>
        /// Clears all items efficiently
        /// </summary>
        public new void Clear()
        {
            if (Items.Count == 0) return;

            _suppressNotification = true;
            try
            {
                Items.Clear();
            }
            finally
            {
                _suppressNotification = false;
            }

            // Fire single notification for clear
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

