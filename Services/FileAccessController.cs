using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// Centralized file access controller that provides reader-writer lock semantics
    /// for archive file access. This eliminates file lock issues by:
    /// 1. Allowing multiple concurrent readers
    /// 2. Blocking new readers when a writer is waiting
    /// 3. Providing exclusive access for write operations (optimization, move, delete)
    /// 4. Automatic cleanup via disposable tokens
    /// 
    /// USAGE:
    /// - For reading (image loading): await AcquireReadAccessAsync(path)
    /// - For writing (optimization): await AcquireWriteAccessAsync(path, timeout)
    /// </summary>
    public sealed class FileAccessController : IDisposable
    {
        #region Singleton
        
        private static readonly Lazy<FileAccessController> _instance = new(() => new FileAccessController());
        public static FileAccessController Instance => _instance.Value;
        
        #endregion
        
        #region Per-File Lock State
        
        /// <summary>
        /// Tracks the lock state for a single file
        /// </summary>
        private sealed class FileLockState : IDisposable
        {
            public readonly AsyncReaderWriterLock Lock = new();
            public int ActiveReaders;
            public volatile bool WriterWaiting;
            public volatile bool WriterActive;
            public DateTime LastAccess = DateTime.UtcNow;
            public CancellationTokenSource ReaderCancellation;
            private volatile bool _disposed;
            
            public bool IsDisposed => _disposed;
            
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                
                ReaderCancellation?.Cancel();
                ReaderCancellation?.Dispose();
                Lock.Dispose();
            }
        }
        
        #endregion
        
        #region Fields
        
        private readonly ConcurrentDictionary<string, FileLockState> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _cleanupTimer;
        private readonly TimeSpan _staleTimeout = TimeSpan.FromMinutes(10); // Increased from 5 to 10 minutes for safety
        private bool _disposed;
        
        // Lock for atomic state transitions to prevent race conditions
        private readonly object _writerTransitionLock = new();
        
        // Statistics
        private long _totalReadAcquisitions;
        private long _totalWriteAcquisitions;
        private long _readBlockedByWriter;
        private long _writeTimeouts;
        
        #endregion
        
        #region Constructor
        
        private FileAccessController()
        {
            // Cleanup stale entries every 60 seconds
            _cleanupTimer = new Timer(CleanupStaleEntries, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
        
        #endregion
        
        #region Public API - Read Access
        
        /// <summary>
        /// Acquires read access to a file. Multiple readers can access the same file concurrently.
        /// If a writer is waiting or active, this will throw OperationCanceledException immediately
        /// (fail-fast behavior for non-critical reads like image loading).
        /// </summary>
        /// <param name="filePath">Path to the file</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Disposable token that releases the read lock when disposed</returns>
        /// <exception cref="OperationCanceledException">If a writer is waiting/active or cancellation requested</exception>
        public async Task<IDisposable> AcquireReadAccessAsync(string filePath, CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FileAccessController));
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));
            
            var normalizedPath = NormalizePath(filePath);
            var state = GetOrCreateState(normalizedPath);
            
            // Fail-fast if writer is waiting or active
            // This prevents new image loads from starting when optimization is pending
            if (state.WriterWaiting || state.WriterActive)
            {
                Interlocked.Increment(ref _readBlockedByWriter);
                throw new OperationCanceledException($"File is locked for writing: {Path.GetFileName(filePath)}");
            }
            
            // Check if reads for this file have been cancelled
            var readerCt = state.ReaderCancellation?.Token ?? CancellationToken.None;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, readerCt);
            
            try
            {
                await state.Lock.EnterReadLockAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Re-check why we were cancelled
                if (state.WriterWaiting || state.WriterActive)
                {
                    Interlocked.Increment(ref _readBlockedByWriter);
                    throw new OperationCanceledException($"File is locked for writing: {Path.GetFileName(filePath)}");
                }
                throw;
            }
            
            Interlocked.Increment(ref state.ActiveReaders);
            Interlocked.Increment(ref _totalReadAcquisitions);
            state.LastAccess = DateTime.UtcNow;
            
            return new ReadAccessToken(this, normalizedPath, state);
        }
        
        /// <summary>
        /// Tries to acquire read access without throwing if writer is active.
        /// Returns null if access cannot be acquired.
        /// </summary>
        public async Task<IDisposable> TryAcquireReadAccessAsync(string filePath, CancellationToken ct = default)
        {
            try
            {
                return await AcquireReadAccessAsync(filePath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        
        /// <summary>
        /// Checks if a file is currently locked for writing (optimization in progress).
        /// Use this for fast pre-checks before attempting to queue image loads.
        /// </summary>
        public bool IsFileLockedForWriting(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            
            var normalizedPath = NormalizePath(filePath);
            if (_fileLocks.TryGetValue(normalizedPath, out var state))
            {
                return state.WriterWaiting || state.WriterActive;
            }
            
            // Also check by filename only (handles different paths to same file)
            var fileName = Path.GetFileName(filePath);
            foreach (var kvp in _fileLocks)
            {
                if (Path.GetFileName(kvp.Key).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (kvp.Value.WriterWaiting || kvp.Value.WriterActive)
                        return true;
                }
            }
            
            return false;
        }
        
        #endregion
        
        #region Public API - Write Access
        
        /// <summary>
        /// Acquires exclusive write access to a file. This will:
        /// 1. Immediately block new readers (WriterWaiting = true) - ATOMICALLY
        /// 2. Cancel any pending read operations for this file
        /// 3. Wait for existing readers to finish (up to timeout)
        /// 4. Return exclusive access token
        /// </summary>
        /// <param name="filePath">Path to the file</param>
        /// <param name="timeout">Maximum time to wait for exclusive access</param>
        /// <returns>Disposable token that releases the write lock when disposed</returns>
        /// <exception cref="TimeoutException">If exclusive access cannot be acquired within timeout</exception>
        public async Task<IDisposable> AcquireWriteAccessAsync(string filePath, TimeSpan timeout)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FileAccessController));
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));
            
            var normalizedPath = NormalizePath(filePath);
            var state = GetOrCreateState(normalizedPath);
            
            // CRITICAL FIX: Use lock to atomically set WriterWaiting and cancel readers
            // This prevents race condition where a reader could slip through between
            // the flag set and the cancellation
            lock (_writerTransitionLock)
            {
                // Signal that writer is waiting - new readers will fail fast immediately
                state.WriterWaiting = true;
                
                // Cancel any pending read operations for this file
                // This causes in-progress AcquireReadAccessAsync calls to throw
                state.ReaderCancellation?.Cancel();
                state.ReaderCancellation?.Dispose();
                state.ReaderCancellation = new CancellationTokenSource();
            }
            
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                
                // Wait for exclusive access - this blocks until all readers finish
                await state.Lock.EnterWriteLockAsync(cts.Token).ConfigureAwait(false);
                
                state.WriterActive = true;
                state.WriterWaiting = false;
                state.LastAccess = DateTime.UtcNow;
                
                Interlocked.Increment(ref _totalWriteAcquisitions);
                
                return new WriteAccessToken(this, normalizedPath, state);
            }
            catch (OperationCanceledException)
            {
                state.WriterWaiting = false;
                Interlocked.Increment(ref _writeTimeouts);
                throw new TimeoutException($"Could not acquire exclusive access to '{Path.GetFileName(filePath)}' within {timeout.TotalSeconds:F1}s. " +
                    $"Active readers: {state.ActiveReaders}");
            }
        }
        
        /// <summary>
        /// Acquires exclusive write access to multiple files atomically.
        /// All files must be acquired or none are.
        /// </summary>
        public async Task<IDisposable> AcquireWriteAccessAsync(IEnumerable<string> filePaths, TimeSpan timeout)
        {
            var paths = filePaths.Select(NormalizePath).Distinct().ToList();
            var acquiredTokens = new List<WriteAccessToken>();
            
            try
            {
                foreach (var path in paths)
                {
                    var token = (WriteAccessToken)await AcquireWriteAccessAsync(path, timeout).ConfigureAwait(false);
                    acquiredTokens.Add(token);
                }
                
                return new CompositeWriteAccessToken(acquiredTokens);
            }
            catch
            {
                // Release any tokens we acquired
                foreach (var token in acquiredTokens)
                {
                    token.Dispose();
                }
                throw;
            }
        }
        
        #endregion
        
        #region Public API - Query & Management
        
        /// <summary>
        /// Gets the current number of active readers for a file.
        /// </summary>
        public int GetActiveReaderCount(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return 0;
            
            var normalizedPath = NormalizePath(filePath);
            if (_fileLocks.TryGetValue(normalizedPath, out var state))
            {
                return state.ActiveReaders;
            }
            return 0;
        }
        
        /// <summary>
        /// Gets statistics about file access operations.
        /// </summary>
        public (long totalReads, long totalWrites, long readsBlockedByWriter, long writeTimeouts, int trackedFiles) GetStatistics()
        {
            return (
                Interlocked.Read(ref _totalReadAcquisitions),
                Interlocked.Read(ref _totalWriteAcquisitions),
                Interlocked.Read(ref _readBlockedByWriter),
                Interlocked.Read(ref _writeTimeouts),
                _fileLocks.Count
            );
        }
        
        /// <summary>
        /// Forces a full garbage collection to release file handles held by finalizers.
        /// Call this before critical file operations (move, delete) when other methods fail.
        /// WARNING: This is expensive and blocks the calling thread. Use sparingly.
        /// </summary>
        public static void ForceReleaseFileHandles()
        {
            // Force full GC to release any file handles held by finalizers
            // This is necessary because SharpCompress may hold handles until finalization
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        }
        
        /// <summary>
        /// Resets statistics counters.
        /// </summary>
        public void ResetStatistics()
        {
            Interlocked.Exchange(ref _totalReadAcquisitions, 0);
            Interlocked.Exchange(ref _totalWriteAcquisitions, 0);
            Interlocked.Exchange(ref _readBlockedByWriter, 0);
            Interlocked.Exchange(ref _writeTimeouts, 0);
        }
        
        /// <summary>
        /// Forces cleanup of a specific file's lock state.
        /// Use after file operations complete to free memory.
        /// </summary>
        public void InvalidateFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            
            var normalizedPath = NormalizePath(filePath);
            if (_fileLocks.TryRemove(normalizedPath, out var state))
            {
                state.Dispose();
            }
            
            // Also remove by filename
            var fileName = Path.GetFileName(filePath);
            var keysToRemove = _fileLocks.Keys
                .Where(k => Path.GetFileName(k).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            foreach (var key in keysToRemove)
            {
                if (_fileLocks.TryRemove(key, out state))
                {
                    state.Dispose();
                }
            }
        }
        
        /// <summary>
        /// Forces cleanup of all lock states.
        /// Use before bulk operations or shutdown.
        /// </summary>
        public void InvalidateAll()
        {
            var keys = _fileLocks.Keys.ToList();
            foreach (var key in keys)
            {
                if (_fileLocks.TryRemove(key, out var state))
                {
                    state.Dispose();
                }
            }
        }
        
        #endregion
        
        #region Private Methods
        
        private FileLockState GetOrCreateState(string normalizedPath)
        {
            return _fileLocks.GetOrAdd(normalizedPath, _ => new FileLockState());
        }
        
        private static string NormalizePath(string path)
        {
            // Normalize path separators and case for consistent dictionary keys
            return path.Replace('/', '\\').ToLowerInvariant();
        }
        
        private void ReleaseReadLock(string normalizedPath, FileLockState state)
        {
            // CRITICAL: Check if state was disposed by cleanup timer
            if (state.IsDisposed) return;
            
            try
            {
                Interlocked.Decrement(ref state.ActiveReaders);
                state.Lock.ExitReadLock();
                state.LastAccess = DateTime.UtcNow;
            }
            catch (ObjectDisposedException)
            {
                // State was disposed between check and operation - ignore
            }
        }
        
        private void ReleaseWriteLock(string normalizedPath, FileLockState state)
        {
            // CRITICAL: Check if state was disposed by cleanup timer
            if (state.IsDisposed) return;
            
            try
            {
                state.WriterActive = false;
                state.Lock.ExitWriteLock();
                state.LastAccess = DateTime.UtcNow;
                
                // Reset reader cancellation so new reads can proceed
                state.ReaderCancellation?.Dispose();
                state.ReaderCancellation = null;
            }
            catch (ObjectDisposedException)
            {
                // State was disposed between check and operation - ignore
            }
        }
        
        // Track consecutive cleanup failures for logging/diagnostics
        private int _consecutiveCleanupFailures = 0;
        private const int MaxConsecutiveCleanupFailures = 5;
        
        private void CleanupStaleEntries(object state)
        {
            if (_disposed) return;
            
            try
            {
                var now = DateTime.UtcNow;
                var keysToRemove = new List<string>();
                
                foreach (var kvp in _fileLocks)
                {
                    var lockState = kvp.Value;
                    
                    // Only remove if no active readers/writers and stale
                    // Use longer timeout (10 minutes) to be extra safe
                    if (lockState.ActiveReaders == 0 && 
                        !lockState.WriterWaiting && 
                        !lockState.WriterActive &&
                        !lockState.IsDisposed &&
                        now - lockState.LastAccess > _staleTimeout)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
                
                foreach (var key in keysToRemove)
                {
                    // Double-check before removal to avoid race conditions
                    if (_fileLocks.TryGetValue(key, out var checkState))
                    {
                        // Re-verify the state hasn't changed
                        if (checkState.ActiveReaders == 0 && 
                            !checkState.WriterWaiting && 
                            !checkState.WriterActive &&
                            !checkState.IsDisposed)
                        {
                            if (_fileLocks.TryRemove(key, out var removedState))
                            {
                                removedState.Dispose();
                            }
                        }
                    }
                }
                
                // Reset failure counter on success
                _consecutiveCleanupFailures = 0;
            }
            catch (Exception ex)
            {
                // FIXED: Track consecutive failures instead of silently ignoring
                // This helps diagnose persistent cleanup issues
                _consecutiveCleanupFailures++;
                
                if (_consecutiveCleanupFailures >= MaxConsecutiveCleanupFailures)
                {
                    // Log to debug output after repeated failures
                    System.Diagnostics.Debug.WriteLine($"[FileAccessController] Cleanup failed {_consecutiveCleanupFailures} times. Last error: {ex.Message}");
                }
            }
        }
        
        #endregion
        
        #region Access Tokens
        
        /// <summary>
        /// Token that releases read lock when disposed
        /// </summary>
        private sealed class ReadAccessToken : IDisposable
        {
            private readonly FileAccessController _controller;
            private readonly string _normalizedPath;
            private readonly FileLockState _state;
            private bool _disposed;
            
            public ReadAccessToken(FileAccessController controller, string normalizedPath, FileLockState state)
            {
                _controller = controller;
                _normalizedPath = normalizedPath;
                _state = state;
            }
            
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                
                _controller.ReleaseReadLock(_normalizedPath, _state);
            }
        }
        
        /// <summary>
        /// Token that releases write lock when disposed
        /// </summary>
        private sealed class WriteAccessToken : IDisposable
        {
            private readonly FileAccessController _controller;
            private readonly string _normalizedPath;
            private readonly FileLockState _state;
            private bool _disposed;
            
            public WriteAccessToken(FileAccessController controller, string normalizedPath, FileLockState state)
            {
                _controller = controller;
                _normalizedPath = normalizedPath;
                _state = state;
            }
            
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                
                _controller.ReleaseWriteLock(_normalizedPath, _state);
            }
        }
        
        /// <summary>
        /// Token that releases multiple write locks when disposed
        /// </summary>
        private sealed class CompositeWriteAccessToken : IDisposable
        {
            private readonly List<WriteAccessToken> _tokens;
            private bool _disposed;
            
            public CompositeWriteAccessToken(List<WriteAccessToken> tokens)
            {
                _tokens = tokens;
            }
            
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                
                // Release in reverse order
                for (int i = _tokens.Count - 1; i >= 0; i--)
                {
                    _tokens[i].Dispose();
                }
            }
        }
        
        #endregion
        
        #region IDisposable
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _cleanupTimer.Dispose();
            
            foreach (var kvp in _fileLocks)
            {
                kvp.Value.Dispose();
            }
            _fileLocks.Clear();
        }
        
        #endregion
    }
    
    /// <summary>
    /// Async-compatible reader-writer lock implementation.
    /// Allows multiple concurrent readers OR a single exclusive writer.
    /// Writer requests have priority - when a writer is waiting, new readers are blocked.
    /// 
    /// FIXED: Race condition between checking _writerWaiting and acquiring lock.
    /// Now uses atomic lock acquisition with proper state checking inside the lock.
    /// </summary>
    public sealed class AsyncReaderWriterLock : IDisposable
    {
        private readonly SemaphoreSlim _readSemaphore = new(1, 1);
        private readonly SemaphoreSlim _writeSemaphore = new(1, 1);
        private readonly SemaphoreSlim _writerWaitingSemaphore = new(1, 1);
        private int _readerCount;
        private volatile bool _writerWaiting;
        private readonly object _countLock = new();
        private bool _disposed;
        
        /// <summary>
        /// Enters the lock in read mode asynchronously.
        /// Multiple readers can hold the lock simultaneously.
        /// </summary>
        public async Task EnterReadLockAsync(CancellationToken ct = default)
        {
            // Acquire read semaphore first to serialize reader entry
            await _readSemaphore.WaitAsync(ct).ConfigureAwait(false);
            
            bool acquiredWriteSemaphore = false;
            bool incrementedReaderCount = false;
            
            try
            {
                // CRITICAL FIX: Check writer state INSIDE the lock to prevent race condition
                // Previously, a writer could set _writerWaiting between the check and semaphore acquisition
                lock (_countLock)
                {
                    // If writer is waiting or active, fail fast
                    if (_writerWaiting)
                    {
                        throw new OperationCanceledException("Writer is waiting for exclusive access");
                    }
                    
                    if (++_readerCount == 1)
                    {
                        incrementedReaderCount = true;
                        // First reader acquires write semaphore to block writers
                        if (!_writeSemaphore.Wait(0))
                        {
                            // Writer has the lock, revert and fail
                            _readerCount--;
                            incrementedReaderCount = false;
                            throw new OperationCanceledException("Writer has exclusive access");
                        }
                        acquiredWriteSemaphore = true;
                    }
                    else
                    {
                        incrementedReaderCount = true;
                    }
                }
            }
            catch
            {
                // Clean up on failure - only release read semaphore, write semaphore handled above
                _readSemaphore.Release();
                throw;
            }
            
            _readSemaphore.Release();
        }
        
        /// <summary>
        /// Exits read mode.
        /// </summary>
        public void ExitReadLock()
        {
            if (_disposed) return; // Don't try to release if already disposed
            
            try
            {
                lock (_countLock)
                {
                    // FIXED: Guard against underflow
                    if (_readerCount <= 0)
                    {
                        // Already at zero or negative - this is a bug in calling code
                        // but we shouldn't corrupt state further
                        return;
                    }
                    
                    if (--_readerCount == 0)
                    {
                        // Last reader releases write semaphore
                        _writeSemaphore.Release();
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Semaphores were disposed - ignore
            }
        }
        
        /// <summary>
        /// Enters the lock in write mode asynchronously.
        /// Only one writer can hold the lock, and no readers can be active.
        /// </summary>
        public async Task EnterWriteLockAsync(CancellationToken ct = default)
        {
            // Acquire the writer-waiting semaphore first
            await _writerWaitingSemaphore.WaitAsync(ct).ConfigureAwait(false);
            
            // CRITICAL: Set writer waiting flag AFTER acquiring semaphore to prevent race
            // This ensures only one writer can set the flag at a time
            _writerWaiting = true;
            
            try
            {
                // Wait for exclusive access (all readers must finish)
                await _writeSemaphore.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                _writerWaiting = false;
                _writerWaitingSemaphore.Release();
                throw;
            }
        }
        
        /// <summary>
        /// Exits write mode.
        /// </summary>
        public void ExitWriteLock()
        {
            if (_disposed) return; // Don't try to release if already disposed
            
            try
            {
                _writeSemaphore.Release();
                _writerWaiting = false;
                _writerWaitingSemaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                // Semaphores were disposed - ignore
            }
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _readSemaphore.Dispose();
            _writeSemaphore.Dispose();
            _writerWaitingSemaphore.Dispose();
        }
    }
}
