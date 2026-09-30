namespace CS2LineupFinder.Contracts;

/// <summary>
/// Supplies the throw origin for a player. Implemented by the plugin, which owns
/// the CSSharp pawn types.
/// </summary>
public interface IMovementProvider
{
    /// <summary>
    /// Builds a <see cref="ThrowOrigin"/> for a player slot.
    /// </summary>
    /// <param name="playerSlot">Engine slot of the throwing player.</param>
    /// <param name="mode">Stance the throw will be executed from.</param>
    /// <returns>The origin, or <see langword="null"/> when the player is not in a throwable state.</returns>
    ThrowOrigin? GetThrowOrigin(int playerSlot, ThrowMode mode);

    /// <summary>
    /// Returns the point the player is currently looking at, used to seed a zone
    /// from the crosshair.
    /// </summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="maxDistance">Maximum trace distance in units.</param>
    /// <returns>The trace result of the crosshair ray.</returns>
    TraceHit GetCrosshairAimPoint(int playerSlot, float maxDistance = 8192f);
}
