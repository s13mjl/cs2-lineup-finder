using System;
using System.Globalization;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Abstractions;
using CS2LineupFinder.Plugin.Config;
using CS2LineupFinder.Plugin.Persistence;
using CS2LineupFinder.Plugin.Simulation;
using CS2LineupFinder.Plugin.State;

namespace CS2LineupFinder.Plugin.Commands;

/// <summary>
/// Every state change the commands can ask for, decoupled from CSSharp. Each method
/// reports back through <see cref="IPluginMessages"/>, so the same logic runs in the
/// plugin and in the unit tests.
/// </summary>
/// <remarks>
/// The class is split across files: <c>CommandService.cs</c> holds the session
/// mutations, <c>CommandService.Find.cs</c> drives a search and
/// <c>CommandService.Lineups.cs</c> owns save/list/load.
/// </remarks>
public sealed partial class CommandService
{
    private readonly SessionStore _sessions;
    private readonly LineupRepository _lineups;
    private readonly SimulatorBridge _simulator;
    private readonly IPluginMessages _messages;
    private readonly IPluginVisuals _visuals;
    private readonly IClock _clock;
    private readonly Func<string> _currentMap;
    private readonly Func<PluginConfig> _config;
    private readonly Func<PluginConfig, PluginConfig> _applyConfig;

    /// <summary>Creates the service.</summary>
    /// <param name="sessions">Per-player session store.</param>
    /// <param name="lineups">Saved line-up repository.</param>
    /// <param name="simulator">Bridge to the solver stack.</param>
    /// <param name="messages">Chat and log output.</param>
    /// <param name="visuals">In-world drawing.</param>
    /// <param name="clock">Time source stamped into a saved line-up.</param>
    /// <param name="currentMap">Returns the map the server is running, used when a session has none.</param>
    /// <param name="config">Returns the live configuration, so a reload takes effect immediately.</param>
    /// <param name="applyConfig">Publishes a configuration read from disk back to the plugin.</param>
    public CommandService(
        SessionStore sessions,
        LineupRepository lineups,
        SimulatorBridge simulator,
        IPluginMessages messages,
        IPluginVisuals visuals,
        IClock clock,
        Func<string> currentMap,
        Func<PluginConfig> config,
        Func<PluginConfig, PluginConfig> applyConfig)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(lineups);
        ArgumentNullException.ThrowIfNull(simulator);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(visuals);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(currentMap);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(applyConfig);

