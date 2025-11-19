using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace VPM.Services
{
    /// <summary>
    /// Simple performance timing system for tracking optimization process durations
    /// Provides detailed metrics for each phase of optimization
    /// </summary>
    public class OptimizationTimer
    {
        private readonly Dictionary<string, TimingEntry> _timings = new Dictionary<string, TimingEntry>();
        private readonly Stopwatch _globalStopwatch = new Stopwatch();
        private readonly object _lock = new object();

        private class TimingEntry
        {
            public Stopwatch Stopwatch { get; set; }
            public long TotalMilliseconds { get; set; }
            public int CallCount { get; set; }
            public long MinMilliseconds { get; set; } = long.MaxValue;
            public long MaxMilliseconds { get; set; } = long.MinValue;
        }

        public OptimizationTimer()
        {
            _globalStopwatch.Start();
        }

        /// <summary>
        /// Start timing a named operation
        /// </summary>
        public void Start(string operationName)
        {
            lock (_lock)
            {
                if (!_timings.ContainsKey(operationName))
                {
                    _timings[operationName] = new TimingEntry { Stopwatch = new Stopwatch() };
                }

                _timings[operationName].Stopwatch.Restart();
            }
        }

        /// <summary>
        /// Stop timing and record the duration
        /// </summary>
        public long Stop(string operationName)
        {
            lock (_lock)
            {
                if (!_timings.ContainsKey(operationName))
                    return 0;

                var entry = _timings[operationName];
                entry.Stopwatch.Stop();
                
                long elapsed = entry.Stopwatch.ElapsedMilliseconds;
                entry.TotalMilliseconds += elapsed;
                entry.CallCount++;
                entry.MinMilliseconds = Math.Min(entry.MinMilliseconds, elapsed);
                entry.MaxMilliseconds = Math.Max(entry.MaxMilliseconds, elapsed);

                System.Diagnostics.Debug.WriteLine(string.Format("[TIMER] {0}: {1}ms (Total: {2}ms, Calls: {3})", 
                    operationName, elapsed, entry.TotalMilliseconds, entry.CallCount));
                
                return elapsed;
            }
        }

        /// <summary>
        /// Get timing for a specific operation
        /// </summary>
        public (long total, int count, long min, long max, double average) GetTiming(string operationName)
        {
            lock (_lock)
            {
                if (!_timings.ContainsKey(operationName))
                    return (0, 0, 0, 0, 0);

                var entry = _timings[operationName];
                double average = entry.CallCount > 0 ? (double)entry.TotalMilliseconds / entry.CallCount : 0;
                return (entry.TotalMilliseconds, entry.CallCount, entry.MinMilliseconds, entry.MaxMilliseconds, average);
            }
        }

        /// <summary>
        /// Get all timings as a formatted report
        /// </summary>
        public string GetReport()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.AppendLine("\n========================================================================");
                sb.AppendLine("                    OPTIMIZATION PERFORMANCE REPORT");
                sb.AppendLine("========================================================================\n");

                if (_timings.Count == 0)
                {
                    sb.AppendLine("No timing data collected.");
                    return sb.ToString();
                }

                // Sort by total time descending
                var sortedTimings = _timings
                    .OrderByDescending(kvp => kvp.Value.TotalMilliseconds)
                    .ToList();

                long totalTime = sortedTimings.Sum(kvp => kvp.Value.TotalMilliseconds);

                sb.AppendLine(string.Format("{0,-40} {1,12} {2,6} {3,7} {4,10} {5,10} {6,10}", 
                    "Operation", "Time", "%", "Count", "Avg", "Min", "Max"));
                sb.AppendLine(new string('-', 95));

                foreach (var kvp in sortedTimings)
                {
                    var entry = kvp.Value;
                    double percentage = totalTime > 0 ? (100.0 * entry.TotalMilliseconds / totalTime) : 0;
                    double average = entry.CallCount > 0 ? (double)entry.TotalMilliseconds / entry.CallCount : 0;
                    
                    string operationName = kvp.Key.Length > 40 ? kvp.Key.Substring(0, 37) + "..." : kvp.Key;
                    
                    sb.AppendLine(string.Format("{0,-40} {1,10}ms {2,5:F1}% {3,7} {4,9:F0}ms {5,9}ms {6,9}ms",
                        operationName, entry.TotalMilliseconds, percentage, entry.CallCount, average, 
                        entry.MinMilliseconds, entry.MaxMilliseconds));
                }

                sb.AppendLine(new string('-', 95));
                sb.AppendLine(string.Format("{0,-40} {1,10}ms {2,5:F1}%", "TOTAL", totalTime, 100.0));
                sb.AppendLine();

                // Add global elapsed time
                _globalStopwatch.Stop();
                sb.AppendLine(string.Format("Total Elapsed Time: {0}ms ({1:F2}s)", 
                    _globalStopwatch.ElapsedMilliseconds, _globalStopwatch.Elapsed.TotalSeconds));
                sb.AppendLine(string.Format("Unaccounted Time: {0}ms", 
                    _globalStopwatch.ElapsedMilliseconds - totalTime));
                sb.AppendLine();

                return sb.ToString();
            }
        }

        /// <summary>
        /// Get a quick summary of the top bottlenecks
        /// </summary>
        public string GetBottleneckSummary(int topCount = 5)
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.AppendLine("\nTOP PERFORMANCE BOTTLENECKS:\n");

                var topOperations = _timings
                    .OrderByDescending(kvp => kvp.Value.TotalMilliseconds)
                    .Take(topCount)
                    .ToList();

                int index = 1;
                foreach (var kvp in topOperations)
                {
                    var entry = kvp.Value;
                    sb.AppendLine(string.Format("{0}. {1}: {2}ms ({3} calls, avg {4}ms)",
                        index, kvp.Key, entry.TotalMilliseconds, entry.CallCount, 
                        entry.TotalMilliseconds / entry.CallCount));
                    index++;
                }

                sb.AppendLine();
                return sb.ToString();
            }
        }

        /// <summary>
        /// Clear all timing data
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _timings.Clear();
                _globalStopwatch.Restart();
            }
        }

        public void Dispose()
        {
            _globalStopwatch?.Stop();
        }
    }
}
