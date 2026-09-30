using System;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Abstractions;

/// <summary>
/// Everything the command layer needs from the running server. Keeping it behind an
/// interface means the command logic can be exercised by unit tests with a recorder,
/// while the plugin supplies the CSSharp implementation.
/// </summary>
public interface IPluginMessages
{
    /// <summary>Sends a line of chat to one player.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="text">Already formatted, localized text.</param>
    void Reply(int playerSlot, string text);

    /// <summary>Sends a line to the server console.</summary>
    /// <param name="text">Text to log.</param>
    void Log(string text);

    /// <summary>Sends a warning to the server console.</summary>
    /// <param name="text">Text to log.</param>
    void LogWarning(string text);
}

/// <summary>
/// In-world drawing the command layer can request. The plugin implements this with
/// <c>env_beam</c> entities; tests record the calls instead.
/// </summary>
public interface IPluginVisuals
{
    /// <summary>True when the operator has visualization enabled in config.toml.</summary>
    bool Enabled { get; }

    /// <summary>Draws the aim beam a player should align with.</summary>
    /// <param name="playerSlot">Engine slot of the player to draw for.</param>
    /// <param name="origin">Beam start, normally the player's eyes.</param>
    /// <param name="direction">Unit direction of the suggested throw.</param>
    /// <param name="durationSeconds">How long the beam stays visible.</param>
    void DrawAimBeam(int playerSlot, Vec3 origin, Vec3 direction, double durationSeconds);

    /// <summary>Draws a cross marker where the grenade is expected to land.</summary>
    /// <param name="position">Impact point.</param>
    /// <param name="durationSeconds">How long the marker stays visible.</param>
    void DrawImpactMarker(Vec3 position, double durationSeconds);

    /// <summary>Draws the outline of a landing zone.</summary>
    /// <param name="zone">Zone to outline.</param>
    /// <param name="durationSeconds">How long the outline stays visible.</param>
    void DrawZone(GroundZone zone, double durationSeconds);

    /// <summary>Draws a marker at the player's marked throw point.</summary>
    /// <param name="position">Throw origin.</param>
    /// <param name="durationSeconds">How long the marker stays visible.</param>
    void DrawThrowOrigin(Vec3 position, double durationSeconds);

    /// <summary>Removes every entity the plugin has drawn.</summary>
    void Clear();
}

/// <summary>
/// Time source for the command layer, so a saved line-up's <c>createdAt</c> is
/// deterministic under test.
/// </summary>
public interface IClock
{
    /// <summary>Current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>Clock backed by the system, used by the plugin.</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