        _sessions = sessions;
        _lineups = lineups;
        _simulator = simulator;
        _messages = messages;
        _visuals = visuals;
        _clock = clock;
        _currentMap = currentMap;
        _config = config;
        _applyConfig = applyConfig;
    }

    /// <summary>Session store backing this service, also used by the command glue.</summary>
    public SessionStore Sessions => _sessions;

    /// <summary>Solver bridge backing this service.</summary>
    private SimulatorBridge Simulator => _simulator;

    /// <summary>Saved line-up repository backing this service.</summary>
    private LineupRepository Lineups => _lineups;

    /// <summary>In-world drawing backend.</summary>
    private IPluginVisuals Visuals => _visuals;

    /// <summary>Live configuration accessor.</summary>
    public PluginConfig Config => _config();

    /// <summary>
    /// Whether the plugin may solve on the map the server is running. Set from the
    /// map catalogue at map start; when it is false, a search explains instead of
    /// spending the server's time on a map the operator excluded.
    /// </summary>
    public bool MapEnabled { get; set; } = true;

    /// <summary>Language every reply is rendered in.</summary>
    private string Language => _config().Language;

    /// <summary>Map a line-up is filed under when neither the session nor the server names one.</summary>
    private const string UnknownMap = "unknown";

    /// <summary>
    /// Map a player's line-ups belong to: the one their session recorded, or the map
    /// the server is running when the session predates it.
    /// </summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <returns>The map name, never empty.</returns>
    private string MapFor(int playerSlot)
    {
        var recorded = _sessions.GetOrCreate(playerSlot).MapName;
        if (!string.IsNullOrWhiteSpace(recorded))
        {
            return recorded;
        }

        var live = _currentMap();
        return string.IsNullOrWhiteSpace(live) ? UnknownMap : live;
    }

    /// <summary>Marks the player's current position as the throw origin.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="feet">Position of the player's feet.</param>
    /// <param name="eyes">Position of the player's eyes.</param>
    /// <param name="velocity">Player velocity at the moment of marking, used by jump throws.</param>
    public void MarkStart(int playerSlot, Vec3 feet, Vec3 eyes, Vec3 velocity = default)
    {
        var session = _sessions.GetOrCreate(playerSlot);
        session.ThrowPoint = feet;
        session.EyePoint = eyes;
        session.ThrowVelocity = velocity;
        session.MarkingMode = true;

        Reply(playerSlot, Phrases.Id.MarkedThrowPoint, CommandParser.FormatPoint(feet));

        if (_visuals.Enabled)
        {
            _visuals.DrawThrowOrigin(feet, _config().BeamDurationSeconds);
        }
    }

    /// <summary>Creates or replaces the landing zone around a traced ground point.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="arguments">Tokens after the command name, already split.</param>
    /// <param name="aimPoint">Ground point the player is looking at.</param>
    /// <param name="aimWasTraced">Whether <paramref name="aimPoint"/> came from a real trace.</param>
    /// <param name="fallbackDistance">Distance the fallback point sits at, for the chat note.</param>
    public void SetZone(int playerSlot, IReadOnlyList<string> arguments, Vec3 aimPoint, bool aimWasTraced = true, float fallbackDistance = 0f)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var config = _config();
        var session = _sessions.GetOrCreate(playerSlot);

        if (!CommandParser.TryParseZone(arguments, (float)config.DefaultZoneRadius, out var shape, out var error))
        {
            if (error is null)
            {
                Reply(playerSlot, Phrases.Id.BadRadius);
            }
            else
            {
                Reply(playerSlot, Phrases.Id.UnknownOption, error, "css_lf_zone circle <radius> | css_lf_zone rect <width> <height>");
            }

            return;
        }

        session.Zone = shape!.WithCenter(aimPoint);
        Reply(playerSlot, Phrases.Id.LandingZone, Phrases.DescribeZone(session.Zone, Language));
        if (!aimWasTraced)
        {
            Reply(playerSlot, Phrases.Id.ZoneNotTraced, fallbackDistance);
        }

        if (_visuals.Enabled)
        {
            _visuals.DrawZone(session.Zone, config.BeamDurationSeconds);
        }
    }

    /// <summary>Selects the grenade family.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="token">Token typed after the command name.</param>
    /// <param name="errorText">Localized usage text shown when the token is unknown.</param>
    /// <returns><see langword="true"/> when the token was accepted.</returns>
    public bool SetGrenade(int playerSlot, string? token, string errorText)
    {
        if (!CommandUsage.TryParseGrenadeType(token, out var grenade))
        {
            Reply(playerSlot, Phrases.Id.UnknownOption, token ?? string.Empty, errorText);
            return false;
        }

        _sessions.GetOrCreate(playerSlot).GrenadeType = grenade;
        Reply(playerSlot, Phrases.Id.GrenadeSet, CommandUsage.Token(grenade));
        return true;
    }

    /// <summary>Selects the throw stance.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="token">Token typed after the command name.</param>
    /// <param name="errorText">Localized usage text shown when the token is unknown.</param>
    /// <returns><see langword="true"/> when the token was accepted.</returns>
    public bool SetThrowMode(int playerSlot, string? token, string errorText)
    {
        if (!CommandUsage.TryParseThrowMode(token, out var mode))
        {
            Reply(playerSlot, Phrases.Id.UnknownOption, token ?? string.Empty, errorText);
            return false;
        }

        _sessions.GetOrCreate(playerSlot).ThrowMode = mode;
        Reply(playerSlot, Phrases.Id.ThrowModeSet, CommandUsage.Token(mode));
        return true;
    }

    /// <summary>Selects the mouse button combination.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="token">Token typed after the command name.</param>
    /// <param name="errorText">Localized usage text shown when the token is unknown.</param>
    /// <returns><see langword="true"/> when the token was accepted.</returns>
    public bool SetButton(int playerSlot, string? token, string errorText)
    {
        if (!CommandUsage.TryParseThrowButton(token, out var button))
        {
            Reply(playerSlot, Phrases.Id.UnknownOption, token ?? string.Empty, errorText);
            return false;
        }

        _sessions.GetOrCreate(playerSlot).Button = button;
        Reply(playerSlot, Phrases.Id.ButtonSet, CommandUsage.Token(button));
        return true;
    }

    /// <summary>
    /// Builds the request for a player from their session, or reports what is missing.
    /// Shared by <c>css_lf_find</c> and the tests.
    /// </summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="explain">Whether a missing prerequisite is reported in chat.</param>
    /// <returns>The request, or <see langword="null"/> when the session is incomplete.</returns>
    public LineupRequest? BuildRequest(int playerSlot, bool explain = true)
    {
        var session = _sessions.GetOrCreate(playerSlot);
        if (session.ThrowPoint is null || session.EyePoint is null)
        {
            if (explain)
            {
                Reply(playerSlot, Phrases.Id.NeedThrowPoint);
            }

            return null;
        }

        if (session.Zone is null)
        {
            if (explain)
            {
                Reply(playerSlot, Phrases.Id.NeedZone);
            }

            return null;
        }

        var config = _config();
        return new LineupRequest
        {
            MapName = session.MapName,
            GrenadeType = session.GrenadeType,
            ThrowMode = session.ThrowMode,
            Button = session.Button,
            Origin = new ThrowOrigin
            {
                Feet = session.ThrowPoint.Value,
                Eyes = session.EyePoint.Value,
                Velocity = session.ThrowVelocity,
            },
            TargetZone = session.Zone,
            Environment = config.BuildEnvironment(),
            MaxCandidates = config.MaxCandidates,
        };
    }

    /// <summary>Reports the search settings currently in effect.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    public void ReportSettings(int playerSlot)
    {
        var config = _config();
        Reply(playerSlot, Phrases.Id.SearchSettings, config.MaxCandidates, config.SolveTimeoutSeconds, config.MaxResults);
        if (_simulator.UsingStub)
        {
            Reply(playerSlot, Phrases.Id.StubSimulator);
        }
    }

    /// <summary>Forgets the player's throw point, landing zone and last result.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    public void Clear(int playerSlot)
    {
        _sessions.GetOrCreate(playerSlot).Reset();
        Reply(playerSlot, Phrases.Id.Cleared);
    }

    /// <summary>Removes every entity the plugin has drawn in the world.</summary>
    public void ClearVisuals() => _visuals.Clear();

    /// <summary>
    /// Takes a configuration read from disk as the one in effect. The reload command
    /// reads the file and hands the result here, so the same path serves the plugin
    /// and the tests, which can then assert what a player is told after a reload.
    /// </summary>
    /// <param name="config">Configuration to publish.</param>
    /// <returns>The configuration that is now in effect.</returns>
    public PluginConfig ApplyConfig(PluginConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return _applyConfig(config);
    }

    /// <summary>Sends a chat line prefixed with the plugin tag.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="id">Message index, see <see cref="Phrases.Id"/>.</param>
    /// <param name="args">Format arguments.</param>
    public void Reply(int playerSlot, int id, params object?[] args)
        => _messages.Reply(playerSlot, Phrases.Prefixed(Language, id, args));

    /// <summary>Formats a number using the invariant culture.</summary>
    /// <param name="value">Value to format.</param>
    /// <returns>The formatted number.</returns>
    private static string Invariant(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
