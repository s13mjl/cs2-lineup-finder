using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Config;
using CS2LineupFinder.Plugin.Game;
using CS2LineupFinder.Plugin.Simulation;

namespace CS2LineupFinder.Plugin.Commands;

/// <summary>
/// The glue between one CounterStrikeSharp command call and
/// <see cref="CommandService"/>: it resolves the player, reads the engine state the
/// command needs and hands everything to the engine-free logic.
/// </summary>
/// <remarks>
/// Keeping this in its own file is what makes the command layer testable. Every
/// handler on the plugin class becomes three lines - build a context, call one
/// service method, done - while the "what does the engine say" work happens here.
/// When a player cannot be resolved, each method reports the reason instead of
/// throwing, so a handler never has to defend itself twice.
/// </remarks>
public sealed class LfCommandContext
{
    private readonly CommandService _service;
    private readonly GameMovementProvider _movement;
    private readonly IWorldGeometry _world;
    private readonly CancellationRegistry _cancellations;
    private readonly Func<PluginConfig, PluginConfig> _applyConfig;

    /// <summary>Creates a context for one command call.</summary>
    /// <param name="service">Engine-free command logic.</param>
    /// <param name="movement">Reads the thrower and the crosshair aim point.</param>
    /// <param name="world">Geometry the solver traces against.</param>
    /// <param name="cancellations">Per-player cancellations, so a disconnect stops a search.</param>
    /// <param name="playerSlot">Engine slot of the calling player, or -1 for the server console.</param>
    /// <param name="applyConfig">Publishes a reloaded configuration to everything that reads it.</param>
    /// <param name="root">Plugin root directory, holding <c>config/</c> and <c>data/</c>.</param>
    /// <param name="arguments">Tokens typed after the command name.</param>
    public LfCommandContext(
        CommandService service,
        GameMovementProvider movement,
        IWorldGeometry world,
        CancellationRegistry cancellations,
        Func<PluginConfig, PluginConfig> applyConfig,
        string root,
        int playerSlot,
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(cancellations);
        ArgumentNullException.ThrowIfNull(applyConfig);
        ArgumentNullException.ThrowIfNull(arguments);

        _service = service;
        _movement = movement;
        _world = world;
        _cancellations = cancellations;
        _applyConfig = applyConfig;
        Root = root;
        PlayerSlot = playerSlot;
        Arguments = arguments;
    }

    /// <summary>Engine slot of the calling player, or -1 when the server console called.</summary>
    public int PlayerSlot { get; }

    /// <summary>Plugin root directory, for commands that read a file from it.</summary>
    public string Root { get; }

    /// <summary>Tokens typed after the command name, already split by CounterStrikeSharp.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>First argument, or <see langword="null"/> when the player typed none.</summary>
    public string? First => Arguments.Count > 0 ? Arguments[0] : null;

    /// <summary>True when the server console issued the command rather than a player.</summary>
    public bool IsServerConsole => PlayerSlot < 0;

    /// <summary>Live configuration.</summary>
    public PluginConfig Config => _service.Config;

    /// <summary>Engine-free command logic, for the handlers that only need one call.</summary>
    public CommandService Service => _service;

    /// <summary>
    /// Marks the player's current position as the throw origin, using the stance they
    /// have selected so a crouched mark records the crouched eye height.
    /// </summary>
    /// <returns><see langword="true"/> when the player had a live pawn.</returns>
    public bool MarkStart()
    {
        var mode = _service.Sessions.GetOrCreate(PlayerSlot).ThrowMode;
        var origin = _movement.GetThrowOrigin(PlayerSlot, mode);
        if (origin is null)
        {
            _service.Reply(PlayerSlot, Phrases.Id.NoPawn);
            return false;
        }

        _service.MarkStart(PlayerSlot, origin.Feet, origin.Eyes, origin.Velocity);
        return true;
    }

    /// <summary>Creates the landing zone around the ground point the player is aiming at.</summary>
    public void SetZone()
    {
        var hit = _movement.GetCrosshairAimPoint(PlayerSlot);
        _service.SetZone(
            PlayerSlot,
            Arguments,
            hit.Position,
            _movement.LastAimWasTraced,
            (float)Config.FallbackAimDistance);
    }

    /// <summary>Reloads <c>config/config.toml</c> and publishes it to everything that reads it.</summary>
    /// <returns>The configuration that is now in effect.</returns>
    public PluginConfig ReloadConfig() => _applyConfig(PluginConfig.Load(Root));

    /// <summary>Runs a search and reports it.</summary>
    /// <returns>A task that completes once the result has been shown.</returns>
    public Task<SolverOutcome?> FindAsync() =>
        _service.FindAsync(PlayerSlot, _world, _cancellations.TokenFor(PlayerSlot));

}
