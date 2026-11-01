using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Config;

/// <summary>
/// Runtime settings loaded from <c>config/config.toml</c>. Defaults match the
/// shipped template, so a missing file degrades to a working configuration
/// instead of a failed plugin load.
/// </summary>
public sealed class PluginConfig
{
    /// <summary>Relative path of the configuration file inside the plugin directory.</summary>
    public const string RelativePath = "config/config.toml";

    /// <summary>Wall clock budget for <c>css_lf_find</c>, enforced by the plugin.</summary>
    public double SolveTimeoutSeconds { get; private set; } = 3.0;

    /// <summary>Upper bound on how many solutions are printed and stored per search.</summary>
    public int MaxResults { get; private set; } = 5;

    /// <summary>Master switch for every in-world visual (beams, rings, cross markers).</summary>
    public bool EnableVisualization { get; private set; } = true;

    /// <summary>Seconds the aim beam stays visible after a successful search.</summary>
    public double BeamDurationSeconds { get; private set; } = 3.0;

    /// <summary>Length of the aim beam drawn from the player's eyes, in units.</summary>
    public double BeamLength { get; private set; } = 1024.0;

    /// <summary>Sprite drawn for every beam. The default ships with the game.</summary>
    public string BeamSprite { get; private set; } = "materials/sprites/laserbeam.vmat";

    /// <summary>Number of beam segments used to approximate a landing zone ring.</summary>
    public int RingSegments { get; private set; } = 24;

    /// <summary>Radius applied when the player creates a zone without one, in units.</summary>
    public double DefaultZoneRadius { get; private set; } = 128.0;

    /// <summary>
    /// How far in front of the player a landing zone is placed when the server
    /// cannot trace the crosshair. Ignored on servers that expose the managed
    /// trace API, where the real surface is used.
    /// </summary>
    public double FallbackAimDistance { get; private set; } = 1024.0;

    /// <summary>Language of player-facing text: <c>en</c> or <c>zh-CN</c>.</summary>
    public string Language { get; private set; } = "en";

    /// <summary>
    /// When true, a successful search (or <c>css_lf_load</c> / <c>css_lf_set</c>)
    /// snaps the player's view onto the suggested angles automatically.
    /// </summary>
    public bool AutoAim { get; private set; } = true;

   /// <summary>Upper bound on candidates handed to the simulator per search.</summary>
   public int MaxCandidates { get; private set; } = 64;

    /// <summary>
    /// Yaw grid step in degrees for the coarse angle sweep. Smaller values search
    /// more precisely and take proportionally longer.
    /// </summary>
    public double YawStepDegrees { get; private set; } = 2.0;

    /// <summary>
    /// Elevation grid step in degrees for the coarse angle sweep. Smaller values
    /// search more precisely and take proportionally longer.
    /// </summary>
    public double ElevationStepDegrees { get; private set; } = 3.0;

    /// <summary>
    /// Upper bound on collision sweeps one search may issue against the map. A
    /// search that hits the cap treats the remaining traces as misses, so a broken
    /// candidate cannot keep bouncing until the timeout fires.
    /// </summary>
    // Default derivation: the shipped template used 5,000,000, which a full
    // coarse grid (~4087 candidates x up to ~6000 sweeps) exhausts mid-search
    // (observed 2026-10-15 as "sweep budget exhausted" during lf_find 60).
    // 64,000,000 exceeds the worst-case full-grid need (~24.6M), so the meter
    // now only trips on a genuine runaway loop, never on a legitimate search.
    public int MaxTracesPerSolve { get; private set; } = 64000000;

    /// <summary>Whether the menu also lists the settings and backend diagnostics.</summary>
    public bool VerboseMenu { get; private set; }

    /// <summary>
    /// Whether the plugin replays every recorded telemetry capture and
    /// calibration sample on the game thread 25 s after a map start. Developer
    /// diagnostic: the replay hitches the server for a few hundred ms, so it
    /// is off by default and only the on-demand replay command runs it.
    /// </summary>
    public bool StartupDiagnostics { get; private set; }

    /// <summary>
    /// When true, the plugin forwards chat aliases (<c>!lf_zone</c>) to its console
    /// commands itself via the <c>player_chat</c> game event. CounterStrikeSharp
    /// builds this bridge in from the same chat trigger, but the trigger's engine
    /// signature broke with the CS2 1.41.9.0 update, so the plugin keeps the
    /// aliases alive until the upstream fix ships. Turn it off once the game's
    /// own chat dispatch works again, or to deliberately bridge chat to the
    /// console commands when the native hook is broken. Off by default: when the
    /// native chat trigger works, bridging makes every command run twice.
    /// </summary>
    public bool ChatBridge { get; private set; } = false;

