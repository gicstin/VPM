using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VPM.Services
{
    /// <summary>
    /// Thread-safe work queue with priority support and backpressure handling.
    /// Manages task ordering, dequeuing, and queue statistics.
    /// 
    /// Features:
    /// - Priority-based task ordering (higher priority = dequeued first)
    /// - Backpressure support (max queue size)
    /// - Thread-safe operations with minimal locking
    /// - Queue statistics and diagnostics
    /// - Task state tracking
    /// </summary>
    public class WorkQueue
    {
        private readonly PriorityQueue<WorkTask> _queue;
        private readonly object _queueLock = new object();
        private readonly int _maxQueueSize;
        private readonly SemaphoreSlim _availableSlot;
        private volatile int _currentSize = 0;

        /// <summary>
        /// Queue statistics
        /// </summary>
        public class QueueStatistics
        {
            public int CurrentSize { get; set; }
            public int MaxSize { get; set; }
            public long TotalEnqueued { get; set; }
            public long TotalDequeued { get; set; }
            public long TotalRejected { get; set; }
            public double AverageQueueDepth { get; set; }
            public int PendingTasks { get; set; }
            public int RunningTasks { get; set; }
            public int CompletedTasks { get; set; }
            public int FailedTasks { get; set; }
        }

        private long _totalEnqueued = 0;
        private long _totalDequeued = 0;
        private long _totalRejected = 0;
        private double _averageQueueDepth = 0;
        private int _sampleCount = 0;

        /// <summary>
        /// Initializes a new work queue
        /// </summary>
        /// <param name="maxQueueSize">Maximum queue size (0 = unlimited)</param>
        public WorkQueue(int maxQueueSize = 10000)
        {
            _maxQueueSize = maxQueueSize;
            _queue = new PriorityQueue<WorkTask>();
            _availableSlot = new SemaphoreSlim(maxQueueSize > 0 ? maxQueueSize : int.MaxValue);
        }

        /// <summary>
        /// Enqueue a task (blocks if queue is full)
        /// </summary>
        public bool TryEnqueue(WorkTask task, int timeoutMs = -1)
        {
            if (task == null)
                return false;

            // Wait for available slot (with timeout support)
            bool slotAvailable = timeoutMs < 0 
                ? _availableSlot.Wait(timeoutMs == -1 ? Timeout.Infinite : timeoutMs)
                : _availableSlot.Wait(timeoutMs);

            if (!slotAvailable)
            {
                Interlocked.Increment(ref _totalRejected);
                return false;
            }

            lock (_queueLock)
            {
                _queue.Enqueue(task, task.Priority);
                _currentSize++;
                Interlocked.Increment(ref _totalEnqueued);
                UpdateAverageQueueDepth();
            }

            return true;
        }

        /// <summary>
        /// Dequeue the highest priority task
        /// </summary>
        public bool TryDequeue(out WorkTask task, int timeoutMs = 0)
        {
            task = null;

            lock (_queueLock)
            {
                if (_queue.Count == 0)
                    return false;

                task = _queue.Dequeue();
                _currentSize--;
                Interlocked.Increment(ref _totalDequeued);
                UpdateAverageQueueDepth();
            }

            _availableSlot.Release();
            return true;
        }

        /// <summary>
        /// Peek at the highest priority task without removing it
        /// </summary>
        public bool TryPeek(out WorkTask task)
        {
            task = null;

            lock (_queueLock)
            {
                if (_queue.Count == 0)
                    return false;

                task = _queue.Peek();
            }

            return true;
        }

        /// <summary>
        /// Get current queue size
        /// </summary>
        public int Count
        {
            get
            {
                lock (_queueLock)
                {
                    return _queue.Count;
                }
            }
        }

        /// <summary>
        /// Check if queue is empty
        /// </summary>
        public bool IsEmpty
        {
            get
            {
                lock (_queueLock)
                {
                    return _queue.Count == 0;
                }
            }
        }

        /// <summary>
        /// Clear all tasks from queue
        /// </summary>
        public void Clear()
        {
            lock (_queueLock)
            {
                int count = _queue.Count;
                _queue.Clear();
                _currentSize = 0;

                // Release all semaphore slots
                for (int i = 0; i < count; i++)
                {
                    _availableSlot.Release();
                }
            }
        }

        /// <summary>
        /// Get queue statistics
        /// </summary>
        public QueueStatistics GetStatistics()
        {
            lock (_queueLock)
            {
                var allTasks = _queue.GetAll().ToList();
                return new QueueStatistics
                {
                    CurrentSize = _currentSize,
                    MaxSize = _maxQueueSize,
                    TotalEnqueued = _totalEnqueued,
                    TotalDequeued = _totalDequeued,
                    TotalRejected = _totalRejected,
                    AverageQueueDepth = _averageQueueDepth,
                    PendingTasks = allTasks.Count(t => t.State == TaskState.Pending),
                    RunningTasks = allTasks.Count(t => t.State == TaskState.Running),
                    CompletedTasks = allTasks.Count(t => t.State == TaskState.Completed),
                    FailedTasks = allTasks.Count(t => t.State == TaskState.Failed)
                };
            }
        }

        /// <summary>
        /// Update average queue depth for monitoring
        /// </summary>
        private void UpdateAverageQueueDepth()
        {
            _sampleCount++;
            _averageQueueDepth = (_averageQueueDepth * (_sampleCount - 1) + _currentSize) / _sampleCount;
        }
    }

    /// <summary>
    /// Simple priority queue implementation using SortedDictionary
    /// </summary>
    internal class PriorityQueue<T> where T : class
    {
        private readonly SortedDictionary<int, Queue<T>> _queues = new SortedDictionary<int, Queue<T>>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        private int _count = 0;

        public int Count 
        { 
            get { return _count; }
        }

        public void Enqueue(T item, int priority)
        {
            if (!_queues.ContainsKey(priority))
            {
                _queues[priority] = new Queue<T>();
            }

            _queues[priority].Enqueue(item);
            _count++;
        }

        public T Dequeue()
        {
            if (_count == 0)
                throw new InvalidOperationException("Queue is empty");

            var kvp = _queues.First();
            var item = kvp.Value.Dequeue();
            _count--;

            if (kvp.Value.Count == 0)
            {
                _queues.Remove(kvp.Key);
            }

            return item;
        }

        public T Peek()
        {
            if (_count == 0)
                throw new InvalidOperationException("Queue is empty");

            return _queues.First().Value.Peek();
        }

        public void Clear()
        {
            _queues.Clear();
            _count = 0;
        }

        public IEnumerable<T> GetAll()
        {
            foreach (var kvp in _queues)
            {
                foreach (var item in kvp.Value)
                {
                    yield return item;
                }
            }
        }
    }
}
