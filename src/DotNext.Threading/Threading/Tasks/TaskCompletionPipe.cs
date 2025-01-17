using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using static System.Threading.Timeout;

namespace DotNext.Threading.Tasks;

/// <summary>
/// Represents a pipe to process asynchronous tasks as they complete.
/// </summary>
/// <typeparam name="T">The type of the task.</typeparam>
public partial class TaskCompletionPipe<T> : IAsyncEnumerable<T>
    where T : Task
{
    // Represents a number of scheduled tasks which can be greater than the number of enqueued tasks
    // because only completed task can be enqueued
    private uint scheduledTasksCount;
    private bool completionRequested;

    /// <summary>
    /// Initializes a new pipe.
    /// </summary>
    /// <param name="capacity">The expected number of tasks to be placed to the pipe.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is less than zero.</exception>
    public TaskCompletionPipe(int capacity = 0)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        pool = new(OnCompleted);
        completedTasks = new(capacity);
    }

    [MethodImpl(MethodImplOptions.Synchronized | MethodImplOptions.NoInlining)]
    private void OnCompleted(Signal signal)
    {
        if (signal.NeedsRemoval)
            RemoveNode(signal);

        pool.Return(signal);
    }

    /// <summary>
    /// Marks the pipe as being complete, meaning no more items will be added to it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pipe is already completed.</exception>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Complete()
    {
        if (completionRequested)
            throw new InvalidOperationException();

        if (scheduledTasksCount == 0U)
        {
            for (LinkedValueTaskCompletionSource<bool>? current = first, next; current is not null; current = next)
            {
                next = current.CleanupAndGotoNext();
                current?.TrySetResult(Sentinel.Instance, value: false);
            }

            first = last = null;
        }

        completionRequested = true;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private bool IsCompleted
    {
        get
        {
            Debug.Assert(Monitor.IsEntered(this));

            return scheduledTasksCount == 0U && completionRequested;
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    private bool TryAdd(T task)
    {
        if (completionRequested)
            throw new InvalidOperationException();

        scheduledTasksCount++;

        if (task.IsCompleted)
        {
            Enqueue(task);
            return true;
        }

        return false;
    }

    private void Enqueue(T task)
    {
        Debug.Assert(Monitor.IsEntered(this));

        completedTasks.Enqueue(task);
        Notify();
    }

    /// <summary>
    /// Adds the task to this pipe.
    /// </summary>
    /// <param name="task">The task to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The pipe is closed.</exception>
    public void Add(T task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!TryAdd(task))
            task.ConfigureAwait(false).GetAwaiter().OnCompleted(new Continuation(this, task).Invoke);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    private ValueTask<bool> TryDequeue(out T? task, CancellationToken token)
    {
        if (completedTasks.TryDequeue(out task))
        {
            Debug.Assert(scheduledTasksCount > 0U);

            scheduledTasksCount--;
            return ValueTask.FromResult(true);
        }

        return IsCompleted
            ? ValueTask.FromResult(false)
            : EnqueueNode().CreateTask(InfiniteTimeSpan, token);
    }

    /// <summary>
    /// Attempts to read the completed task synchronously.
    /// </summary>
    /// <param name="task">The completed task.</param>
    /// <returns><see langword="true"/> if a task was read; otherwise, <see langword="false"/>.</returns>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryRead([MaybeNullWhen(false)]out T task)
    {
        if (completedTasks.TryDequeue(out task))
        {
            Debug.Assert(scheduledTasksCount > 0U);

            scheduledTasksCount--;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Waits for the first completed task.
    /// </summary>
    /// <param name="timeout">The time to wait for the task completion.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns><see langword="true"/> if data is available to read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="TimeoutException">The operation has timed out.</exception>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public ValueTask<bool> WaitToReadAsync(TimeSpan timeout, CancellationToken token = default)
    {
        if (!completedTasks.IsEmpty)
            return ValueTask.FromResult(true);

        return IsCompleted
            ? ValueTask.FromResult(false)
            : EnqueueNode().CreateTask(timeout, token);
    }

    /// <summary>
    /// Waits for the first completed task.
    /// </summary>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns><see langword="true"/> if data is available to read; otherwise, <see langword="false"/>.</returns>
    public ValueTask<bool> WaitToReadAsync(CancellationToken token = default)
        => WaitToReadAsync(InfiniteTimeSpan, token);

    /// <summary>
    /// Gets the enumerator to get the completed tasks.
    /// </summary>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The enumerator over completed tasks.</returns>
    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken token)
    {
        while (await TryDequeue(out var task, token).ConfigureAwait(false))
        {
            if (task is not null)
            {
                Debug.Assert(task.IsCompleted);

                yield return task;
            }
        }
    }

    private sealed class Continuation : Tuple<TaskCompletionPipe<T>, T>
    {
        internal Continuation(TaskCompletionPipe<T> pipe, T task)
            : base(pipe, task)
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Invoke()
        {
            lock (Item1)
            {
                Item1.Enqueue(Item2);
            }
        }
    }
}