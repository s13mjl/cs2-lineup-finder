namespace CS2LineupFinder.Contracts;

/// <summary>
/// Ray/trace abstraction so <c>Core</c> can simulate bounces without referencing
/// CSSharp. The plugin implements this on top of <c>Trace.TraceShape</c>.
/// </summary>
public interface IWorldGeometry
{
    /// <summary>Traces a single ray through the world.</summary>
    /// <param name="origin">Start position in world units.</param>
    /// <param name="direction">Direction, does not need to be normalized.</param>
    /// <param name="maxDistance">Maximum distance to trace.</param>
    /// <param name="ignoreEntityIndex">Entity index to skip, or -1.</param>
    /// <returns>The trace result.</returns>
    TraceHit TraceRay(Vec3 origin, Vec3 direction, float maxDistance, int ignoreEntityIndex = -1);

    /// <summary>Traces a sphere aligned hull of the given radius through the world.</summary>
    /// <param name="origin">Start position in world units.</param>
    /// <param name="direction">Direction, does not need to be normalized.</param>
    /// <param name="maxDistance">Maximum distance to trace.</param>
    /// <param name="radius">Radius of the swept sphere.</param>
    /// <param name="ignoreEntityIndex">Entity index to skip, or -1.</param>
    /// <returns>The trace result.</returns>
    TraceHit TraceSphere(Vec3 origin, Vec3 direction, float maxDistance, float radius, int ignoreEntityIndex = -1);

    /// <summary>Returns the ground height at a horizontal position, or <see langword="null"/> when nothing is hit.</summary>
    /// <param name="position">Position to probe from, typically above the ground.</param>
    /// <param name="maxDrop">How far below <paramref name="position"/> to look.</param>
    /// <returns>The trace result of the downward trace.</returns>
    TraceHit ProbeGround(Vec3 position, float maxDrop);
}
