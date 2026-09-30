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

    /// <summary>
    /// Returns the session for a slot with an extra preparation step applied,
    /// creating the session first when the player has never used a command.
    /// </summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="prepare">Applied to the session; may run more than once.</param>
    /// <returns>The session, never <see langword="null"/>.</returns>
    /// <remarks>
    /// A concurrent dictionary may build a value that loses an insertion race, so
    /// the winning session is prepared after the entry is in place. Preparing inside
    /// the factory would silently drop the work on the losing thread.
    /// </remarks>
    public PlayerSession GetOrCreate(int playerSlot, Action<PlayerSession> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);

        var session = GetOrCreate(playerSlot);
        prepare(session);
        return session;
    }

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

    /// <summary>Applies a change to every session, used when the map changes.</summary>
    /// <param name="apply">Applied to each session in turn.</param>
    public void ForEach(Action<PlayerSession> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        foreach (var session in _sessions.Values)
        {
            apply(session);
        }
    }

    /// <summary>Current number of sessions, for diagnostics and tests.</summary>
    public int Count => _sessions.Count;

    /// <summary>Slots that currently have a session, for diagnostics and tests.</summary>
    /// <returns>A snapshot of the known slots.</returns>
    public IReadOnlyCollection<int> Slots() => _sessions.Keys.ToArray();
}
