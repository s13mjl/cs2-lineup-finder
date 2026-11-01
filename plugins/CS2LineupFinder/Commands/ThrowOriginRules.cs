// <copyright file="ThrowOriginRules.cs" company="CS2LineupFinder">
// Rules that turn the live thrower pose into the search origin.
// </copyright>

using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Commands;

/// <summary>
/// The two adjustments a jump search applies on top of the live pawn pose.
/// Kept pure so the chat command path and the menu path cannot drift apart,
/// and so the rules are unit testable without an engine.
/// </summary>
public static class ThrowOriginRules
{
    /// <summary>
    /// Minimum pawn rise, in units, that confirms the thrower actually jumped.
    /// A jump climbs ~25 u within the six-tick confirmation window; a stutter
    /// that eats the jump leaves the pawn at its spawn height.
    /// </summary>
    public const float JumpRiseThreshold = 5f;

    /// <summary>
    /// Whether a jump-throw carry measurement may be trusted: the pawn must
    /// have visibly left the ground after the spawn tick. A throw without the
    /// jump measures zero and must not poison the session's carry estimate.
    /// </summary>
    /// <param name="spawnPawnZ">Pawn height when the grenade spawned.</param>
    /// <param name="laterPawnZ">Pawn height a few ticks later.</param>
    /// <returns><see langword="true"/> when the pawn rose enough to count as a jump.</returns>
    public static bool JumpCarryConfirmed(float spawnPawnZ, float laterPawnZ)
        => !float.IsNaN(spawnPawnZ)
            && !float.IsNaN(laterPawnZ)
            && laterPawnZ - spawnPawnZ >= JumpRiseThreshold;
    /// <summary>
    /// Builds the throw origin a search should start from.
    /// </summary>
    /// <param name="live">Origin read from the pawn right now (carries the +300 jump impulse).</param>
    /// <param name="markedVelocity">Velocity recorded when the throw point was marked.</param>
    /// <param name="mode">Stance of the selected throw.</param>
    /// <param name="measuredJumpCarryZ">
    /// Vertical pawn speed measured at the player's last jump throw, if any.
    /// </param>
    /// <param name="configuredJumpCarryZ">
    /// Starting carry from the configuration, used until a live measurement
    /// exists so the first find of a session is already right.
    /// </param>
    /// <param name="measuredJumpReleaseRise">
    /// How far above the standing feet the player's last confirmed jump throw
    /// spawned, if any.
    /// </param>
    /// <param name="configuredJumpReleaseRise">
    /// Starting release height for jump searches until a live measurement
    /// exists.
    /// </param>
    /// <returns>The origin to put on the request.</returns>
    public static ThrowOrigin ForSearch(
        ThrowOrigin live,
        Vec3 markedVelocity,
        ThrowMode mode,
        float? measuredJumpCarryZ,
        float configuredJumpCarryZ,
        float? measuredJumpReleaseRise,
        float configuredJumpReleaseRise)
    {
        var origin = live;

        // A jump throw marked during a run-up must keep the marked horizontal
        // velocity: the search runs while the player stands, so the live pawn
        // has none, while the throw will carry the run-up.
        if (mode == ThrowMode.Jump && markedVelocity.Length2D > 1f)
        {
            origin = origin with
            {
                Velocity = new Vec3(markedVelocity.X, markedVelocity.Y, origin.Velocity.Z),
            };
        }

        // A jump throw leaves the hand a few ticks into the jump: the pawn is
        // already ~40 u up and carries a stable vertical speed. Simulating
        // from the standing eye point instead puts the whole arc that much
        // lower, so a long jump line-up clips an obstacle the real throw
        // clears and the solver misses by hundreds of units.
        if (mode == ThrowMode.Jump)
        {
            float carry = measuredJumpCarryZ ?? configuredJumpCarryZ;
            float rise = measuredJumpReleaseRise ?? configuredJumpReleaseRise;
            origin = origin with
            {
                Feet = new Vec3(origin.Feet.X, origin.Feet.Y, origin.Feet.Z + rise),
                Eyes = new Vec3(origin.Eyes.X, origin.Eyes.Y, origin.Eyes.Z + rise),
                Velocity = new Vec3(origin.Velocity.X, origin.Velocity.Y, carry),
            };
        }

        return origin;
    }
}
