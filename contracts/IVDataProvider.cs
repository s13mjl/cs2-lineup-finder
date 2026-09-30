namespace CS2LineupFinder.Contracts;

/// <summary>
/// Provides grenade physics profiles backed by the game's <c>weapon_*.vdata</c>.
/// The plugin supplies the real implementation; tests supply a stub.
/// </summary>
public interface IVDataProvider
{
    /// <summary>
    /// Returns the physics profile for a grenade family, or <see langword="null"/>
    /// when the item is unavailable on the running build.
    /// </summary>
    /// <param name="grenadeType">Grenade family to look up.</param>
    /// <returns>The profile, or <see langword="null"/>.</returns>
    GrenadeProfile? GetProfile(GrenadeType grenadeType);

    /// <summary>Returns the stance profile for the local server's player movement VData.</summary>
    /// <returns>The stance profile, or <see langword="null"/> when it cannot be resolved.</returns>
    StanceProfile? GetStanceProfile();
}
