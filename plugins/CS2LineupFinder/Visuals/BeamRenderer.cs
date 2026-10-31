using System;
using System.Collections.Generic;
using System.Globalization;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Abstractions;
using CS2LineupFinder.Plugin.Config;
using CS2LineupFinder.Plugin.Game;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2LineupFinder.Plugin.Visuals;

/// <summary>
/// Draws temporary beams with plain <c>beam</c> entities (<see cref="CBeam"/>).
/// </summary>
/// <remarks>
/// <para>
/// An earlier implementation used <c>env_beam</c> plus two named <c>info_target</c>
/// endpoints per beam. Clients failed to parse those beam snapshots and their
/// network queues grew until the engine disconnected them with an overflow error,
/// so the plugin now uses the single-entity <c>beam</c> form the community relies
/// on: position is the entity origin, <see cref="CBeam.EndPos"/> is the far end,
/// and no target names or endpoint entities are involved.
/// </para>
/// <para>
/// <see cref="CBeam"/> has no lifetime field, so the renderer tracks every beam and
/// the plugin's tick handler calls <see cref="Tick"/> to retire expired ones.
/// </para>
/// </remarks>
public sealed class BeamRenderer : IDisposable
{
    /// <summary>Sprite every beam uses. Ships with the game, so nothing has to be precached.</summary>
    public const string DefaultSprite = "materials/sprites/laserbeam.vmat";

    /// <summary>Entity ceiling, so a leaked loop cannot fill the edict list.</summary>
    public const int MaxEntities = 512;

    /// <summary>Beam thickness in units, wide enough to read from across a site.</summary>
    public const float BeamWidth = 3f;

    private readonly struct DrawnBeam(CBeam beam, double expiresAt)
    {
        public CBeam Beam { get; } = beam;
        public double ExpiresAt { get; } = expiresAt;
    }

    private readonly GameThreadDispatcher _dispatcher;
    private readonly Func<PluginConfig> _config;
    private readonly IPluginMessages _messages;
    private readonly List<DrawnBeam> _spawned = new();

    /// <summary>Creates a renderer.</summary>
    /// <param name="dispatcher">Marshals drawing onto the game thread.</param>
    /// <param name="config">Returns the live configuration, for the entity cap.</param>
    /// <param name="messages">Console log used when a spawn fails.</param>
    public BeamRenderer(GameThreadDispatcher dispatcher, Func<PluginConfig> config, IPluginMessages messages)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(messages);
        _dispatcher = dispatcher;
        _config = config;
        _messages = messages;
    }

    /// <summary>Entities currently alive because of this renderer. Game thread only.</summary>
    public int EntityCount => _spawned.Count;

    /// <summary>Whether the calling thread may call the drawing methods.</summary>
    public bool OnGameThread => _dispatcher.IsGameThread;

    /// <summary>
    /// Retires beams whose lifetime has elapsed. Called once per tick from the
    /// plugin's tick handler, on the game thread.
    /// </summary>
    public void Tick()
    {
        if (!OnGameThread || _spawned.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow.TimeOfDay.TotalSeconds;
        for (var i = _spawned.Count - 1; i >= 0; i--)
        {
            if (_spawned[i].ExpiresAt <= now)
            {
                Remove(_spawned[i].Beam);
                _spawned.RemoveAt(i);
            }
        }
    }

    /// <summary>Draws one beam from <paramref name="start"/> to <paramref name="end"/>.</summary>
    /// <param name="start">Beam start in world units.</param>
    /// <param name="end">Beam end in world units.</param>
    /// <param name="durationSeconds">Seconds the beam stays visible.</param>
    /// <param name="width">Beam thickness in units.</param>
    /// <returns><see langword="true"/> when the beam was created.</returns>
    public bool DrawBeam(Vec3 start, Vec3 end, double durationSeconds, float width = BeamWidth)
    {
        if (!OnGameThread)
        {
            return false;
        }

        if (_spawned.Count + 1 > MaxEntities)
        {
            // Old marks are about to expire anyway; dropping the oldest beats growing forever.
            _messages.LogWarning($"Beam entity cap ({MaxEntities}) reached, dropping the oldest marks.");
            _spawned.RemoveRange(0, _spawned.Count / 2);
        }

        try
        {
            var life = (float)Math.Clamp(durationSeconds, 0.1, 60.0);
            var beam = Utilities.CreateEntityByName<CBeam>("beam");
            if (beam is null)
            {
                return false;
            }

            beam.Width = width;
            beam.EndWidth = width;
            beam.StartFrame = 0;
            beam.FrameRate = 0;
            beam.Amplitude = 0;
            beam.Speed = 0;
            beam.Teleport(new Vector(start.X, start.Y, start.Z), new QAngle(), new Vector());
            beam.EndPos.Add(new Vector(end.X, end.Y, end.Z));
            beam.DispatchSpawn();

            var expires = DateTime.UtcNow.TimeOfDay.TotalSeconds + life;
            _spawned.Add(new DrawnBeam(beam, expires));
            return true;
        }
        catch (Exception ex)
        {
            _messages.LogWarning($"Failed to draw a beam: {ex.Message}");
            return false;
        }
    }

    /// <summary>Removes every beam this renderer created.</summary>
    public void Clear()
    {
        if (!OnGameThread)
        {
            return;
        }

        for (var i = _spawned.Count - 1; i >= 0; i--)
        {
            Remove(_spawned[i].Beam);
        }

        _spawned.Clear();
    }

    /// <inheritdoc />
    public void Dispose() => Clear();

    private static void Remove(CBeam beam)
    {
        try
        {
            if (beam.IsValid)
            {
                beam.Remove();
            }
        }
        catch (Exception)
        {
            // The entity is already gone, which is the state we wanted.
        }
    }
}
