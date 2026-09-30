using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Abstractions;
using CS2LineupFinder.Plugin.Config;
using CS2LineupFinder.Plugin.Game;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2LineupFinder.Plugin.Visuals;

/// <summary>
/// Spawns the <c>env_beam</c> entities the plugin draws with: one beam plus the two
/// <c>info_target</c> endpoints each beam needs.
/// </summary>
/// <remarks>
/// Two reasons this is a class of its own. Beams are the only part of the plugin
/// that creates entities, so keeping every spawn and every removal in one file makes
/// the leak surface small enough to review. And a beam is a pair of named endpoints
/// plus the beam itself, which is a surprising amount of plumbing for what players
/// just see as a line.
///
/// Every method here must run on the game thread;
/// <see cref="EngineVisuals"/> is responsible for getting there.
/// </remarks>
public sealed class BeamRenderer : IDisposable
{
    /// <summary>Sprite every beam uses. Ships with the game, so nothing has to be precached.</summary>
    public const string DefaultSprite = "materials/sprites/laserbeam.vmat";

    /// <summary>Entity ceiling, so a leaked loop cannot fill the edict list.</summary>
    public const int MaxEntities = 512;

    /// <summary>Beam thickness in units, wide enough to read from across a site.</summary>
    public const float BeamWidth = 3f;

    private static int _sequence;

    private readonly GameThreadDispatcher _dispatcher;
    private readonly Func<PluginConfig> _config;
    private readonly IPluginMessages _messages;
    private readonly List<CEntityInstance> _spawned = new();

    /// <summary>Creates a renderer.</summary>
    /// <param name="dispatcher">Marshals drawing onto the game thread.</param>
    /// <param name="config">Returns the live configuration, for the sprite and the entity cap.</param>
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

        var config = _config();
        var life = (float)Math.Clamp(durationSeconds, 0.1, 60.0);
        if (!TryReserve(2))
        {
            return false;
        }

        try
        {
            var startTarget = SpawnTarget(start);
            var endTarget = SpawnTarget(end);
            if (startTarget is null || endTarget is null)
            {
                return false;
            }

            var beam = Utilities.CreateEntityByName<CEnvBeam>("env_beam");
            if (beam is null || !Track(beam))
            {
                return false;
            }

            // Spawn() resolves StartEntity/EndEntity into handles, so every field has
            // to be in place before DispatchSpawn, not after.
            beam.SpriteName = string.IsNullOrWhiteSpace(config.BeamSprite) ? DefaultSprite : config.BeamSprite;
            beam.StartEntity = NameOf(startTarget);
            beam.EndEntity = NameOf(endTarget);
            beam.Life = life;
            beam.Width = width;
            beam.EndWidth = width;
            beam.Radius = MathF.Max(width * 2f, 4f);
            beam.Speed = 0;
            beam.FrameStart = 0;
            beam.FrameRate = 0;
            beam.TurnedOff = false;
            beam.TouchType = Touch_t.touch_none;
            beam.DispatchSpawn();

            // FadeLength is a network field rather than a spawn key, and a non-zero
            // value keeps the beam from popping out of existence.
            beam.FadeLength = 4f;
            return true;
        }
        catch (Exception ex)
        {
            _messages.LogWarning($"Failed to draw a beam: {ex.Message}");
            return false;
        }
    }

    /// <summary>Removes every entity this renderer created.</summary>
    public void Clear()
    {
        if (!OnGameThread)
        {
            return;
        }

        for (var i = _spawned.Count - 1; i >= 0; i--)
        {
            Remove(_spawned[i]);
        }

        _spawned.Clear();
    }

    /// <inheritdoc />
    public void Dispose() => Clear();

    private bool TryReserve(int count)
    {
        if (_spawned.Count + count <= MaxEntities)
        {
            return true;
        }

        // Old marks are about to expire anyway; dropping them beats growing forever.
        _messages.LogWarning($"Beam entity cap ({MaxEntities}) reached, dropping the oldest marks.");
        Clear();
        return _spawned.Count + count <= MaxEntities;
    }

    private CInfoTarget? SpawnTarget(Vec3 position)
    {
        var target = Utilities.CreateEntityByName<CInfoTarget>("info_target");
        if (target is null || !Track(target))
        {
            return null;
        }

        using var keyValues = new CEntityKeyValues();
        keyValues.SetString("targetname", NextName());
        keyValues.SetVector("origin", new Vector(position.X, position.Y, position.Z));
        target.DispatchSpawn(keyValues);
        return target;
    }

    private bool Track(CEntityInstance entity)
    {
        try
        {
            _spawned.Add(entity);
            return true;
        }
        catch (Exception ex)
        {
            _messages.LogWarning($"Failed to track a visual entity: {ex.Message}");
            return false;
        }
    }

    private void Remove(CEntityInstance entity)
    {
        try
        {
            if (entity.IsValid)
            {
                entity.Remove();
            }
        }
        catch (Exception)
        {
            // The entity is already gone, which is the state we wanted.
        }
    }

    private static string NameOf(CEntityInstance entity)
    {
        try
        {
            return entity.Entity?.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string NextName() => string.Create(
        CultureInfo.InvariantCulture,
        $"lf_beam_point_{Interlocked.Increment(ref _sequence)}");
}