    /// <summary>
    /// Whether the plugin may enable <c>sv_cheats</c> on map start. The view snap
    /// uses the client-side <c>setang</c> command, which is cheat-protected, so a
    /// practice server that wants auto-aim needs this on.
    /// </summary>
    public bool SvCheats { get; private set; } = true;

    /// <summary>Launcher view-angle span searched either side of the zone bearing.</summary>
    public double YawSpanDegrees { get; private set; } = 30.0;

    /// <summary>Lowest release elevation the solver grid may try, degrees above the horizon.</summary>
    public double MinElevationDegrees { get; private set; } = -15.0;

    /// <summary>Highest release elevation the solver grid may try, degrees above the horizon.</summary>
    public double MaxElevationDegrees { get; private set; } = 89.0;

    /// <summary>Number of misses refined around each of the best seeds after the coarse sweep.</summary>
    public int RefineSeeds { get; private set; } = 4;

    /// <summary>Grenade gravity, u/s^2. CS2 runs grenades at 0.4x sv_gravity.</summary>
    public double PhysicsGrenadeGravity { get; private set; } = 320.0;

    /// <summary>Bounce restitution. CS2 mirrors the velocity and scales by 0.45.</summary>
    public double PhysicsRestitution { get; private set; } = 0.45;

    /// <summary>Tangential speed retained at impact; 1.0 matches the engine (no loss).</summary>
    public double PhysicsGroundFriction { get; private set; } = 1.00;

    /// <summary>Coulomb impact friction coefficient; 0 keeps the legacy flat retention.</summary>
    public double PhysicsBounceFrictionMu { get; private set; }

    /// <summary>Extra tangential damping applied to a smoke's first ground contact.</summary>
    public double PhysicsSmokeFirstBounceDamping { get; private set; } = 1.00;

    /// <summary>Extra tangential damping toggle for a smoke's first ground contact.</summary>
    public bool PhysicsSmokeExtraDampingEnabled { get; private set; }

    /// <summary>Exponential tangential decay per second while rolling on the floor.</summary>
    public double PhysicsGroundRollFrictionRate { get; private set; } = 0.0;

    /// <summary>Full-power launch speed, u/s.</summary>
    public double PhysicsFullThrowSpeed { get; private set; } = 675.0;

    /// <summary>
    /// Multiplier on the thrower's own velocity added to the launch. The CS:GO
    /// leak (weapon_basecsgrenade.cpp L614, cstrike15_src mirror) reads
    /// vecThrow = vForward * flVel + (pPlayer->GetAbsVelocity() * 1.25);
    /// telemetry-002 row 0 independently measured 678.63 u/s against the 675
    /// full-throw constant, i.e. 1.25 x 2.9 u/s of thrower motion.
    /// </summary>
    public double PhysicsVelocityInheritance { get; private set; } = 1.25;

    /// <summary>
    /// Vertical pawn speed a jump throw inherits at release, in u/s, before any
    /// live measurement. Measured 215 on this server with CS2 manual jump
    /// throws; the fixed +300 game impulse is only right for a throw released
    /// on the jump's first tick.
    /// </summary>
    public double JumpCarryVelocity { get; private set; } = 215.0;

    /// <summary>
    /// How far above the standing feet the player releases a jump throw, in
    /// units. A CS2 manual jump throw is released a few ticks into the jump,
    /// when the pawn is already ~40 u up; simulating from the standing eye
    /// point puts every jump trajectory that much lower and clips obstacles a
    /// real throw clears. Refined from live throws when confirmed.
    /// </summary>
    public double JumpReleaseRise { get; private set; } = 40.0;

    /// <summary>Upward aim bias the engine applies to every throw, degrees.</summary>
    public double PhysicsAimBiasDegrees { get; private set; } = 10.0;

    /// <summary>Keys present in the file that the plugin does not understand.</summary>
    public IReadOnlyList<string> UnknownKeys { get; private set; } = Array.Empty<string>();

    /// <summary>Directory the settings were loaded from, for diagnostics.</summary>
    public string SourceDirectory { get; private set; } = string.Empty;

