using System;
using System.Collections.Concurrent;
using System.Threading;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Abstractions;
using CS2LineupFinder.Plugin.Commands;
using CS2LineupFinder.Plugin.Config;
using CS2LineupFinder.Plugin.Game;
using CounterStrikeSharp.API;

namespace CS2LineupFinder.Plugin.Visuals;

/// <summary>
/// The plugin's <see cref="IPluginVisuals"/> implementation: it owns the geometry
/// of every mark and hands the actual spawning to <see cref="BeamRenderer"/> on the
/// game thread.
/// </summary>
/// <remarks>
/// A search finishes on a worker thread, so drawing has to hop back to the game
/// thread before it touches an entity. The hop is deliberately fire and forget: a
/// player waiting for a line-up should never wait on a sprite.
/// </remarks>
public sealed class EngineVisuals : IPluginVisuals, IDisposable
{
    /// <summary>Half length of the arms of the impact cross, in units.</summary>
    private const float CrossArm = 12f;

    private readonly GameThreadDispatcher _dispatcher;
    private readonly BeamRenderer _renderer;
    private readonly Func<PluginConfig> _config;

    private int _disposed;
    private int _markGeneration;

    /// <summary>
    /// Pending one-per-tick draw steps. DrawZone fills this from the command
    /// thread; <see cref="Step"/> drains exactly one entry per game tick, so a
    /// ring never lands on the wire as a single snapshot burst.
    /// </summary>
    private readonly ConcurrentQueue<Action> _steps = new();

