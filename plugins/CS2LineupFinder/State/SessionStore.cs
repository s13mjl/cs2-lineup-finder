using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CS2LineupFinder.Plugin.State;

/// <summary>
/// Thread-safe map of player slot to <see cref="PlayerSession"/>. CSSharp runs
/// commands on the game thread, but timers and the solver continuation can land on
/// the thread pool, so sessions are stored in a concurrent map.
/// </summary>
public sealed class SessionStore
{
    private readonly ConcurrentDictionary<int, PlayerSession> _sessions = new();

    /// <summary>Returns the session for a slot, creating it when needed.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <returns>The session, never <see langword="null"/>.</returns>
    public PlayerSession GetOrCreate(int playerSlot) => _sessions.GetOrAdd(playerSlot, static slot => new PlayerSession(slot));

    /// <summary>Returns the session for a slot without creating one.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <returns>The session, or <see langword="null"/> when the player never ran a command.</returns>
    public PlayerSession? Find(int playerSlot) => _sessions.TryGetValue(playerSlot, out var session) ? session : null;

    /// <summary>Removes the session of a disconnected player.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <returns><see langword="true"/> when a session was removed.</returns>
    public bool Remove(int playerSlot) => _sessions.TryRemove(playerSlot, out _);

    /// <summary>Removes every session, used when the map changes.</summary>
    public void Clear() => _sessions.Clear();

    /// <summary>Current number of sessions, for diagnostics and tests.</summary>
    public int Count => _sessions.Count;

    /// <summary>Slots that currently have a session, for diagnostics and tests.</summary>
    /// <returns>A snapshot of the known slots.</returns>
    public IReadOnlyCollection<int> Slots() => _sessions.Keys.ToArray();
}
