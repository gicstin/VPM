using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;

namespace VPM.Services
{
    /// <summary>
    /// Helper class for safe parallel processing of ZIP archive entries.
    /// Handles thread-safe IArchive access and provides convenient parallel processing patterns.
    /// 
    /// Key design:
    /// - Each thread opens its own IArchive instance (thread-safe)
    /// - Supports both void and return-value processing
    /// - Configurable parallelism level
    /// - Exception handling and aggregation
    /// </summary>
    public static class ParallelArchiveProcessor
    {
        /// <summary>
        /// Processes archive entries in parallel with a custom action.
        /// Each thread opens its own IArchive instance for thread safety.
        /// </summary>
        /// <typeparam name="T">Type of items to process</typeparam>
        /// <param name="zipPath">Path to the ZIP archive</param>
        /// <param name="items">Items to process (typically filtered entries)</param>
        /// <param name="processor">Action to execute for each item. Receives: (archive, item, index)</param>
        /// <param name="maxDegreeOfParallelism">Maximum parallel threads (0 = auto)</param>
        public static void ProcessInParallel<T>(
            string zipPath,
            IEnumerable<T> items,
            Action<IArchive, T, int> processor,
            int maxDegreeOfParallelism = 0)
        {
            if (string.IsNullOrEmpty(zipPath) || items == null)
                return;

            var itemList = items.ToList();
            if (itemList.Count == 0)
                return;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism <= 0 
                    ? Environment.ProcessorCount 
                    : maxDegreeOfParallelism
            };

            var exceptions = new ConcurrentBag<Exception>();

