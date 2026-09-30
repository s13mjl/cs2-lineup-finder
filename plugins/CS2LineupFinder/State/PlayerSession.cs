using System;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.State;

/// <summary>
/// Everything the plugin remembers about one player between commands: the throw
/// point they marked, the landing area they described and the throw parameters
/// currently selected. Sessions are cheap value holders; the plugin creates one
/// lazily the first time a player runs a <c>css_lf_*</c> command.
/// </summary>
public sealed class PlayerSession
{
    /// <summary>Creates a session for one player slot.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    public PlayerSession(int playerSlot) => PlayerSlot = playerSlot;

    /// <summary>Engine slot of the player this session belongs to.</summary>
    public int PlayerSlot { get; }

    /// <summary>Last known player name, used as the <c>author</c> of saved line-ups.</summary>
    public string PlayerName { get; set; } = string.Empty;

    /// <summary>
    /// Map the player was last seen on. Captured from the engine on connect and on
    /// every map start, so a request can name the map the physics was calibrated for
    /// even when the session predates the current map.
    /// </summary>
    public string MapName { get; set; } = string.Empty;

    /// <summary>World position marked with <c>css_lf_start</c>, i.e. the throw origin.</summary>
    public Vec3? ThrowPoint { get; set; }

    /// <summary>Eye position captured together with <see cref="ThrowPoint"/>.</summary>
    public Vec3? EyePoint { get; set; }

    /// <summary>
    /// Velocity the player carried when the throw point was marked. A jump throw
    /// inherits it, so it is part of the request rather than a detail.
    /// </summary>
    public Vec3 ThrowVelocity { get; set; } = Vec3.Zero;

    /// <summary>Landing area created with <c>css_lf_zone</c>.</summary>
    public GroundZone? Zone { get; set; }

    /// <summary>Grenade family selected with <c>css_lf_grenade</c>.</summary>
    public GrenadeType GrenadeType { get; set; } = GrenadeType.Smoke;

    /// <summary>Stance selected with <c>css_lf_throw</c>.</summary>
    public ThrowMode ThrowMode { get; set; } = ThrowMode.Stand;

    /// <summary>Mouse button selected with <c>css_lf_button</c>.</summary>
    public ThrowButton Button { get; set; } = ThrowButton.Primary;

    /// <summary>True between <c>css_lf_start</c> and the next successful search or reset.</summary>
    public bool MarkingMode { get; set; }

    /// <summary>Most recent successful search, kept so <c>css_lf_save</c> can persist it.</summary>
    public SolverOutcome? LastOutcome { get; set; }

    /// <summary>Index of the solution <c>css_lf_save</c> should store, 0 based.</summary>
    public int SelectedResultIndex { get; set; }

    /// <summary>True once both a throw point and a landing zone are known.</summary>
    public bool IsReadyForSearch => ThrowPoint.HasValue && Zone is not null;

    /// <summary>Returns the currently selected solution, or <see langword="null"/>.</summary>
    /// <returns>The selected solution.</returns>
    public LineupSolution? SelectedSolution
    {
        get
        {
            var results = LastOutcome?.Results;
            if (results is null || results.Count == 0)
            {
                return null;
            }

            var index = Math.Clamp(SelectedResultIndex, 0, results.Count - 1);
            return results[index];
        }
    }

    /// <summary>Forgets the throw point, the landing zone and the last search result.</summary>
    public void Reset()
    {
        ThrowPoint = null;
        EyePoint = null;
        ThrowVelocity = Vec3.Zero;
        Zone = null;
        MarkingMode = false;
        LastOutcome = null;
        SelectedResultIndex = 0;
    }
}