    /// <summary>Creates the visual layer.</summary>
    /// <param name="dispatcher">Marshals drawing onto the game thread.</param>
    /// <param name="renderer">Entity spawner used for every mark.</param>
    /// <param name="config">Returns the live configuration.</param>
    public EngineVisuals(GameThreadDispatcher dispatcher, BeamRenderer renderer, Func<PluginConfig> config)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(config);
        _dispatcher = dispatcher;
        _renderer = renderer;
        _config = config;
    }

    /// <summary>Beam entities currently alive, for diagnostics.</summary>
    public int BeamEntities => _renderer.EntityCount;

    /// <inheritdoc />
    public bool Enabled
    {
        get
        {
            if (_disposed != 0 || _dispatcher.IsShuttingDown)
            {
                return false;
            }

            return _config().EnableVisualization;
        }
    }

    /// <inheritdoc />
    public void DrawAimBeam(int playerSlot, Vec3 origin, Vec3 direction, double durationSeconds)
    {
        var unit = direction.Normalized();
        if (unit.IsZero())
        {
            return;
        }

        var length = (float)_config().BeamLength;
        var end = origin + (unit * length);
        _dispatcher.Post(() => _renderer.DrawBeam(origin, end, durationSeconds));
    }

    /// <inheritdoc />
    public void DrawImpactMarker(Vec3 position, double durationSeconds)
    {
        _dispatcher.Post(() =>
        {
            // A cross reads as a point on the ground from any angle, which a single
            // line would not, so the impact is drawn as two horizontal strokes.
            _renderer.DrawBeam(position + new Vec3(-CrossArm, 0f, 0f), position + new Vec3(CrossArm, 0f, 0f), durationSeconds, 2f);
            _renderer.DrawBeam(position + new Vec3(0f, -CrossArm, 0f), position + new Vec3(0f, CrossArm, 0f), durationSeconds, 2f);
        });
    }

    /// <inheritdoc />
    public void DrawZone(GroundZone zone, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var segments = Math.Clamp(_config().RingSegments, 3, 256);
        // A newer mark supersedes whatever ring is still queued.
        var generation = Interlocked.Increment(ref _markGeneration);
        _steps.Clear();

        for (var i = 0; i < segments; i++)
        {
            var index = i;
            _steps.Enqueue(() =>
            {
                if (Volatile.Read(ref _markGeneration) != generation || _disposed != 0)
                {
                    return;
                }

                if (zone.Type == GroundZoneType.Rectangle)
                {
                    DrawRectangleSegment(zone, index, segments, durationSeconds);
                }
                else
                {
                    var radius = zone.Radius > 0f ? zone.Radius : (float)_config().DefaultZoneRadius;
                    var from = PointOnCircle(zone.Center, 360f * index / segments, radius);
                    var to = PointOnCircle(zone.Center, 360f * (index + 1) / segments, radius);
                    _renderer.DrawBeam(from, to, durationSeconds);
                }
            });
        }
    }

    /// <summary>
    /// Runs at most one queued draw step. Called from the plugin's tick handler on
    /// the game thread - deliberately NOT through <see cref="GameThreadDispatcher"/>,
    /// whose drain loop would immediately re-consume re-posted work in the same tick
    /// and undo the pacing.
    /// </summary>
    public void Step()
    {
        if (_disposed != 0 || _dispatcher.IsShuttingDown)
        {
            return;
        }

        if (_steps.TryDequeue(out var step))
        {
            step();
        }
    }

    /// <inheritdoc />
    public void DrawThrowOrigin(Vec3 position, double durationSeconds)
    {
        _dispatcher.Post(() =>
        {
            // Three axes marked with their own colour, so a player can tell which way
            // the recorded throw point is facing.
            _renderer.DrawBeam(position, position + new Vec3(CrossArm, 0f, 0f), durationSeconds, 2f);
            _renderer.DrawBeam(position, position + new Vec3(0f, CrossArm, 0f), durationSeconds, 2f);
            _renderer.DrawBeam(position, position + new Vec3(0f, 0f, CrossArm), durationSeconds, 2f);
        });
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (_disposed != 0)
        {
            return;
        }

        // Any queued draw steps belong to superseded marks.
        Interlocked.Increment(ref _markGeneration);
        _steps.Clear();

        if (_dispatcher.IsGameThread)
        {
            _renderer.Clear();
            return;
        }

        _dispatcher.Post(_renderer.Clear);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _markGeneration);
        _steps.Clear();
        _renderer.Clear();
        _renderer.Dispose();
    }

    private void DrawRectangleSegment(GroundZone zone, int index, int segments, double durationSeconds)
    {
        var halfWidth = MathF.Max(zone.HalfWidth, 1f);
        var halfHeight = MathF.Max(zone.HalfHeight, 1f);
        var from = PointOnRectangle(zone, segments, index / (float)segments, halfWidth, halfHeight);
        var to = PointOnRectangle(zone, segments, (index + 1) / (float)segments, halfWidth, halfHeight);
        _renderer.DrawBeam(from, to, durationSeconds);
    }

    private static Vec3 PointOnRectangle(GroundZone zone, int segments, float t, float halfWidth, float halfHeight)
    {
        // Walk the perimeter in four equal legs, then rotate into the zone's own yaw.
        var perimeter = t * 4f;
        var leg = (int)MathF.Floor(perimeter);
        var local = perimeter - leg;
        var along = (local * 2f) - 1f;

        var localX = leg switch
        {
            0 => halfWidth,
            1 => -along * halfWidth,
            2 => -halfWidth,
            _ => along * halfWidth,
        };
        var localY = leg switch
        {
            0 => along * halfHeight,
            1 => halfHeight,
            2 => -along * halfHeight,
            _ => -halfHeight,
        };

        var radians = zone.Yaw * (MathF.PI / 180f);
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return zone.Center + new Vec3(
            (localX * cos) - (localY * sin),
            (localX * sin) + (localY * cos),
            0f);
    }

    private static Vec3 PointOnCircle(Vec3 centre, float degrees, float radius)
    {
        var radians = degrees * (MathF.PI / 180f);
        return centre + new Vec3(MathF.Cos(radians) * radius, MathF.Sin(radians) * radius, 0f);
    }
}
