using System;
using System.Collections.Concurrent;
using System.Threading;

namespace CS2LineupFinder.Plugin.Simulation;

/// <summary>
/// One cancellation token per player slot, so a disconnect can stop that player's
/// search without touching anybody else's.
/// </summary>
/// <remarks>
/// A search runs on the thread pool and can take the full solve budget. When the
/// player who asked for it leaves, the work is pointless and the world geometry it
/// is tracing may already be gone, so the plugin cancels it explicitly. Tokens are
/// handed out per lookup rather than per session, which keeps the search in flight
/// cancellable even if the session itself is replaced by a reconnect on the same
/// slot.
/// </remarks>
public sealed class CancellationRegistry : IDisposable
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _sources = new();

    private volatile bool _shutdown;

    /// <summary>True once <see cref="Dispose()"/> ran and new searches are refused.</summary>
    public bool IsShuttingDown => _shutdown;

    /// <summary>Number of slots with a live token, for diagnostics.</summary>
    public int Count => _sources.Count;

    /// <summary>Token for a slot, created on first use.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <returns>A token that is cancelled by <see cref="Cancel"/> or by unload.</returns>
    public CancellationToken TokenFor(int playerSlot)
    {
        if (_shutdown)
        {
            return new CancellationToken(canceled: true);
        }

        var source = _sources.GetOrAdd(playerSlot, static _ => new CancellationTokenSource());
        return source.Token;
    }

    /// <summary>Cancels whatever a slot has in flight, e.g. because the player left.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <returns><see langword="true"/> when a live token was cancelled.</returns>
    public bool Cancel(int playerSlot)
    {
        if (!_sources.TryRemove(playerSlot, out var source))
        {
            return false;
        }

        Signal(source);
        return true;
    }

    /// <summary>Cancels and forgets a slot's token, used when a search finishes normally.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    public void Forget(int playerSlot)
    {
        if (_sources.TryRemove(playerSlot, out var source))
        {
            Dispose(source);
        }
    }

    /// <summary>Cancels every search, used when the map changes or the plugin unloads.</summary>
    public void CancelAll()
    {
        foreach (var slot in _sources.Keys)
        {
            if (_sources.TryRemove(slot, out var source))
            {
                Signal(source);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown = true;
        CancelAll();
    }

    private static void Signal(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down, which is the state the caller wanted.
        }
    }

    private static void Dispose(CancellationTokenSource source)
    {
        try
        {
            source.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }
    }
}
