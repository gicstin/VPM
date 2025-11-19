using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VPM.Services
{
    /// <summary>
    /// Dead letter queue for managing failed tasks.
    /// Provides persistence, analysis, and recovery mechanisms for failed operations.
    /// 
    /// Features:
    /// - Task failure persistence
    /// - Failure analysis and categorization
    /// - Automatic retry scheduling
    /// - Dead letter metrics
    /// - Batch processing of failed tasks
    /// - Configurable retention policies
    /// </summary>
    public class DeadLetterQueue
    {
        /// <summary>
        /// Dead letter entry
        /// </summary>
        public class DeadLetterEntry
        {
            public string EntryId { get; set; } = Guid.NewGuid().ToString("N");
            public string TaskId { get; set; }
            public string TaskName { get; set; }
            public string TaskType { get; set; }
            public DateTime FailureTime { get; set; }
            public Exception Exception { get; set; }
            public string ExceptionMessage { get; set; }
            public string ExceptionType { get; set; }
            public int RetryCount { get; set; }
            public int MaxRetries { get; set; }
            public DateTime? NextRetryTime { get; set; }
            public FailureCategory Category { get; set; }
            public string Details { get; set; }
            public bool IsResolved { get; set; }
        }

        /// <summary>
        /// Failure categorization
        /// </summary>
        public enum FailureCategory
        {
            Unknown,
            Transient,           // Temporary failures (timeout, I/O)
            Permanent,           // Permanent failures (invalid input)
            ResourceExhaustion,  // Out of memory, disk space
            ConfigurationError,  // Invalid configuration
            ExternalService,     // External service failure
            Timeout,             // Operation timeout
            Cancelled            // Operation cancelled
        }

        /// <summary>
        /// Dead letter queue configuration
        /// </summary>
        public class DeadLetterConfig
        {
            /// <summary>
            /// Maximum entries to retain
            /// </summary>
            public int MaxEntries { get; set; } = 10000;

            /// <summary>
            /// Retention time for resolved entries (milliseconds)
            /// </summary>
            public int RetentionTimeMs { get; set; } = 86400000; // 24 hours

            /// <summary>
            /// Enable automatic retry scheduling
            /// </summary>
            public bool EnableAutoRetry { get; set; } = true;

            /// <summary>
            /// Initial retry delay (milliseconds)
            /// </summary>
            public int InitialRetryDelayMs { get; set; } = 5000;

            /// <summary>
            /// Maximum retry delay (milliseconds)
            /// </summary>
            public int MaxRetryDelayMs { get; set; } = 300000; // 5 minutes
        }

        /// <summary>
        /// Dead letter statistics
        /// </summary>
        public class DeadLetterStats
        {
            public int TotalEntries { get; set; }
            public int UnresolvedEntries { get; set; }
            public int ResolvedEntries { get; set; }
            public Dictionary<FailureCategory, int> EntriesByCategory { get; set; }
            public Dictionary<string, int> EntriesByTaskType { get; set; }
            public int PendingRetries { get; set; }
            public double AverageRetryCount { get; set; }
        }

        private readonly ConcurrentDictionary<string, DeadLetterEntry> _entries;
        private readonly DeadLetterConfig _config;
        private readonly object _statsLock = new object();

        public DeadLetterQueue(DeadLetterConfig config = null)
        {
            _entries = new ConcurrentDictionary<string, DeadLetterEntry>();
            _config = config ?? new DeadLetterConfig();
        }

        /// <summary>
        /// Add failed task to dead letter queue
        /// </summary>
        public DeadLetterEntry AddEntry(WorkTask task, Exception exception, int maxRetries = 3)
        {
            var category = CategorizeFailure(exception);

            var entry = new DeadLetterEntry
            {
                TaskId = task.TaskId,
                TaskName = task.TaskName,
                TaskType = task.GetTaskType(),
                FailureTime = DateTime.UtcNow,
                Exception = exception,
                ExceptionMessage = exception?.Message ?? "Unknown error",
                ExceptionType = exception?.GetType().Name ?? "Unknown",
                RetryCount = 0,
                MaxRetries = maxRetries,
                Category = category,
                Details = task.ErrorMessage
            };

            // Schedule first retry if enabled
            if (_config.EnableAutoRetry && category == FailureCategory.Transient)
            {
                entry.NextRetryTime = DateTime.UtcNow.AddMilliseconds(_config.InitialRetryDelayMs);
            }

            _entries.TryAdd(entry.EntryId, entry);

            // Enforce max entries
            if (_entries.Count > _config.MaxEntries)
            {
                var oldestResolved = _entries.Values
                    .Where(e => e.IsResolved)
                    .OrderBy(e => e.FailureTime)
                    .FirstOrDefault();

                if (oldestResolved != null)
                {
                    _entries.TryRemove(oldestResolved.EntryId, out _);
                }
            }

            return entry;
        }

        /// <summary>
        /// Get entry by ID
        /// </summary>
        public DeadLetterEntry GetEntry(string entryId)
        {
            _entries.TryGetValue(entryId, out var entry);
            return entry;
        }

        /// <summary>
        /// Get all unresolved entries
        /// </summary>
        public List<DeadLetterEntry> GetUnresolvedEntries()
        {
            return _entries.Values
                .Where(e => !e.IsResolved)
                .OrderByDescending(e => e.FailureTime)
                .ToList();
        }

        /// <summary>
        /// Get entries pending retry
        /// </summary>
        public List<DeadLetterEntry> GetPendingRetries()
        {
            var now = DateTime.UtcNow;
            return _entries.Values
                .Where(e => !e.IsResolved && 
                           e.NextRetryTime.HasValue && 
                           e.NextRetryTime <= now &&
                           e.RetryCount < e.MaxRetries)
                .OrderBy(e => e.NextRetryTime)
                .ToList();
        }

        /// <summary>
        /// Record retry attempt
        /// </summary>
        public void RecordRetryAttempt(string entryId, bool success)
        {
            if (!_entries.TryGetValue(entryId, out var entry))
                return;

            entry.RetryCount++;

            if (success)
            {
                entry.IsResolved = true;
            }
            else if (entry.RetryCount < entry.MaxRetries)
            {
                // Schedule next retry with exponential backoff
                int delayMs = (int)Math.Min(
                    _config.MaxRetryDelayMs,
                    _config.InitialRetryDelayMs * Math.Pow(2, entry.RetryCount - 1));

                entry.NextRetryTime = DateTime.UtcNow.AddMilliseconds(delayMs);
            }
            else
            {
                entry.IsResolved = true;
            }
        }

        /// <summary>
        /// Resolve entry manually
        /// </summary>
        public void ResolveEntry(string entryId)
        {
            if (_entries.TryGetValue(entryId, out var entry))
            {
                entry.IsResolved = true;
            }
        }

        /// <summary>
        /// Get entries by category
        /// </summary>
        public List<DeadLetterEntry> GetEntriesByCategory(FailureCategory category)
        {
            return _entries.Values
                .Where(e => e.Category == category)
                .OrderByDescending(e => e.FailureTime)
                .ToList();
        }

        /// <summary>
        /// Get entries by task type
        /// </summary>
        public List<DeadLetterEntry> GetEntriesByTaskType(string taskType)
        {
            return _entries.Values
                .Where(e => e.TaskType == taskType)
                .OrderByDescending(e => e.FailureTime)
                .ToList();
        }

        /// <summary>
        /// Get dead letter statistics
        /// </summary>
        public DeadLetterStats GetStatistics()
        {
            lock (_statsLock)
            {
                var allEntries = _entries.Values.ToList();
                var unresolvedEntries = allEntries.Where(e => !e.IsResolved).ToList();

                var stats = new DeadLetterStats
                {
                    TotalEntries = allEntries.Count,
                    UnresolvedEntries = unresolvedEntries.Count,
                    ResolvedEntries = allEntries.Count - unresolvedEntries.Count,
                    EntriesByCategory = new Dictionary<FailureCategory, int>(),
                    EntriesByTaskType = new Dictionary<string, int>(),
                    PendingRetries = GetPendingRetries().Count,
                    AverageRetryCount = allEntries.Count > 0 ? allEntries.Average(e => e.RetryCount) : 0
                };

                // Count by category
                foreach (var category in Enum.GetValues(typeof(FailureCategory)).Cast<FailureCategory>())
                {
                    int count = allEntries.Count(e => e.Category == category);
                    if (count > 0)
                        stats.EntriesByCategory[category] = count;
                }

                // Count by task type
                foreach (var taskType in allEntries.Select(e => e.TaskType).Distinct())
                {
                    int count = allEntries.Count(e => e.TaskType == taskType);
                    stats.EntriesByTaskType[taskType] = count;
                }

                return stats;
            }
        }

        /// <summary>
        /// Categorize failure based on exception type
        /// </summary>
        private FailureCategory CategorizeFailure(Exception exception)
        {
            if (exception == null)
                return FailureCategory.Unknown;

            var exceptionType = exception.GetType();

            if (exceptionType == typeof(TimeoutException) || exceptionType.Name.Contains("Timeout"))
                return FailureCategory.Timeout;

            if (exceptionType == typeof(OperationCanceledException))
                return FailureCategory.Cancelled;

            if (exceptionType == typeof(OutOfMemoryException) || 
                exceptionType == typeof(System.IO.IOException))
                return FailureCategory.ResourceExhaustion;

            if (exceptionType == typeof(ArgumentException) || 
                exceptionType == typeof(InvalidOperationException))
                return FailureCategory.Permanent;

            if (exceptionType == typeof(System.Net.Http.HttpRequestException) ||
                exceptionType.Name.Contains("Service"))
                return FailureCategory.ExternalService;

            if (exceptionType == typeof(System.Configuration.ConfigurationErrorsException))
                return FailureCategory.ConfigurationError;

            // Transient by default for unknown exceptions
            return FailureCategory.Transient;
        }

        /// <summary>
        /// Clean up old resolved entries
        /// </summary>
        public int CleanupOldEntries()
        {
            var cutoffTime = DateTime.UtcNow.AddMilliseconds(-_config.RetentionTimeMs);
            var entriesToRemove = _entries.Values
                .Where(e => e.IsResolved && e.FailureTime < cutoffTime)
                .ToList();

            int removedCount = 0;
            foreach (var entry in entriesToRemove)
            {
                if (_entries.TryRemove(entry.EntryId, out _))
                {
                    removedCount++;
                }
            }

            return removedCount;
        }

        /// <summary>
        /// Get formatted report
        /// </summary>
        public string GetFormattedReport()
        {
            var stats = GetStatistics();
            var report = new System.Text.StringBuilder();

            report.AppendLine("=== Dead Letter Queue Report ===");
            report.AppendLine();
            report.AppendLine($"Total Entries: {stats.TotalEntries}");
            report.AppendLine($"Unresolved: {stats.UnresolvedEntries}");
            report.AppendLine($"Resolved: {stats.ResolvedEntries}");
            report.AppendLine($"Pending Retries: {stats.PendingRetries}");
            report.AppendLine($"Average Retry Count: {stats.AverageRetryCount:F1}");
            report.AppendLine();

            if (stats.EntriesByCategory.Count > 0)
            {
                report.AppendLine("Failures by Category:");
                foreach (var kvp in stats.EntriesByCategory.OrderByDescending(x => x.Value))
                {
                    report.AppendLine($"  {kvp.Key}: {kvp.Value}");
                }
                report.AppendLine();
            }

            if (stats.EntriesByTaskType.Count > 0)
            {
                report.AppendLine("Failures by Task Type:");
                foreach (var kvp in stats.EntriesByTaskType.OrderByDescending(x => x.Value))
                {
                    report.AppendLine($"  {kvp.Key}: {kvp.Value}");
                }
            }

            return report.ToString();
        }

        /// <summary>
        /// Clear all entries
        /// </summary>
        public void Clear()
        {
            _entries.Clear();
        }
    }
}
