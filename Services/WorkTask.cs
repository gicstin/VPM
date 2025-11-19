using System;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// Base abstraction for all work tasks in the parallel optimization system.
    /// Provides a consistent interface for task execution, cancellation, and progress tracking.
    /// 
    /// Design principles:
    /// - Generic to support any task type (images, JSON, archives, etc.)
    /// - Built-in cancellation support via CancellationToken
    /// - Progress reporting through callbacks
    /// - Execution metadata tracking
    /// </summary>
    public abstract class WorkTask
    {
        /// <summary>
        /// Unique identifier for this task
        /// </summary>
        public string TaskId { get; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Human-readable task name
        /// </summary>
        public string TaskName { get; protected set; }

        /// <summary>
        /// Task priority (higher = execute sooner)
        /// </summary>
        public int Priority { get; set; } = 0;

        /// <summary>
        /// Current execution state
        /// </summary>
        public TaskState State { get; protected set; } = TaskState.Pending;

        /// <summary>
        /// Progress percentage (0-100)
        /// </summary>
        public int ProgressPercent { get; protected set; } = 0;

        /// <summary>
        /// Estimated total work units
        /// </summary>
        public long TotalWorkUnits { get; protected set; } = 0;

        /// <summary>
        /// Completed work units
        /// </summary>
        public long CompletedWorkUnits { get; protected set; } = 0;

        /// <summary>
        /// Task creation timestamp
        /// </summary>
        public DateTime CreatedAt { get; } = DateTime.UtcNow;

        /// <summary>
        /// Task start timestamp
        /// </summary>
        public DateTime? StartedAt { get; protected set; }

        /// <summary>
        /// Task completion timestamp
        /// </summary>
        public DateTime? CompletedAt { get; protected set; }

        /// <summary>
        /// Error message if task failed
        /// </summary>
        public string ErrorMessage { get; protected set; }

        /// <summary>
        /// Exception if task failed
        /// </summary>
        public Exception Exception { get; protected set; }

        /// <summary>
        /// Execution duration
        /// </summary>
        public TimeSpan? Duration => CompletedAt.HasValue && StartedAt.HasValue 
            ? CompletedAt.Value - StartedAt.Value 
            : null;

        /// <summary>
        /// Progress changed event
        /// </summary>
        public event EventHandler<ProgressChangedEventArgs> ProgressChanged;

        /// <summary>
        /// State changed event
        /// </summary>
        public event EventHandler<TaskStateChangedEventArgs> StateChanged;

        /// <summary>
        /// Execute the task asynchronously
        /// </summary>
        public abstract Task ExecuteAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Get task type for categorization
        /// </summary>
        public abstract string GetTaskType();

        /// <summary>
        /// Update progress and raise event
        /// </summary>
        protected void UpdateProgress(long completed, long total)
        {
            CompletedWorkUnits = completed;
            TotalWorkUnits = total;
            ProgressPercent = total > 0 ? (int)((completed * 100) / total) : 0;
            ProgressChanged?.Invoke(this, new ProgressChangedEventArgs(ProgressPercent, completed, total));
        }

        /// <summary>
        /// Update task state and raise event
        /// </summary>
        internal void UpdateState(TaskState newState)
        {
            var oldState = State;
            State = newState;

            if (newState == TaskState.Running && !StartedAt.HasValue)
                StartedAt = DateTime.UtcNow;

            if ((newState == TaskState.Completed || newState == TaskState.Failed || newState == TaskState.Cancelled) 
                && !CompletedAt.HasValue)
                CompletedAt = DateTime.UtcNow;

            StateChanged?.Invoke(this, new TaskStateChangedEventArgs(oldState, newState));
        }

        /// <summary>
        /// Mark task as failed
        /// </summary>
        internal void MarkFailed(string errorMessage, Exception exception = null)
        {
            ErrorMessage = errorMessage;
            Exception = exception;
            UpdateState(TaskState.Failed);
        }

        /// <summary>
        /// Mark task as completed
        /// </summary>
        internal void MarkCompleted()
        {
            UpdateProgress(TotalWorkUnits, TotalWorkUnits);
            UpdateState(TaskState.Completed);
        }
    }

    /// <summary>
    /// Generic work task with typed result
    /// </summary>
    public abstract class WorkTask<TResult> : WorkTask
    {
        /// <summary>
        /// Task result (available after completion)
        /// </summary>
        public TResult Result { get; protected set; }
    }

    /// <summary>
    /// Task execution states
    /// </summary>
    public enum TaskState
    {
        Pending,
        Running,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Progress change event arguments
    /// </summary>
    public class ProgressChangedEventArgs : EventArgs
    {
        public int ProgressPercent { get; }
        public long CompletedUnits { get; }
        public long TotalUnits { get; }

        public ProgressChangedEventArgs(int progressPercent, long completed, long total)
        {
            ProgressPercent = progressPercent;
            CompletedUnits = completed;
            TotalUnits = total;
        }
    }

    /// <summary>
    /// Task state change event arguments
    /// </summary>
    public class TaskStateChangedEventArgs : EventArgs
    {
        public TaskState OldState { get; }
        public TaskState NewState { get; }

        public TaskStateChangedEventArgs(TaskState oldState, TaskState newState)
        {
            OldState = oldState;
            NewState = newState;
        }
    }
}
