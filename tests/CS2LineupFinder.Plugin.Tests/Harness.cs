using System;
using System.IO;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Commands;
using CS2LineupFinder.Plugin.Config;
using CS2LineupFinder.Plugin.Game;
using CS2LineupFinder.Plugin.Persistence;
using CS2LineupFinder.Plugin.Simulation;
using CS2LineupFinder.Plugin.State;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// Wires a <see cref="CommandService"/> out of test doubles and a temporary data
/// directory, so the acceptance tests drive exactly the code a server runs.
/// </summary>
internal sealed class Harness : IDisposable
{
    /// <summary>Creates a harness rooted in its own temporary directory.</summary>
    /// <param name="configToml">Contents of <c>config/config.toml</c>, or null for the defaults.</param>
    /// <param name="map">Map the server is running.</param>
    public Harness(string? configToml = null, string map = "de_mirage")
    {
        Root = Path.Combine(Path.GetTempPath(), "cs2lf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        if (configToml is not null)
        {
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            File.WriteAllText(Path.Combine(Root, "config", "config.toml"), configToml);
        }

        Map = map;
        Config = PluginConfig.Load(Root);
        Messages = new FakeMessages();
        Visuals = new FakeVisuals();
        Clock = new FakeClock();
        Backend = new FakeTraceBackend();
        Lineups = LineupRepository.ForPluginRoot(Root);
        Bridge = new SimulatorBridge(() => TimeSpan.FromSeconds(Config.SolveTimeoutSeconds));
        World = new GameWorldGeometry(Backend);

        Sessions = new SessionStore();
        Service = new CommandService(
            Sessions,
            Lineups,
            Bridge,
            Messages,
            Visuals,
            Clock,
            () => Map,
            () => Config,
            config =>
            {
                Config = config;
                return config;
            });
    }

    /// <summary>Temporary plugin root; holds <c>config/</c> and <c>data/</c>.</summary>
    public string Root { get; }

    /// <summary>Map the server is reported to be running.</summary>
    public string Map { get; private set; }

    /// <summary>Configuration in effect.</summary>
    public PluginConfig Config { get; private set; }

    /// <summary>Chat recorder.</summary>
    public FakeMessages Messages { get; }

    /// <summary>Visual recorder.</summary>
    public FakeVisuals Visuals { get; }

    /// <summary>Fixed clock.</summary>
    public FakeClock Clock { get; }

    /// <summary>Flat floor backend behind <see cref="World"/>.</summary>
    public FakeTraceBackend Backend { get; }

    /// <summary>Saved line-up repository under the temporary root.</summary>
    public LineupRepository Lineups { get; }

    /// <summary>Solver bridge; starts on the built-in stub.</summary>
    public SimulatorBridge Bridge { get; }

    /// <summary>World geometry handed to a search.</summary>
    public GameWorldGeometry World { get; }

    /// <summary>Session store.</summary>
    public SessionStore Sessions { get; }

    /// <summary>The command layer under test.</summary>
    public CommandService Service { get; }

    /// <summary>Player slot used by the tests.</summary>
    public const int Slot = 3;

    /// <summary>Replaces the map the server reports.</summary>
    /// <param name="map">New map name.</param>
    public void SetMap(string map)
    {
        Map = map;
        Sessions.ForEach(session => session.MapName = map);
    }

    /// <summary>
    /// Marks a throw point and creates a landing zone, i.e. everything
    /// <c>css_lf_find</c> needs before it will search.
    /// </summary>
    /// <param name="eyeZ">Height of the marked eyes.</param>
    /// <param name="zone">Landing zone to aim at; defaults to a 500 unit circle straight ahead.</param>
    /// <returns>The session that was prepared.</returns>
    public PlayerSession PrepareSession(float eyeZ = 64f, GroundZone? zone = null)
    {
        var session = Sessions.GetOrCreate(Slot);
        session.MapName = Map;
        session.PlayerName = "Tester";
        session.ThrowPoint = new Vec3(0f, 0f, eyeZ - 64f);
        session.EyePoint = new Vec3(0f, 0f, eyeZ);
        session.Zone = zone ?? new GroundZone
        {
            Type = GroundZoneType.Circle,
            Center = new Vec3(500f, 0f, 0f),
            Radius = 120f,
        };
        return session;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary directory must not fail a test run.
        }
    }
}
