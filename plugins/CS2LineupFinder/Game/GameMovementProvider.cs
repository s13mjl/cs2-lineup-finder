using System;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Commands;
using CS2LineupFinder.Plugin.Config;
using CounterStrikeSharp.API.Core;

namespace CS2LineupFinder.Plugin.Game;

/// <summary>
/// Builds the throw origin from a live player, which is the one thing the solver
/// cannot work out for itself: where the grenade actually leaves the hand.
/// </summary>
/// <remarks>
/// CS2 keeps the throw's own offsets in weapon VData, which is not readable from
/// managed code on a stock server, so the constants here are the measured standing
/// and crouched eye heights plus the jump impulse, all listed in <c>docs/USAGE.md</c>.
/// The eye height is the one that matters: a crouched throw releases 18 units lower,
/// and ignoring that shifts every suggested pitch by a fraction of a degree.
///
/// The aim probe runs through <see cref="GameWorldTracer"/>, so it steps past the
/// thrower and other players the same way a simulated grenade does. When the server
/// cannot trace at all, the zone centre falls back to a fixed distance along the
/// view direction and <see cref="LastAimWasTraced"/> says so, which lets
/// <c>css_lf_zone</c> tell the player what actually happened.
/// </remarks>
public sealed class GameMovementProvider : IMovementProvider
{
    /// <summary>Eye height above the feet while standing, in units.</summary>
    public const float StandingEyeHeight = 64f;

    /// <summary>Eye height above the feet while crouched, in units.</summary>
    public const float CrouchedEyeHeight = 46f;

    /// <summary>Upward velocity a jump throw adds, in units per second.</summary>
    public const float JumpVelocity = 300f;

    /// <summary>Release point offset along the view direction, in units.</summary>
    public const float ReleaseForwardOffset = 16f;

    /// <summary>Release point offset upwards from the eye line, in units.</summary>
    public const float ReleaseUpOffset = 8f;

    /// <summary>Distance a crosshair probe travels when the caller does not say, in units.</summary>
    public const float DefaultAimDistance = 8192f;

    /// <summary>Upper bound on a crosshair probe, in units.</summary>
    public const float MaxAimDistance = 32768f;

    /// <summary>Fallback aim distance used when no configuration is supplied, in units.</summary>
    public const float FallbackAimDistance = 1024f;

    private readonly GameWorldTracer _tracer;
    private readonly Func<PluginConfig>? _config;

    /// <summary>Creates the provider.</summary>
    /// <param name="tracer">Tracer used for the crosshair probe.</param>
    /// <param name="config">Returns the live configuration, for the fallback aim distance.</param>
    public GameMovementProvider(GameWorldTracer tracer, Func<PluginConfig>? config = null)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        _tracer = tracer;
        _config = config;
    }

    /// <summary>
    /// Whether the most recent <see cref="GetCrosshairAimPoint"/> call reached real
    /// geometry, rather than falling back to a fixed distance along the view ray.
    /// </summary>
    public bool LastAimWasTraced { get; private set; }

    /// <inheritdoc />
    public ThrowOrigin? GetThrowOrigin(int playerSlot, ThrowMode mode)
    {
        var pawn = EnginePlayers.Pawn(playerSlot);
        if (pawn is null)
        {
            return null;
        }

        try
        {
            var origin = pawn.AbsOrigin!;
            var feet = new Vec3(origin.X, origin.Y, origin.Z);
            var eyeHeight = mode == ThrowMode.Crouch ? CrouchedEyeHeight : StandingEyeHeight;
            var velocity = pawn.Velocity;

            return new ThrowOrigin
            {
                Feet = feet,
                Eyes = feet + new Vec3(0f, 0f, eyeHeight),
                Velocity = velocity is null
                    ? Vec3.Zero
                    : new Vec3(velocity.X, velocity.Y, velocity.Z),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public TraceHit GetCrosshairAimPoint(int playerSlot, float maxDistance = DefaultAimDistance)
    {
        LastAimWasTraced = false;

        var pawn = EnginePlayers.Pawn(playerSlot);
        if (pawn is null)
        {
            return GameWorldTracer.Miss(Vec3.Zero, maxDistance, Vec3.UnitX);
        }

        Vec3 eyes;
        Vec3 direction;

        try
        {
            var origin = pawn.AbsOrigin!;
            eyes = new Vec3(origin.X, origin.Y, origin.Z + StandingEyeHeight);

            // EyeAngles is the player's own recoil-free view, which is what a player
            // aims with; aim punch only matters mid-spray, when nobody marks a zone.
            var view = pawn.EyeAngles;
            direction = CommandParser.DirectionFromPlayerAngles(view.Y, view.X);
        }
        catch (Exception)
        {
            return GameWorldTracer.Miss(Vec3.Zero, maxDistance, Vec3.UnitX);
        }

        var distance = Math.Clamp(maxDistance, 1f, MaxAimDistance);
        var hit = _tracer.TraceLine(eyes, direction, distance, -1);
        if (hit.DidHit)
        {
            LastAimWasTraced = true;
            return hit;
        }

        // The server cannot trace, so put the zone where the player is looking rather
        // than refusing the command. css_lf_zone reports the difference in chat.
        var fallback = _config is null
            ? FallbackAimDistance
            : (float)Math.Clamp(_config().FallbackAimDistance, 1.0, MaxAimDistance);
        var end = eyes + (direction.Normalized() * fallback);
        return new TraceHit(false, end, Vec3.UnitZ, 1f);
    }
}