    /// <summary>Loads settings from a plugin root directory.</summary>
    /// <param name="pluginRootDirectory">Directory holding <c>config/</c> and <c>data/</c>.</param>
    /// <param name="document">Parsed document, when the caller already has one.</param>
    /// <returns>The loaded configuration.</returns>
    /// <exception cref="TomlFormatException">Thrown when the file is not valid TOML.</exception>
    public static PluginConfig Load(string pluginRootDirectory, TomlDocument? document = null)
    {
        var config = new PluginConfig { SourceDirectory = pluginRootDirectory };
        var path = Path.Combine(pluginRootDirectory, RelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (document is null)
        {
            if (!File.Exists(path))
            {
                return config;
            }

            document = TomlDocument.Load(path);
        }

        config.SolveTimeoutSeconds = ClampPositive(document.GetDouble("solver.timeoutSeconds", config.SolveTimeoutSeconds), 0.1, 300.0);
        config.MaxResults = (int)ClampPositive(document.GetInt("solver.maxResults", config.MaxResults), 1, 50);
       config.MaxCandidates = (int)ClampPositive(document.GetInt("solver.maxCandidates", config.MaxCandidates), 1, 4096);
        config.YawStepDegrees = ClampPositive(document.GetDouble("solver.yawStepDegrees", config.YawStepDegrees), 0.25, 15.0);
        config.ElevationStepDegrees = ClampPositive(document.GetDouble("solver.elevationStepDegrees", config.ElevationStepDegrees), 0.25, 15.0);
        // Upper clamp derivation: a full 4096-candidate grid with the worst-case
        // 6000 sweeps per 12 s trajectory needs ~24.6M sweeps, while the wall
        // clock cap (60 s at ~0.55M sweeps/s measured in-game) allows only ~33M.
        // 64M covers the grid mathematically, so exhaustion can only mean a real
        // runaway, never a legitimate search that simply needed more headroom.
        config.MaxTracesPerSolve = (int)ClampPositive(document.GetInt("solver.maxTracesPerSolve", config.MaxTracesPerSolve), 1, 64000000);
        config.EnableVisualization = document.GetBool("visuals.enabled", config.EnableVisualization);
        config.BeamDurationSeconds = ClampPositive(document.GetDouble("visuals.beamDurationSeconds", config.BeamDurationSeconds), 0.1, 60.0);
        config.BeamLength = ClampPositive(document.GetDouble("visuals.beamLength", config.BeamLength), 64.0, 32768.0);
        config.BeamSprite = document.GetString("visuals.beamSprite", config.BeamSprite);
        config.RingSegments = (int)ClampPositive(document.GetInt("visuals.ringSegments", config.RingSegments), 3, 256);
        config.DefaultZoneRadius = ClampPositive(document.GetDouble("zone.defaultRadius", config.DefaultZoneRadius), 1.0, 8192.0);
        config.FallbackAimDistance = ClampPositive(document.GetDouble("zone.fallbackAimDistance", config.FallbackAimDistance), 64.0, 32768.0);
        config.Language = NormalizeLanguage(document.GetString("general.language", config.Language));
        config.AutoAim = document.GetBool("general.autoAim", config.AutoAim);
        config.VerboseMenu = document.GetBool("general.verboseMenu", config.VerboseMenu);
        config.StartupDiagnostics = document.GetBool("diagnostics.startupReplay", config.StartupDiagnostics);
        config.ChatBridge = document.GetBool("general.chatBridge", config.ChatBridge);
        config.SvCheats = document.GetBool("general.svCheats", config.SvCheats);
        config.YawSpanDegrees = ClampPositive(document.GetDouble("solver.yawSpanDegrees", config.YawSpanDegrees), 5.0, 180.0);
        config.MinElevationDegrees = document.GetDouble("solver.minElevationDegrees", config.MinElevationDegrees);
        config.MaxElevationDegrees = document.GetDouble("solver.maxElevationDegrees", config.MaxElevationDegrees);
        config.RefineSeeds = (int)ClampPositive(document.GetInt("solver.refineSeeds", config.RefineSeeds), 1, 16);
        config.PhysicsGrenadeGravity = ClampPositive(document.GetDouble("physics.grenadeGravity", config.PhysicsGrenadeGravity), 240.0, 400.0);
        config.PhysicsRestitution = ClampPositive(document.GetDouble("physics.restitution", config.PhysicsRestitution), 0.30, 0.70);
        config.PhysicsGroundFriction = ClampPositive(document.GetDouble("physics.groundFriction", config.PhysicsGroundFriction), 0.40, 1.00);
        config.PhysicsBounceFrictionMu = ClampPositive(document.GetDouble("physics.bounceFrictionMu", config.PhysicsBounceFrictionMu), 0.0, 1.0);
        config.PhysicsSmokeFirstBounceDamping = ClampPositive(document.GetDouble("physics.smokeFirstBounceDamping", config.PhysicsSmokeFirstBounceDamping), 0.30, 1.0);
        config.PhysicsSmokeExtraDampingEnabled = document.GetBool("physics.smokeExtraDampingEnabled", config.PhysicsSmokeExtraDampingEnabled);
        config.PhysicsGroundRollFrictionRate = ClampPositive(document.GetDouble("physics.groundRollFrictionRate", config.PhysicsGroundRollFrictionRate), 0.0, 16.0);
        config.PhysicsFullThrowSpeed = ClampPositive(document.GetDouble("physics.fullThrowSpeed", config.PhysicsFullThrowSpeed), 550.0, 800.0);
        config.PhysicsVelocityInheritance = ClampPositive(document.GetDouble("physics.velocityInheritance", config.PhysicsVelocityInheritance), 0.0, 3.0);
        config.JumpCarryVelocity = ClampPositive(document.GetDouble("solver.jumpCarryVelocity", config.JumpCarryVelocity), 0.0, 400.0);
        config.JumpReleaseRise = ClampPositive(document.GetDouble("solver.jumpReleaseRise", config.JumpReleaseRise), 0.0, 120.0);
        config.PhysicsAimBiasDegrees = document.GetDouble("physics.aimBiasDegrees", config.PhysicsAimBiasDegrees);
        config.UnknownKeys = FindUnknownKeys(document);
        return config;
    }

    /// <summary>Builds the environment handed to the simulator.</summary>
    /// <returns>Simulation settings for a 64 tick server.</returns>
    public SimulationEnvironment BuildEnvironment() => new()
    {
        Gravity = 800f,
        TickInterval = 1f / 64f,
        MaxSteps = 4096,
        AirDrag = 0f,
        ThrowerVelocityInheritance = (float)PhysicsVelocityInheritance,
    };

    private static readonly string[] KnownKeys =
    {
        "solver.timeoutSeconds",
        "solver.maxResults",
       "solver.maxCandidates",
       "solver.maxTracesPerSolve",
        "solver.yawStepDegrees",
        "solver.elevationStepDegrees",
       "visuals.enabled",
        "visuals.beamDurationSeconds",
        "visuals.beamLength",
        "visuals.beamSprite",
        "visuals.ringSegments",
        "zone.defaultRadius",
        "zone.fallbackAimDistance",
        "general.language",
        "general.autoAim",
        "general.verboseMenu",
            "general.chatBridge",
            "general.svCheats",
            "solver.yawSpanDegrees",
            "solver.minElevationDegrees",
            "solver.maxElevationDegrees",
            "solver.refineSeeds",
            "physics.grenadeGravity",
            "physics.restitution",
            "physics.groundFriction",
            "physics.bounceFrictionMu",
            "physics.smokeFirstBounceDamping",
            "physics.smokeExtraDampingEnabled",
            "physics.groundRollFrictionRate",
            "physics.fullThrowSpeed",
            "physics.velocityInheritance",
            "physics.aimBiasDegrees",
            "solver.jumpCarryVelocity",
            "solver.jumpReleaseRise",
            "diagnostics.startupReplay",
    };

    private static IReadOnlyList<string> FindUnknownKeys(TomlDocument document)
    {
        List<string>? unknown = null;
        foreach (var key in document.Values.Keys)
        {
            if (Array.IndexOf(KnownKeys, key) >= 0)
            {
                continue;
            }

            (unknown ??= new List<string>()).Add(key);
        }

        return (IReadOnlyList<string>?)unknown ?? Array.Empty<string>();
    }

    private static double ClampPositive(double value, double min, double max)
    {
        if (double.IsNaN(value))
        {
            return min;
        }

        return Math.Clamp(value, min, max);
    }

    private static string NormalizeLanguage(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }

        return "en";
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"timeout={SolveTimeoutSeconds:0.##}s maxResults={MaxResults} visuals={EnableVisualization} lang={Language} chatBridge={ChatBridge}");
}