            Parallel.ForEach(itemList, parallelOptions, (item, state, index) =>
            {
                try
                {
                    using (var archive = ZipArchive.Open(zipPath))
                    {
                        processor(archive, item, (int)index);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            if (exceptions.Count > 0)
            {
                throw new AggregateException("Errors occurred during parallel processing", exceptions);
            }
        }

        /// <summary>
        /// Processes archive entries in parallel and collects results.
        /// Each thread opens its own IArchive instance for thread safety.
        /// </summary>
        /// <typeparam name="TItem">Type of items to process</typeparam>
        /// <typeparam name="TResult">Type of results to collect</typeparam>
        /// <param name="zipPath">Path to the ZIP archive</param>
        /// <param name="items">Items to process (typically filtered entries)</param>
        /// <param name="processor">Function to execute for each item. Receives: (archive, item, index). Returns result or null to skip.</param>
        /// <param name="maxDegreeOfParallelism">Maximum parallel threads (0 = auto)</param>
        /// <returns>List of non-null results</returns>
        public static List<TResult> ProcessInParallel<TItem, TResult>(
            string zipPath,
            IEnumerable<TItem> items,
            Func<IArchive, TItem, int, TResult> processor,
            int maxDegreeOfParallelism = 0) where TResult : class
        {
            if (string.IsNullOrEmpty(zipPath) || items == null)
                return new List<TResult>();

            var itemList = items.ToList();
            if (itemList.Count == 0)
                return new List<TResult>();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism <= 0 
                    ? Environment.ProcessorCount 
                    : maxDegreeOfParallelism
            };

            var results = new ConcurrentBag<TResult>();
            var exceptions = new ConcurrentBag<Exception>();

            Parallel.ForEach(itemList, parallelOptions, (item, state, index) =>
            {
                try
                {
                    using (var archive = ZipArchive.Open(zipPath))
                    {
                        var result = processor(archive, item, (int)index);
                        if (result != null)
                        {
                            results.Add(result);
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            if (exceptions.Count > 0)
            {
                throw new AggregateException("Errors occurred during parallel processing", exceptions);
            }

            return results.ToList();
        }

        /// <summary>
        /// Processes archive entries in parallel with a custom action that returns a value.
        /// Collects results in a thread-safe dictionary keyed by item.
        /// </summary>
        /// <typeparam name="TItem">Type of items to process</typeparam>
        /// <typeparam name="TResult">Type of results to collect</typeparam>
        /// <param name="zipPath">Path to the ZIP archive</param>
        /// <param name="items">Items to process</param>
        /// <param name="processor">Function to execute for each item. Receives: (archive, item, index). Returns result or null to skip.</param>
        /// <param name="maxDegreeOfParallelism">Maximum parallel threads (0 = auto)</param>
        /// <returns>Dictionary mapping items to results</returns>
        public static Dictionary<TItem, TResult> ProcessInParallelWithMapping<TItem, TResult>(
            string zipPath,
            IEnumerable<TItem> items,
            Func<IArchive, TItem, int, TResult> processor,
            int maxDegreeOfParallelism = 0) where TItem : class where TResult : class
        {
            if (string.IsNullOrEmpty(zipPath) || items == null)
                return new Dictionary<TItem, TResult>();

            var itemList = items.ToList();
            if (itemList.Count == 0)
                return new Dictionary<TItem, TResult>();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism <= 0 
                    ? Environment.ProcessorCount 
                    : maxDegreeOfParallelism
            };

            var results = new ConcurrentDictionary<TItem, TResult>();
            var exceptions = new ConcurrentBag<Exception>();

            Parallel.ForEach(itemList, parallelOptions, (item, state, index) =>
            {
                try
                {
                    using (var archive = ZipArchive.Open(zipPath))
                    {
                        var result = processor(archive, item, (int)index);
                        if (result != null)
                        {
                            results.TryAdd(item, result);
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            if (exceptions.Count > 0)
            {
                throw new AggregateException("Errors occurred during parallel processing", exceptions);
            }

            return new Dictionary<TItem, TResult>(results);
        }

        /// <summary>
        /// Processes archive entries in parallel with reduced parallelism for memory-intensive operations.
        /// Useful for operations that consume significant memory (e.g., texture conversion).
        /// </summary>
        /// <typeparam name="T">Type of items to process</typeparam>
        /// <param name="zipPath">Path to the ZIP archive</param>
        /// <param name="items">Items to process</param>
        /// <param name="processor">Action to execute for each item. Receives: (archive, item, index)</param>
        /// <param name="maxDegreeOfParallelism">Maximum parallel threads (recommended: 2-4 for memory-intensive ops)</param>
        public static void ProcessInParallelLimited<T>(
            string zipPath,
            IEnumerable<T> items,
            Action<IArchive, T, int> processor,
            int maxDegreeOfParallelism = 2)
        {
            ProcessInParallel(zipPath, items, processor, maxDegreeOfParallelism);
        }

        /// <summary>
        /// Calculates optimal parallelism level based on operation type and system resources.
        /// </summary>
        /// <param name="operationType">Type of operation: "io" (I/O-bound), "cpu" (CPU-bound), "memory" (memory-intensive)</param>
        /// <returns>Recommended max degree of parallelism</returns>
        public static int GetOptimalParallelism(string operationType = "io")
        {
            int coreCount = Environment.ProcessorCount;

            return operationType?.ToLowerInvariant() switch
            {
                "io" => coreCount * 2,        // I/O-bound: use more threads
                "cpu" => coreCount,           // CPU-bound: use core count
                "memory" => Math.Max(2, coreCount / 2),  // Memory-intensive: use fewer threads
                _ => coreCount
            };
        }

        // ============================================
        // SEMAPHORE-BASED BATCH PROCESSING (Phase 2 Optimization)
        // ============================================

        /// <summary>
        /// Processes archive entries in parallel using SemaphoreSlim for finer resource control.
        /// Benefit: Better resource management, prevents resource exhaustion, adaptive batching
        /// </summary>
        /// <typeparam name="T">Type of items to process</typeparam>
        /// <param name="zipPath">Path to the ZIP archive</param>
        /// <param name="items">Items to process</param>
        /// <param name="processor">Async action to execute for each item. Receives: (archive, item, index)</param>
        /// <param name="maxConcurrency">Maximum concurrent operations (0 = auto)</param>
        public static async Task ProcessInParallelWithSemaphoreAsync<T>(
            string zipPath,
            IEnumerable<T> items,
            Func<IArchive, T, int, Task> processor,
            int maxConcurrency = 0)
        {
            if (string.IsNullOrEmpty(zipPath) || items == null)
                return;

            var itemList = items.ToList();
            if (itemList.Count == 0)
                return;

            if (maxConcurrency <= 0)
                maxConcurrency = Environment.ProcessorCount;

            var exceptions = new ConcurrentBag<Exception>();
            using (var semaphore = new System.Threading.SemaphoreSlim(maxConcurrency))
            {
                var tasks = itemList.Select(async (item, index) =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        using (var archive = ZipArchive.Open(zipPath))
                        {
                            await processor(archive, item, index);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
            }

            if (exceptions.Count > 0)
            {
                throw new AggregateException("Errors occurred during parallel processing", exceptions);
            }
        }

        /// <summary>
        /// Processes archive entries in parallel using SemaphoreSlim and collects results.
        /// Benefit: Better resource management, prevents resource exhaustion, adaptive batching
        /// </summary>
        /// <typeparam name="TItem">Type of items to process</typeparam>
        /// <typeparam name="TResult">Type of results to collect</typeparam>
        /// <param name="zipPath">Path to the ZIP archive</param>
        /// <param name="items">Items to process</param>
        /// <param name="processor">Async function to execute for each item. Receives: (archive, item, index). Returns result or null to skip.</param>
        /// <param name="maxConcurrency">Maximum concurrent operations (0 = auto)</param>
        public static async Task<List<TResult>> ProcessInParallelWithSemaphoreAsync<TItem, TResult>(
            string zipPath,
            IEnumerable<TItem> items,
            Func<IArchive, TItem, int, Task<TResult>> processor,
            int maxConcurrency = 0) where TResult : class
        {
            if (string.IsNullOrEmpty(zipPath) || items == null)
                return new List<TResult>();

            var itemList = items.ToList();
            if (itemList.Count == 0)
                return new List<TResult>();

            if (maxConcurrency <= 0)
                maxConcurrency = Environment.ProcessorCount;

            var results = new ConcurrentBag<TResult>();
            var exceptions = new ConcurrentBag<Exception>();

            using (var semaphore = new System.Threading.SemaphoreSlim(maxConcurrency))
            {
                var tasks = itemList.Select(async (item, index) =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        using (var archive = ZipArchive.Open(zipPath))
                        {
                            var result = await processor(archive, item, index);
                            if (result != null)
                            {
                                results.Add(result);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
            }

            if (exceptions.Count > 0)
            {
                throw new AggregateException("Errors occurred during parallel processing", exceptions);
            }

            return results.ToList();
        }

        /// <summary>
        /// Processes archive entries in parallel using SemaphoreSlim with result mapping.
        /// Benefit: Better resource management, prevents resource exhaustion, adaptive batching
        /// </summary>
        /// <typeparam name="TItem">Type of items to process</typeparam>
        /// <typeparam name="TResult">Type of results to collect</typeparam>
        /// <param name="zipPath">Path to the ZIP archive</param>
        /// <param name="items">Items to process</param>
        /// <param name="processor">Async function to execute for each item. Receives: (archive, item, index). Returns result or null to skip.</param>
        /// <param name="maxConcurrency">Maximum concurrent operations (0 = auto)</param>
        public static async Task<Dictionary<TItem, TResult>> ProcessInParallelWithSemaphoreAsyncMapped<TItem, TResult>(
            string zipPath,
            IEnumerable<TItem> items,
            Func<IArchive, TItem, int, Task<TResult>> processor,
            int maxConcurrency = 0) where TItem : class where TResult : class
        {
            if (string.IsNullOrEmpty(zipPath) || items == null)
                return new Dictionary<TItem, TResult>();

            var itemList = items.ToList();
            if (itemList.Count == 0)
                return new Dictionary<TItem, TResult>();

            if (maxConcurrency <= 0)
                maxConcurrency = Environment.ProcessorCount;

            var results = new ConcurrentDictionary<TItem, TResult>();
            var exceptions = new ConcurrentBag<Exception>();

            using (var semaphore = new System.Threading.SemaphoreSlim(maxConcurrency))
            {
                var tasks = itemList.Select(async (item, index) =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        using (var archive = ZipArchive.Open(zipPath))
                        {
                            var result = await processor(archive, item, index);
                            if (result != null)
                            {
                                results.TryAdd(item, result);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
            }

            if (exceptions.Count > 0)
            {
                throw new AggregateException("Errors occurred during parallel processing", exceptions);
            }

            return new Dictionary<TItem, TResult>(results);
        }

        // ============================================
        // ASYNC WRAPPER METHODS (Phase 2 Optimization)
        // ============================================

        /// <summary>
        /// Processes archive entries in parallel asynchronously (async wrapper for sync method).
        /// Each thread opens its own IArchive instance for thread safety.
        /// Benefit: Async/await composition, non-blocking thread pool usage
        /// </summary>
        /// <typeparam name="T">Type of items to process</typeparam>
        /// <param name="zipPath">Path to the ZIP archive</param>
        /// <param name="items">Items to process (typically filtered entries)</param>
        /// <param name="processor">Action to execute for each item. Receives: (archive, item, index)</param>
        /// <param name="maxDegreeOfParallelism">Maximum parallel threads (0 = auto)</param>
        public static async Task ProcessInParallelAsync<T>(
            string zipPath,
            IEnumerable<T> items,
            Action<IArchive, T, int> processor,
            int maxDegreeOfParallelism = 0)
        {
            await Task.Run(() => ProcessInParallel(zipPath, items, processor, maxDegreeOfParallelism));
        }

        /// <summary>
        /// Processes archive entries in parallel asynchronously and collects results.
        /// Each thread opens its own IArchive instance for thread safety.
        /// Benefit: Async/await composition, non-blocking thread pool usage
        /// </summary>
        /// <typeparam name="TItem">Type of items to process</typeparam>
        /// <typeparam name="TResult">Type of results to collect</typeparam>
        /// <param name="zipPath">Path to the ZIP archive</param>
        /// <param name="items">Items to process (typically filtered entries)</param>
        /// <param name="processor">Function to execute for each item. Receives: (archive, item, index). Returns result or null to skip.</param>
        /// <param name="maxDegreeOfParallelism">Maximum parallel threads (0 = auto)</param>
        /// <returns>List of non-null results</returns>
        public static async Task<List<TResult>> ProcessInParallelAsync<TItem, TResult>(
            string zipPath,
            IEnumerable<TItem> items,
            Func<IArchive, TItem, int, TResult> processor,
            int maxDegreeOfParallelism = 0) where TResult : class
        {
            return await Task.Run(() => ProcessInParallel(zipPath, items, processor, maxDegreeOfParallelism));
        }

        /// <summary>
        /// Processes archive entries in parallel asynchronously with result mapping.
        /// Each thread opens its own IArchive instance for thread safety.
        /// Benefit: Async/await composition, non-blocking thread pool usage
        /// </summary>
        /// <typeparam name="TItem">Type of items to process</typeparam>
        /// <typeparam name="TResult">Type of results to collect</typeparam>
        /// <param name="zipPath">Path to the ZIP archive</param>
        /// <param name="items">Items to process</param>
        /// <param name="processor">Function to execute for each item. Receives: (archive, item, index). Returns result or null to skip.</param>
        /// <param name="maxDegreeOfParallelism">Maximum parallel threads (0 = auto)</param>
        /// <returns>Dictionary mapping items to results</returns>
        public static async Task<Dictionary<TItem, TResult>> ProcessInParallelAsyncMapped<TItem, TResult>(
            string zipPath,
            IEnumerable<TItem> items,
            Func<IArchive, TItem, int, TResult> processor,
            int maxDegreeOfParallelism = 0) where TItem : class where TResult : class
        {
            return await Task.Run(() => ProcessInParallelWithMapping(zipPath, items, processor, maxDegreeOfParallelism));
        }

        /// <summary>
        /// Processes archive entries in parallel asynchronously with reduced parallelism for memory-intensive operations.
        /// Useful for operations that consume significant memory (e.g., texture conversion).
        /// Benefit: Async/await composition, memory-aware parallelism
        /// </summary>
        /// <typeparam name="T">Type of items to process</typeparam>
        /// <param name="zipPath">Path to the ZIP archive</param>
        /// <param name="items">Items to process</param>
        /// <param name="processor">Action to execute for each item. Receives: (archive, item, index)</param>
        /// <param name="maxDegreeOfParallelism">Maximum parallel threads (recommended: 2-4 for memory-intensive ops)</param>
        public static async Task ProcessInParallelLimitedAsync<T>(
            string zipPath,
            IEnumerable<T> items,
            Action<IArchive, T, int> processor,
            int maxDegreeOfParallelism = 2)
        {
            await Task.Run(() => ProcessInParallelLimited(zipPath, items, processor, maxDegreeOfParallelism));
        }

    }
}
