using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace CS2LineupFinder.Plugin.Game;

/// <summary>
/// The bridge between the thread pool, where line-up searches run, and the CS2 game
/// thread, which is the only thread allowed to touch engine state.
/// </summary>
/// <remarks>
/// A search issues thousands of collision traces, so handing each one over
/// individually would cost a server tick per trace. Instead the worker parks its
/// request in a queue and <see cref="Drain"/> — called once per tick from the game
/// thread — services a bounded batch of them, which keeps the cost per tick in the
/// low milliseconds while the search itself still runs off the game thread.
/// </remarks>
public sealed class GameThreadDispatcher
{
    /// <summary>How long a parked worker waits before re-checking cancellation, in milliseconds.</summary>
    private const int PollMilliseconds = 25;

    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly object _gate = new();
    private readonly Dictionary<long, PendingCall> _pending = new();

    private long _nextId;
    private int _gameThreadId;
    private volatile bool _shuttingDown;

    /// <summary>Upper bound on how many items one <see cref="Drain"/> pass services.</summary>
    public int MaxItemsPerPass { get; set; } = 256;

    /// <summary>True once the dispatcher is on the game thread, i.e. safe to call engine APIs inline.</summary>
    public bool IsGameThread => _gameThreadId != 0 && Environment.CurrentManagedThreadId == _gameThreadId;

    /// <summary>True after <see cref="Shutdown"/>, when new work is refused.</summary>
    public bool IsShuttingDown => _shuttingDown;

    /// <summary>Invoked for every exception a drained item throws, so the plugin can log it.</summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>Number of fire-and-forget items waiting for the next drain.</summary>
    public int PostedCount => _posted.Count;

    /// <summary>Number of parked synchronous requests waiting for the next drain.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Records the calling thread as the game thread. Called from a plugin callback,
    /// so a request arriving before the first drain still runs inline instead of
    /// waiting for a tick that only the game thread can produce.
    /// </summary>
    public void CaptureGameThread() => Interlocked.CompareExchange(ref _gameThreadId, Environment.CurrentManagedThreadId, 0);

    /// <summary>Queues an action to run on the game thread at the next drain.</summary>
    /// <param name="action">Work to run; exceptions are reported through <see cref="OnError"/>.</param>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_shuttingDown)
        {
            return;
        }

        _posted.Enqueue(action);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the game thread and waits for its result. Called
    /// from a worker thread; when it is already the game thread the work runs inline,
    /// because a nested drain could never happen.
    /// </summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="work">Work to run on the game thread.</param>
    /// <param name="maxWait">Upper bound on the wait, so a missed drain cannot hang the caller.</param>
    /// <param name="cancellationToken">Cancels the wait, e.g. when the search budget expires.</param>
    /// <param name="result">Receives the result when the call was serviced.</param>
    /// <returns><see langword="true"/> when the work ran and produced a result.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> fires.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the work itself failed.</exception>
    public bool TryExecute<T>(
        Func<T> work,
        TimeSpan maxWait,
        CancellationToken cancellationToken,
        out T result)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (IsGameThread)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = work();
            return true;
        }

        // Boxed once here so the pending entry never has to know about T.
        var call = new PendingCall(() => work());
        lock (_gate)
        {
            if (_shuttingDown)
            {
                result = default!;
                return false;
            }

            call.Id = ++_nextId;
            _pending.Add(call.Id, call);
        }

        var stopwatch = Stopwatch.StartNew();
        while (!call.Done.Wait(PollMilliseconds))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Forget(call.Id);
                throw new OperationCanceledException(cancellationToken);
            }

            if (stopwatch.Elapsed >= maxWait)
            {
                Forget(call.Id);
                result = default!;
                return false;
            }
        }

        Forget(call.Id);
        if (call.Abandoned)
        {
            result = default!;
            return false;
        }

        if (call.Error is not null)
        {
            throw new InvalidOperationException("Game thread work failed, see the inner exception.", call.Error);
        }

        result = (T)call.Result!;
        return true;
    }

    /// <summary>
    /// Services queued work. Must be called from the game thread, normally once per
    /// tick.
    /// </summary>
    public void Drain()
    {
        CaptureGameThread();
        if (_shuttingDown)
        {
            return;
        }

        var budget = MaxItemsPerPass;
        while (budget > 0 && _posted.TryDequeue(out var action))
        {
            budget--;
            RunSafely(action);
        }

        if (budget <= 0)
        {
            return;
        }

        List<PendingCall> waiting;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            waiting = new List<PendingCall>(_pending.Values);
        }

        foreach (var call in waiting)
        {
            if (budget-- <= 0)
            {
                break;
            }

            if (!call.TryClaim())
            {
                continue;
            }

            lock (_gate)
            {
                _pending.Remove(call.Id);
            }

            RunSafely(call.Run);
        }
    }

    /// <summary>
    /// Refuses new work and releases everyone currently waiting, so an unload cannot
    /// leave a worker parked.
    /// </summary>
    public void Shutdown()
    {
        _shuttingDown = true;

        List<PendingCall> waiting;
        lock (_gate)
        {
            waiting = new List<PendingCall>(_pending.Values);
            _pending.Clear();
        }

        foreach (var call in waiting)
        {
            call.Abandon();
        }

        while (_posted.TryDequeue(out _))
        {
        }
    }

    private void Forget(long id)
    {
        lock (_gate)
        {
            _pending.Remove(id);
        }
    }

    private void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            try
            {
                OnError?.Invoke(ex);
            }
            catch (Exception)
            {
                // Reporting the failure must never escalate into a second failure.
            }
        }
    }

    /// <summary>One parked synchronous request.</summary>
    private sealed class PendingCall
    {
        private readonly Func<object?> _work;
        private int _claimed;

        internal PendingCall(Func<object?> work) => _work = work;

        internal long Id { get; set; }

        internal ManualResetEventSlim Done { get; } = new(false);

        internal object? Result { get; private set; }

        internal Exception? Error { get; private set; }

        internal bool Abandoned { get; private set; }

        /// <summary>One drain pass owns the call; a release racing with it does not.</summary>
        /// <returns><see langword="true"/> for the single caller that may run the work.</returns>
        internal bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;

        internal void Run()
        {
            try
            {
                Result = _work();
            }
            catch (Exception ex)
            {
                Error = ex;
            }
            finally
            {
                Done.Set();
            }
        }

        /// <summary>Completes the call without running it, after a shutdown.</summary>
        internal void Abandon()
        {
            if (!TryClaim())
            {
                return;
            }

            Abandoned = true;
            Done.Set();
        }
    }
}
