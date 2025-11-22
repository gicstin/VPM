using System;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// Utility class for debouncing rapid selection changes with optional callback
    /// </summary>
    public class SelectionDebouncer
    {
        private CancellationTokenSource _cancellationTokenSource;
        private readonly int _delayMilliseconds;
        private readonly Func<Task> _action;
        private bool _disposed = false;

        /// <summary>
        /// Creates a new SelectionDebouncer
        /// </summary>
        /// <param name="delayMilliseconds">Delay in milliseconds before executing the action</param>
        /// <param name="action">Async action to execute after debounce delay</param>
        public SelectionDebouncer(int delayMilliseconds, Func<Task> action)
        {
            _delayMilliseconds = delayMilliseconds;
            _action = action ?? throw new ArgumentNullException(nameof(action));
        }

        /// <summary>
        /// Triggers the debounced action. Cancels any pending execution and schedules a new one.
        /// </summary>
        public void Trigger()
        {
            if (_disposed)
                return;

            // Cancel any pending execution
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();

            // Create new cancellation token source
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            // Schedule the action after delay
            _ = ExecuteAfterDelayAsync(token);
        }

        private async Task ExecuteAfterDelayAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(_delayMilliseconds, token);
                
                if (!token.IsCancellationRequested)
                {
                    await _action();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when debouncer is cancelled
            }
        }

        /// <summary>
        /// Cancels any pending execution
        /// </summary>
        public void Cancel()
        {
            _cancellationTokenSource?.Cancel();
        }

        /// <summary>
        /// Disposes the debouncer and cancels any pending operations
        /// </summary>
        public void Dispose()
        {
            _disposed = true;
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
        }
    }
}
