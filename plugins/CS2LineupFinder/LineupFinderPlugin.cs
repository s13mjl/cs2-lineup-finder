using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Abstractions;
using CS2LineupFinder.Plugin.Calibration;
using CS2LineupFinder.Plugin.Catalog;
using CS2LineupFinder.Plugin.Commands;
using CS2LineupFinder.Plugin.Config;
using CS2LineupFinder.Plugin.Game;
using CS2LineupFinder.Plugin.Persistence;
using CS2LineupFinder.Plugin.Simulation;
using CS2LineupFinder.Plugin.State;
using CS2LineupFinder.Plugin.Telemetry;
using CS2LineupFinder.Plugin.Visuals;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Option = CS2LineupFinder.Plugin.Commands.CommandUsage;

namespace CS2LineupFinder.Plugin;

/// <summary>
/// CounterStrikeSharp entry point of the line-up finder: it owns the service graph,
/// registers the <c>css_lf_*</c> commands and keeps the plugin's state in sync with
/// the map and the players on it.
/// </summary>
/// <remarks>
/// The class is deliberately thin. Everything with a decision in it lives one layer
/// down - <see cref="CommandService"/> for the command behaviour,
/// <see cref="SimulatorBridge"/> for the solve budget, <see cref="GameWorldTracer"/> for
/// collisions - so a handler here only builds a context and calls one method. That is
/// also what makes the acceptance criteria testable without a game server.
///
/// Chat aliases need no code: CounterStrikeSharp rewrites <c>!lf_zone r 200</c> to
    /// <c>css_lf_zone r 200</c> and dispatches it here, which is why the usage text in
    /// every <see cref="CommandHelperAttribute"/> names the <c>lf_</c> form.
    /// </remarks>
    public sealed class LineupFinderPlugin : BasePlugin
    {
        private ServiceGraph? _services;

        private int _startupReplayDelayTicks;

        private int _pawnProbeDelayTicks;

        private int _pawnProbeAttempts;

    private MapCatalog? _catalog;

    private string _loadError = string.Empty;

    private GrenadeTelemetryRecorder? _telemetry;
    private int _telemetryIndex;
    private Vec3? _calibrationLaunchPosition;
    private Vec3? _calibrationLaunchVelocity;
    private long _calibrationLaunchTickMs;
    private long _calibrationLaunchSuggestionNumber;
    private float _telemetryTickTime;
    private int _lastEngineTick = -1;
    private CHandle<CBaseEntity>? _telemetryEntity;

    // Pending jump-carry measurement: the carry derived from a smoke's launch
    // state, confirmed six ticks later once the player is visibly airborne.
    private uint _carryMeasuredHandle;
    private int _launchCarryProbeSlot = -1;
    private float _launchCarryProbeZ;
    private float _launchCarryProbePawnZ;
    private float _launchCarryProbeRise = float.NaN;
    private int _launchCarryProbeTicksLeft;

    /// <inheritdoc />
    public override string ModuleName => "CS2 Line-up Finder";

    /// <inheritdoc />
    public override string ModuleVersion => "0.1.0";

    /// <inheritdoc />
    public override string ModuleAuthor => "CS2LineupFinder contributors";

    /// <inheritdoc />
    public override string ModuleDescription =>
        "Finds grenade line-ups on the map the player is standing on: !lf_menu, !lf_start, !lf_zone, !lf_find.";

    /// <summary>Configuration currently in effect.</summary>
    internal PluginConfig Config { get; private set; } = new();

    /// <inheritdoc />
    public override void Load(bool hotReload)
    {
        try
        {
            var root = string.IsNullOrEmpty(ModuleDirectory) ? AppContext.BaseDirectory : ModuleDirectory;
            Config = PluginConfig.Load(root);

            var services = new ServiceGraph(this, root);
            services.Build();
            _services = services;

            // The timer is the only thing that lets a search running on the thread
            // pool reach the engine, so it is registered before anything can queue
            // work for it.
            AddTickTimer(1, OnTick, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
            services.Dispatcher.CaptureGameThread();

            // Beams need their sprite material in the resource system before any
            // env_beam reaches a client; otherwise each beam message fails to parse
            // and the client eventually disconnects with an overflow error.
            RegisterListener<Listeners.OnServerPrecacheResources>(OnServerPrecacheResources);

            // CS2 1.41.9.0 moved the code the chat trigger hook attaches to, so the
            // game-event plumbing CounterStrikeSharp builds on it died with the
            // update: chat reaches the console log, but no plugin sees it. The
            // player_chat game event still fires, so forward our own aliases from
            // here until the upstream signature fix ships. See OnChatBridge.
            RegisterEventHandler<EventPlayerChat>(OnChatBridge);
            RegisterEventHandler<EventSmokegrenadeDetonate>(OnSmokeDetonated);

            Logger.LogInformation(
                "CS2LineupFinder loaded: {Config} {Trace} {Core}",
                Config.ToString(),
                services.Tracer.BackendName,
                services.CoreSummary);

            foreach (var problem in Config.UnknownKeys)
            {
                Logger.LogWarning("CS2LineupFinder: config.toml has an unknown key '{Key}'.", problem);
            }

            if (hotReload)
            {
                OnMapStart(Server.MapName);
            }
        }
        catch (Exception ex)
        {
            // A plugin that throws out of Load is unloaded by the host, and then the
            // commands a player types explain nothing. Record the reason, keep the
            // commands registered, and answer with it.
            _loadError = ex.Message;
            Logger.LogError(ex, "CS2LineupFinder failed to start; commands will report the reason.");
        }
    }

    /// <inheritdoc />
    public override void Unload(bool hotReload)
    {
        _services?.Shutdown();
        _services = null;
        _catalog = null;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _services?.Shutdown();
            _services = null;
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Registers the beam sprite with the resource system during the server's
    /// precache phase. Must happen before the first beam is networked; a map
    /// change re-runs it automatically.
    /// </summary>
    private void OnServerPrecacheResources(ResourceManifest manifest)
    {
        manifest.AddResource(BeamRenderer.DefaultSprite);
    }

    /// <summary>Counter for calibration sample names.</summary>
    /// <summary>
    /// Handles the smoke-blooms event: pairs the real detonation with the
    /// suggestion the player was given, records a calibration sample and reports
    /// the miss distance. This is the ground truth feed for parameter fitting.
    /// </summary>
    private HookResult OnSmokeDetonated(EventSmokegrenadeDetonate @event, GameEventInfo info)
    {
        try
        {
            Logger.LogInformation(
                "CS2LineupFinder: smoke detonated at ({X:0.#}, {Y:0.#}, {Z:0.#})",
                @event.X, @event.Y, @event.Z);

            FlushTelemetry(new Vec3(@event.X, @event.Y, @event.Z));

            var services = _services;
            var thrower = @event.Userid;
            if (services is null || thrower is null)
            {
                return HookResult.Continue;
            }
 
            var slot = thrower.Slot;
            var pending = services.Commands.TakeSuggestionForCalibration(slot, GrenadeType.Smoke);
            if (pending is null)
            {
                return HookResult.Continue;
            }
 
            var impact = new Vec3(@event.X, @event.Y, @event.Z);

            // The throw-time feet position is what the prediction was based on;
            // the pawn has usually moved again by the time the smoke detonates,
            // so reading the pawn here would corrupt the sample.
            Vec3 feet = pending.Origin;

            // Pair the launch capture with this sample only when BOTH refer to
            // the same suggestion and the detonation arrived within one long
            // smoke flight (512 ticks = 8 s; telemetry-001's longest recorded
            // flight was 5.78 s, so the previous 4 s window truncated long lobs
            // and silently fell back to the synthesised launch). Anything else
            // is a stale capture from an unrelated grenade and must not
            // contaminate the sample; the synthesised-launch fallback stays
            // correct for stationary throws.
            Vec3? launchPosition = null;
            Vec3? launchVelocity = null;
            long nowMs = Environment.TickCount64;
            if (LineupFinderPlugin.IsLaunchCaptureUsable(
                    _calibrationLaunchTickMs,
                    _calibrationLaunchSuggestionNumber,
                    nowMs,
                    pending))
            {
                launchPosition = _calibrationLaunchPosition;
                launchVelocity = _calibrationLaunchVelocity;
            }

            _calibrationLaunchPosition = null;
            _calibrationLaunchVelocity = null;
            _calibrationLaunchTickMs = 0;
            _calibrationLaunchSuggestionNumber = 0;

            // Re-simulate the flight from the captured launch state so the
            // sample records the model's own error separately from the
            // pre-throw prediction: Error is what the player was promised, the
            // replay error is what the physics produced from the throw that
            // actually left the hand. One simulation on the game thread, at a
            // point where no search is ticking that tick.
            Vec3? replayImpact = null;
            if (launchPosition is { } replayFrom
                && launchVelocity is { } replayVelocity
                && !services.Simulator.UsingStub
                && GameWorldGeometry.CanTrace(services.World))
            {
                try
                {
                    services.World.ResetBudget();
                    TrajectoryResult replay = services.Simulator.Simulator.Simulate(
                        new ThrowParams(replayFrom, replayVelocity, GrenadeType.Smoke, 1f, 64),
                        services.World);
                    replayImpact = replay.Impact.Position;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("CS2LineupFinder: launch replay failed: {Message}", ex.Message);
                }
            }

            // Continue the day-file numbering across restarts: the calibrator
            // pre-loads today's samples, so the next index is Count + 1.
            int index = services.Landings.Count + 1;
            var sample = services.Landings.Record(
                index,
                Server.MapName ?? string.Empty,
                feet,
                pending.EyeHeight,
                pending,
                impact,
                pending.ReleasePosition,
                launchPosition,
                launchVelocity,
                pending.SuggestionNumber,
                replayImpact);
 
            if (sample is not null)
            {
                services.Commands.Reply(
                    slot,
                    Phrases.Id.CalibrationSample,
                    sample.Error,
                    services.Landings.Count);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CS2LineupFinder: landing capture failed: {Message}", ex.Message);
        }
 
        return HookResult.Continue;
    }

    /// <summary>
    /// Closes out the live telemetry capture, if any. Runs on the game thread
    /// from the detonation event, so no extra synchronisation is needed.
    /// </summary>
    private void FlushTelemetry(Vec3 endPosition)
    {
        var recorder = _telemetry;
        if (recorder is null)
        {
            return;
        }

        _telemetry = null;
        _telemetryEntity = null;
        _telemetryIndex++;
        recorder.Flush(_telemetryIndex, endPosition);
    }

    /// <summary>Converts a CSSharp vector into the contract vector.</summary>
    private static Vec3 ToContract(Vector v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// Writes the live projectile's own collision-filter fields to the log once
    /// per capture: the values the engine gave the grenade decide which trace
    /// mask the simulator must use, so they are recorded verbatim.
    /// </summary>
    private void LogGrenadeCollisionFilter(CBaseEntity entity)
    {
        Logger.LogInformation(
            "CS2LineupFinder: {Text}",
            Telemetry.GrenadeCollisionFilterDump.Describe(entity));
    }

    /// <summary>
    /// Finds the live smoke projectile and records its server-side position and
    /// velocity. Entity spawn fires before our listener sees the map, so the
    /// recorder attaches lazily from the tick loop: once per tick the entity
    /// list is scanned only while no capture is active, which is the idle state
    /// 99% of the time.
    /// </summary>
    private void TickTelemetry(int engineTick)
    {
        // Enumerate once per tick: the launch-carry scan must see every fresh
        // smoke even while another capture is still tracking its own grenade.
        var spawned = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("smokegrenade_projectile");
        ScanLaunchCarry(spawned);

        if (_telemetry is not null)
        {
            var entity = _telemetryEntity?.Get()?.As<CBaseEntity>();
            if (entity is null || !entity.IsValid)
            {
                // The grenade vanished without a detonate event (map change,
                // round restart, admin kill): close the capture anyway.
                var orphan = _telemetry;
                _telemetry = null;
                _telemetryIndex++;
                Logger.LogInformation(
                    "CS2LineupFinder: telemetry entity lost after {Count} ticks, flushing",
                    orphan.Count);
                orphan.Flush(_telemetryIndex, Vec3.Zero);
                return;
            }

            // Real server tick interval, not an assumed 64: the 2026-10-15
            // captures arrived at a 128 Hz cadence, and the fitted gravity only
            // reads 320 u/s^2 once the time axis matches that cadence.
            _telemetryTickTime += NativeAPI.GetTickInterval();
            _telemetry.Record(
                _telemetryTickTime,
                ToContract(entity.AbsOrigin!),
                ToContract(entity.AbsVelocity!),
                engineTick);
            return;
        }

        // Idle: look for a freshly thrown smoke projectile to attach to.
        foreach (var candidate in spawned)
        {
            var velocity = candidate.AbsVelocity;
            if (candidate.IsValid && velocity is not null
                && ((velocity.X * velocity.X) + (velocity.Y * velocity.Y) + (velocity.Z * velocity.Z)) > 1f)
            {
                _telemetry = new GrenadeTelemetryRecorder(
                    Path.Combine(_services!.Root, "data", "telemetry"),
                    text => Logger.LogInformation("CS2LineupFinder: {Text}", text));
                // CHandle's IntPtr constructor stores the argument as the ADDRESS of
                // a raw handle value (CHandle.cs: Marshal.ReadInt32(_pointer)), so
                // passing the entity object pointer here made every later lookup
                // read the entity vtable bytes as a handle and fail. EntityHandle.Raw
                // is the real 32-bit index+serial value read from the entity's own
                // CEntityIdentity, which is what GetEntityByHandle compares against.
                _telemetryEntity = new CHandle<CBaseEntity>(candidate.EntityHandle.Raw);
                _telemetryTickTime = 0f;
                Vec3 launchOrigin = ToContract(candidate.AbsOrigin!);
                Vec3 launchVelocity = ToContract(velocity);
                _telemetry.Record(0f, launchOrigin, launchVelocity, engineTick);

                // Keep the measured launch state for the calibration sample: the
                // projectile's own row-0 state is the only launch reconstruction
                // that includes the thrower's inherited velocity (telemetry-002
                // row 0 measured 678.63 u/s vs the 675 full-throw constant).
                _calibrationLaunchPosition = launchOrigin;
                _calibrationLaunchVelocity = launchVelocity;
                _calibrationLaunchTickMs = Environment.TickCount64;
                _calibrationLaunchSuggestionNumber = _services!.Commands.MaxPendingSuggestionNumber();
                LogGrenadeCollisionFilter(candidate);
                Logger.LogInformation(
                    "CS2LineupFinder: telemetry attached to smoke projectile (speed {Speed:0.#} u/s, handle {Handle:X8})",
                    velocity.Length(),
                    candidate.EntityHandle.Raw);
                break;
            }
        }
    }
 
    /// <summary>
    /// Measures the engine's own jump-throw inheritance from a freshly spawned
    /// smoke. The carried velocity is recovered from the projectile's launch
    /// velocity and the thrower's view, so the reading does not depend on when
    /// the scan happens. It runs every tick, independent of the telemetry
    /// capture: a second smoke thrown while the first was still flying used to
    /// be sampled only after the first landed, with the jump velocity already
    /// decayed, and the values then varied by tens of u/s for identical throws.
    /// </summary>
    /// <param name="spawned">Live smoke projectiles this tick.</param>
    private void ScanLaunchCarry(IEnumerable<CBaseEntity> spawned)
    {
        var services = _services;
        if (services is null || services.Simulator.UsingStub)
        {
            return;
        }

        foreach (var candidate in spawned)
        {
            if (!candidate.IsValid)
            {
                continue;
            }

            uint handle = candidate.EntityHandle.Raw;
            if (handle == _carryMeasuredHandle)
            {
                continue;
            }

            var velocity = candidate.AbsVelocity;
            var origin = candidate.AbsOrigin;
            if (velocity is null || origin is null
                || ((velocity.X * velocity.X) + (velocity.Y * velocity.Y) + (velocity.Z * velocity.Z)) <= 1f)
            {
                continue;
            }

            // Measure each projectile once, on the first tick it is visible.
            _carryMeasuredHandle = handle;
            BeginLaunchCarryProbe(ToContract(origin), ToContract(velocity));
            return;
        }
    }

    /// <summary>
    /// Derives the carry from the launch state as a CANDIDATE. It is only kept
    /// after the pawn has visibly left the ground a few ticks later: a stutter
    /// can cost the player the jump, and a throw without the jump must not
    /// update the session's carry estimate.
    /// </summary>
    /// <param name="launchOrigin">Where the projectile spawned.</param>
    /// <param name="launchVelocity">The projectile's launch velocity.</param>
    private void BeginLaunchCarryProbe(Vec3 launchOrigin, Vec3 launchVelocity)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        int bestSlot = -1;
        float bestDistanceSq = 96f * 96f;
        float bestPawnZ = 0f;
        Vec3 bestAim = Vec3.Zero;
        bool aimRead = false;

        for (var slot = 0; slot < 64; slot++)
        {
            var pawn = EnginePlayers.Pawn(slot);
            if (pawn is null)
            {
                continue;
            }

            try
            {
                Vec3 pawnOrigin = ToContract(pawn.AbsOrigin!);
                float dx = pawnOrigin.X - launchOrigin.X;
                float dy = pawnOrigin.Y - launchOrigin.Y;
                float dz = pawnOrigin.Z - launchOrigin.Z;
                float distanceSq = (dx * dx) + (dy * dy) + (dz * dz);
                if (distanceSq >= bestDistanceSq)
                {
                    continue;
                }

                var view = pawn.EyeAngles;
                bestDistanceSq = distanceSq;
                bestSlot = slot;
                bestPawnZ = pawnOrigin.Z;
                bestAim = CommandParser.DirectionFromPlayerAngles(view.Y, view.X);
                aimRead = true;
            }
            catch (Exception)
            {
                // A pawn disappearing mid-scan must not stop the capture.
            }
        }

        if (bestSlot < 0 || !aimRead)
        {
            return;
        }

        var session = services.Sessions.GetOrCreate(bestSlot);
        if (session.ThrowMode != ThrowMode.Jump)
        {
            return;
        }

        Vec3 carried;
        try
        {
            carried = services.Simulator.Simulator.DeriveCarriedVelocity(
                launchVelocity,
                bestAim,
                session.Button);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CS2LineupFinder: launch carry derivation failed: {Message}", ex.Message);
            return;
        }

        _launchCarryProbeSlot = bestSlot;
        _launchCarryProbeZ = carried.Z;
        _launchCarryProbePawnZ = bestPawnZ;
        _launchCarryProbeRise = session.ThrowPoint is { } markedFeet
            ? bestPawnZ - markedFeet.Z
            : float.NaN;
        _launchCarryProbeTicksLeft = 6;
        Logger.LogInformation(
            "CS2LineupFinder: launch carry candidate: slot={Slot} launch=({VX:0.0},{VY:0.0},{VZ:0.0}) carry=({CX:0.0},{CY:0.0},{CZ:0.0}) (confirming in 6 ticks)",
            bestSlot,
            launchVelocity.X,
            launchVelocity.Y,
            launchVelocity.Z,
            carried.X,
            carried.Y,
            carried.Z);
    }

    /// <summary>
    /// Settles a pending jump-carry measurement: keeps it only if the pawn has
    /// risen off the ground, which a real jump always does within six ticks.
    /// </summary>
    private void ResolveLaunchCarryProbe()
    {
        if (_launchCarryProbeSlot < 0)
        {
            return;
        }

        if (--_launchCarryProbeTicksLeft > 0)
        {
            return;
        }

        int slot = _launchCarryProbeSlot;
        _launchCarryProbeSlot = -1;

        var services = _services;
        if (services is null)
        {
            return;
        }

        float pawnZ = float.NaN;
        try
        {
            var pawn = EnginePlayers.Pawn(slot);
            if (pawn is not null)
            {
                pawnZ = ToContract(pawn.AbsOrigin!).Z;
            }
        }
        catch (Exception)
        {
            pawnZ = float.NaN;
        }

        if (!ThrowOriginRules.JumpCarryConfirmed(_launchCarryProbePawnZ, pawnZ))
        {
            Logger.LogInformation(
                "CS2LineupFinder: launch carry discarded: no jump detected (spawn z {SpawnZ:0.0} -> {PawnZ:0.0})",
                _launchCarryProbePawnZ,
                pawnZ);
            services.Commands.Reply(slot, Phrases.Id.JumpCarryNotConfirmed);
            return;
        }

        var session = services.Sessions.GetOrCreate(slot);
        session.MeasuredJumpCarryZ = _launchCarryProbeZ;
        float releaseRise = float.IsNaN(_launchCarryProbeRise) ? 0f : _launchCarryProbeRise;
        if (releaseRise >= 0f && releaseRise <= 120f)
        {
            session.MeasuredJumpReleaseRise = releaseRise;
        }
        else
        {
            releaseRise = 0f;
        }

        Logger.LogInformation(
            "CS2LineupFinder: jump throw measured: slot={Slot} carry={Carry:0.0} u/s releaseRise={ReleaseRise:0.0}u (pawn rose {Rise:0.0}u)",
            slot,
            _launchCarryProbeZ,
            releaseRise,
            pawnZ - _launchCarryProbePawnZ);
        services.Commands.Reply(slot, Phrases.Id.JumpCarryMeasured, _launchCarryProbeZ, releaseRise);
    }

    /// <summary>Reads the catalogue for the new map and resets every player's session.</summary>
    /// <param name="mapName">Map that just started.</param>
    [ListenerHandler<Listeners.OnMapStart>]
    public void OnMapStart(string mapName)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }
 
        try
        {
            // setang is cheat-protected: without sv_cheats 1 the client-side view
            // snap silently does nothing. The practice server enables cheats on
            // purpose; the config switch keeps it opt-in.
            if (Config.SvCheats)
            {
                Server.ExecuteCommand("sv_cheats 1");
            }

            var catalog = MapCatalog.Load(services.Root);
            _catalog = catalog;
            services.Commands.MapEnabled = catalog.IsEnabled(mapName);

            // A session is a position on a map, so a map change invalidates all of
            // them; the name and slot stay usable, everything measured does not.
            services.Sessions.ForEach(session =>
            {
                session.Reset();
                session.MapName = mapName;
            });

            _carryMeasuredHandle = 0;

            services.Beams.Clear();

            foreach (var problem in catalog.Problems)
            {
                Logger.LogWarning("CS2LineupFinder: {Problem}", problem);
            }

            Logger.LogInformation(
                "CS2LineupFinder on {Map}: {Catalog} points={Points} enabled={Enabled}",
                mapName,
                catalog.Describe(),
                catalog.PointsFor(mapName).Count,
                services.Commands.MapEnabled);

            // The per-tick timer is registered during plugin load, which on a
            // cold boot happens BEFORE map start; STOP_ON_MAPCHANGE then kills
            // it and searches would stay dead until the next plugin reload.
            // Re-register it here: the previous instance died with the old map,
            // so exactly one live tick loop exists per map either way.
            AddTickTimer(1, OnTick, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

            // Forensic replay of every recorded sample on the live map. It is a
            // developer diagnostic that runs synchronously on the game thread
            // and hitches the server for a few hundred ms, so it only runs when
            // diagnostics.startupReplay is enabled. The on-demand replay
            // command covers the same ground without surprising a player who
            // happens to throw while it is running.
            // 64 Hz: one tick is 1/64 s. (The 0.064 divisor previously used here
            // made every "20 s" delay fire after ~5 s of wall time.)
            bool startupDiagnostics = Config.StartupDiagnostics;
            Logger.LogInformation(
                "CS2LineupFinder: startup diagnostics={Enabled} jumpCarry={Carry:0.#} jumpRise={Rise:0.#}",
                startupDiagnostics,
                Config.JumpCarryVelocity,
                Config.JumpReleaseRise);
            _startupReplayDelayTicks = startupDiagnostics ? (int)(25f * 64f) : 0;
            _pawnProbeDelayTicks = startupDiagnostics ? (int)(25f * 64f) : 0;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CS2LineupFinder could not read the map catalogue for {Map}.", mapName);
        }
    }

    private void RunStartupReplay()
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        try
        {
            // Tick-by-tick forensic replay first: it pins the first divergent tick
            // of each recorded capture against the live map, which is the evidence
            // the end-to-end sample errors below are then interpreted against.
            services.Commands.ReplayTelemetryToLog(
                System.IO.Path.Combine(services.Root, "data", "telemetry"),
                services.World,
                message => Logger.LogInformation("CS2LineupFinder: {Replay}", message));
            services.Commands.ReplayCalibrationToLog(
                System.IO.Path.Combine(services.Root, "data", "calibration"),
                Server.MapName ?? string.Empty,
                services.World,
                message => Logger.LogInformation("CS2LineupFinder: {Replay}", message));
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CS2LineupFinder: startup replay failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Asks a live pawn which collision layers it reports. Bots only exist once
    /// a player has joined, so this retries on its own short timer until one is
    /// found (or half an hour passes) instead of tying the telemetry replay to
    /// the server's player schedule.
    /// </summary>
    private void RunPawnProbe()
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        try
        {
            bool pawnFound = false;
            foreach (string line in Diagnostics.PawnCollisionProbe.Run(services.Trace))
            {
                Logger.LogInformation("CS2LineupFinder: {Probe}", line);
                if (line.StartsWith("pawn probe: pawn slot", StringComparison.Ordinal))
                {
                    pawnFound = true;
                }
            }

            if (pawnFound)
            {
                _pawnProbeDelayTicks = 0;
                return;
            }

            _pawnProbeAttempts++;
            _pawnProbeDelayTicks = _pawnProbeAttempts <= 600 ? (int)(3f * 64f) : 0;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CS2LineupFinder: pawn probe failed: {Message}", ex.Message);
            _pawnProbeDelayTicks = 0;
        }
    }

    /// <summary>Remembers the name a saved line-up should be credited to.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="name">Persona name.</param>
    /// <param name="ipAddress">Address the player connected from; unused.</param>
    [ListenerHandler<Listeners.OnClientConnect>]
    public void OnClientConnect(int playerSlot, string name, string ipAddress)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        try
        {
            services.Sessions.GetOrCreate(playerSlot, session =>
            {
                session.PlayerName = name ?? string.Empty;
                session.MapName = Server.MapName;
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CS2LineupFinder: could not prepare a session for slot {Slot}: {Message}", playerSlot, ex.Message);
        }
    }

    /// <summary>Refreshes the name after the player has fully spawned.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    [ListenerHandler<Listeners.OnClientPutInServer>]
    public void OnClientPutInServer(int playerSlot)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        try
        {
            var live = EnginePlayers.Name(playerSlot);
            services.Sessions.GetOrCreate(playerSlot, session =>
            {
                if (!string.IsNullOrEmpty(live))
                {
                    session.PlayerName = live;
                }

                session.MapName = Server.MapName;
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CS2LineupFinder: could not refresh slot {Slot}: {Message}", playerSlot, ex.Message);
        }
    }

    /// <summary>Drops the session and cancels the search of a player who left.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    [ListenerHandler<Listeners.OnClientDisconnect>]
    public void OnClientDisconnect(int playerSlot)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        try
        {
            services.Commands.DropSearch(playerSlot);
            services.Cancellations.Cancel(playerSlot);
            services.Sessions.Remove(playerSlot);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CS2LineupFinder: could not clean up slot {Slot}: {Message}", playerSlot, ex.Message);
        }
    }

    /// <summary>Opens the main menu.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_menu", "Opens the line-up finder menu.")]
    [CommandHelper(minArgs: 0, usage: CommandSpec.NoArguments)]
    public void OnMenuCommand(CCSPlayerController? player, CommandInfo commandInfo)
        => Guard(commandInfo, context => OpenMenu(context.PlayerSlot));

    /// <summary>Marks the player's current position as the throw point.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_start", "Marks your current position as the throw point.")]
    [CommandHelper(minArgs: 0, usage: CommandSpec.NoArguments)]
    public void OnStartCommand(CCSPlayerController? player, CommandInfo commandInfo)
        => Guard(commandInfo, static context => context.MarkStart());

    /// <summary>Creates the landing zone around the point the player is aiming at.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_zone", "Creates the landing zone around the point you are looking at.")]
    [CommandHelper(minArgs: 0, usage: "circle <radius> | rect <width> <height>")]
    public void OnZoneCommand(CCSPlayerController? player, CommandInfo commandInfo)
        => Guard(commandInfo, static context => context.SetZone());

    /// <summary>Selects the grenade family.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_grenade", "Selects the grenade: smoke, flash, molotov or he.")]
    [CommandHelper(minArgs: 1, usage: "smoke | flash | molotov | he")]
    public void OnGrenadeCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(
        commandInfo,
        static context => context.Service.SetGrenade(context.PlayerSlot, context.First, Option.UsageFor("css_lf_grenade")));

    /// <summary>Selects the throw stance.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_throw", "Selects the throw stance: stand, crouch or jump.")]
    [CommandHelper(minArgs: 1, usage: "stand | crouch | jump")]
    public void OnThrowCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(
        commandInfo,
        static context => context.Service.SetThrowMode(context.PlayerSlot, context.First, Option.UsageFor("css_lf_throw")));

    /// <summary>Selects the mouse button combination.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_button", "Selects the mouse button: primary, secondary or both.")]
    [CommandHelper(minArgs: 1, usage: "primary | secondary | both")]
    public void OnButtonCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(
        commandInfo,
        static context => context.Service.SetButton(context.PlayerSlot, context.First, Option.UsageFor("css_lf_button")));

    /// <summary>Searches for a line-up and shows the result.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
   [ConsoleCommand("css_lf_find", "Searches for a line-up and draws the result.")]
    [CommandHelper(minArgs: 0, usage: "[seconds]")]
    public void OnFindCommand(CCSPlayerController? player, CommandInfo commandInfo)
        => GuardAsync(commandInfo, static context =>
        {
            double? seconds = null;
            var arg = context.First;
            if (arg is not null)
            {
                if (!double.TryParse(arg, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
                    parsed <= 0d || parsed > 300d)
                {
                   context.Service.Reply(context.PlayerSlot, Phrases.Id.SearchSecondsUsage);
                    return Task.CompletedTask;
                }

                seconds = parsed;
            }

            return context.FindAsync(seconds);
        });

    /// <summary>Saves the currently selected result under a name.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_save", "Saves the current result under a name.")]
    [CommandHelper(minArgs: 1, usage: "<name>")]
    public void OnSaveCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(
        commandInfo,
        static context => context.Service.SaveLineup(context.PlayerSlot, context.First));

    /// <summary>Loads a saved line-up back into the player's session.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_load", "Loads a saved line-up.")]
    [CommandHelper(minArgs: 1, usage: "<name>")]
    public void OnLoadCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(
        commandInfo,
        static context => context.Service.LoadLineup(context.PlayerSlot, context.First, context.Service.Sessions.GetOrCreate(context.PlayerSlot).MapName));

    /// <summary>Lists the line-ups saved on this map.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_list", "Lists the line-ups saved on this map.")]
    [CommandHelper(minArgs: 0, usage: CommandSpec.NoArguments)]
    public void OnListCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(commandInfo, static context =>
    {
        var map = context.Service.Sessions.GetOrCreate(context.PlayerSlot).MapName;
        context.Service.ListLineups(context.PlayerSlot, map, context.Config.MaxResults);
    });

    /// <summary>Redraws the aim beam and the impact mark of the last result.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_draw", "Redraws the latest result in the world.")]
    [CommandHelper(minArgs: 0, usage: CommandSpec.NoArguments)]
    public void OnDrawCommand(CCSPlayerController? player, CommandInfo commandInfo)
        => Guard(commandInfo, static context => context.Service.RedrawLastResult(context.PlayerSlot));

    /// <summary>Picks one of the results of the last search.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_set", "Picks one of the results of the last search.")]
    [CommandHelper(minArgs: 1, usage: "<index>")]
    public void OnSetCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(commandInfo, static context =>
    {
        var slot = context.PlayerSlot;
        var session = context.Service.Sessions.GetOrCreate(slot);
        if (!int.TryParse(context.First, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            context.Service.Reply(slot, Phrases.Id.UnknownOption, context.First ?? string.Empty, Option.UsageFor("css_lf_set"));
            return;
        }

        var count = session.LastOutcome?.Results.Count ?? 0;
        if (index < 1 || index > count)
        {
            context.Service.Reply(slot, Phrases.Id.UnknownOption, index.ToString(CultureInfo.InvariantCulture), Option.UsageFor("css_lf_set"));
            return;
        }

        session.SelectedResultIndex = index - 1;
        context.Service.RedrawLastResult(slot);
    });

    /// <summary>Points the player's view at the best result, or the given one.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_aim", "Points your view at a result: the best one, or the given index.")]
    [CommandHelper(minArgs: 0, usage: "[index]")]
    public void OnAimCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(commandInfo, context =>
    {
        var slot = context.PlayerSlot;
        var session = context.Service.Sessions.GetOrCreate(slot);
        var raw = context.First;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chosen))
            {
                context.Service.Reply(slot, Phrases.Id.UnknownOption, raw, Option.UsageFor("css_lf_aim"));
                return;
            }

            var total = session.LastOutcome?.Results.Count ?? 0;
            if (chosen < 1 || chosen > total)
            {
                context.Service.Reply(slot, Phrases.Id.UnknownOption, chosen.ToString(CultureInfo.InvariantCulture), Option.UsageFor("css_lf_aim"));
                return;
            }

            session.SelectedResultIndex = chosen - 1;
            context.Service.RedrawLastResult(slot);
        }

        var solution = session.SelectedSolution;
        if (solution is null)
        {
            context.Service.Reply(slot, Phrases.Id.NothingToSave);
            return;
        }

        context.Service.AimAtSolution(slot, solution);
        context.Service.Reply(slot, Phrases.Id.Aimed, CommandParser.RoundAngle(solution.Yaw), CommandParser.RoundPlayerPitch(solution.Pitch));
    });

    /// <summary>Clears the player's throw point, landing zone and results.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_clear", "Clears the throw point, the landing zone and the results.")]
    [CommandHelper(minArgs: 0, usage: CommandSpec.NoArguments)]
    public void OnClearCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(commandInfo, static context =>
    {
        context.Service.Clear(context.PlayerSlot);
        context.Service.ClearVisuals();
    });

    /// <summary>Reloads <c>config/config.toml</c> without a server restart.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_reload", "Reloads config.toml.")]
    [CommandHelper(minArgs: 0, usage: CommandSpec.NoArguments)]
    public void OnReloadCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(commandInfo, static context =>
    {
        var reloaded = PluginConfig.Load(context.Root);
        context.Service.ApplyConfig(reloaded);
        context.Service.Reply(context.PlayerSlot, Phrases.Id.ConfigReloaded, reloaded.ToString());
    });

    /// <summary>Replays recorded calibration landings against the live map.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_replay", "Replays calibration landings; add a telemetry number to forensic-replay that capture tick by tick.")]
    [CommandHelper(minArgs: 0, usage: "[telemetryIndex]")]
    public void OnReplayCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(commandInfo, context =>
    {
        if (int.TryParse(context.First, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int telemetryIndex)
            && telemetryIndex > 0)
        {
            // Forensic mode: replay the recorded engine states tick by tick on the
            // live map so the first divergent tick pins down the trace mismatch.
            context.Service.ReplayTelemetryLiveMap(
                context.PlayerSlot,
                context.World,
                System.IO.Path.Combine(context.Root, "data", "telemetry"),
                telemetryIndex);
            return;
        }

        context.Service.ReplayCalibration(
            context.PlayerSlot,
            context.World,
            System.IO.Path.Combine(context.Root, "data", "calibration"));
    });

    /// <summary>
    /// Measures what the engine fills into a trace result, so the simulator reads
    /// the field the engine actually means rather than the one it assumed.
    /// </summary>
    /// <remarks>
    /// The managed <c>Trace</c> type marshals <c>CGameTrace</c> without interpreting
    /// it, so the engine owns the meaning of every field. This command sweeps a
    /// known hull over known ground and prints the raw values: on flat ground the
    /// swept-shape origin and the exact surface contact point differ by exactly the
    /// hull radius along the surface normal, so the printed separation settles
    /// which field is which. It is a diagnostic, it changes no state, and it is the
    /// only reason a geometry bug can be attributed to a cause instead of guessed.
    /// </remarks>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_probe", "Reports raw engine trace fields for the ground under your feet.")]
    [CommandHelper(minArgs: 0, usage: "[hullRadius]")]
    public void OnProbeCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        var services = _services;
        if (services is null)
        {
            commandInfo.ReplyToCommand("[LineupFinder] Unavailable: " +
                (string.IsNullOrEmpty(_loadError) ? "the plugin is not loaded" : _loadError));
            return;
        }

        var slot = commandInfo.CallingPlayer?.Slot ?? -1;
        if (slot < 0)
        {
            commandInfo.ReplyToCommand("[LineupFinder] This command runs on a player, not the server console.");
            return;
        }

        var probe = TraceBackendFactory.AsProbe(services.Trace);
        if (probe is null)
        {
            commandInfo.ReplyToCommand($"[LineupFinder] trace backend '{services.Trace.Name}' cannot report raw trace fields.");
            return;
        }

        var pawn = EnginePlayers.Pawn(slot);
        if (pawn is null)
        {
            commandInfo.ReplyToCommand("[LineupFinder] No live pawn.");
            return;
        }

        var radius = 2f;
        if (commandInfo.ArgCount > 1 && float.TryParse(commandInfo.ArgByIndex(1), out var parsed) && parsed > 0f)
        {
            radius = parsed;
        }

        var origin = pawn.AbsOrigin!;
        var feet = new Vec3(origin.X, origin.Y, origin.Z);

        // Three sweeps over the same ground, each isolating one field: straight
        // down (normal is up, so every horizontal field is a pure noise floor),
        // then down-and-sideways at a shallow angle where the surface point and the
        // swept origin separate horizontally as well as vertically. Comparing the
        // two tells the two apart on any surface, not just a flat one.
        var probes = new (string Label, Vec3 From, Vec3 To)[]
        {
            ("down", feet + new Vec3(0f, 0f, 8f), feet + new Vec3(0f, 0f, -64f)),
            ("shallow", feet + new Vec3(0f, 0f, 8f), feet + new Vec3(32f, 0f, -64f)),
            ("line", feet + new Vec3(0f, 0f, 8f), feet + new Vec3(0f, 0f, -64f)),
        };

        foreach (var (label, from, to) in probes)
        {
            var hull = label == "line" ? 0f : radius;
            var line = probe.DescribeTrace(from, to, hull);
            Logger.LogInformation("CS2LineupFinder: {Text}", line);
            commandInfo.ReplyToCommand(line);
        }
    }

    /// <summary>
    /// Forwards chat aliases such as <c>!lf_zone circle 5</c> to their console
    /// commands over the <c>player_chat</c> game event, which still fires when the
    /// upstream chat trigger hook does not.
    /// </summary>
    /// <remarks>
    /// The message is re-issued as a client command on the game thread, so it flows
    /// through the engine's own command dispatch exactly as if the player had typed
    /// <c>css_lf_zone circle 5</c> - no trigger signatures involved. Messages that
    /// are not one of this plugin's aliases are ignored, and the whole bridge is
    /// switchable through <c>general.chatBridge</c> in config.toml so it can be
    /// turned off once CounterStrikeSharp's own chat pipeline works again.
    /// </remarks>
    private HookResult OnChatBridge(EventPlayerChat @event, GameEventInfo info)
    {
        var services = _services;
        if (services is null || !services.Commands.Config.ChatBridge)
        {
            return HookResult.Continue;
        }

        var text = @event.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return HookResult.Continue;
        }

        var trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] is not ('!' or '/' or '.'))
        {
            return HookResult.Continue;
        }

        // "!lf_zone circle 5" -> "lf_zone"; only aliases this plugin owns cross the
        // bridge, everything else stays with the engine and other plugins.
        var name = Commands.CommandUsage.CommandNameFromChat(trimmed);
        if (!CommandSpec.IsChatAlias(name))
        {
            return HookResult.Continue;
        }

        var userId = @event.Userid;
        var command = "css_" + trimmed[1..];
        services.Dispatcher.Post(() =>
        {
            var player = Utilities.GetPlayerFromUserid(userId);
            player?.ExecuteClientCommandFromServer(command);
        });

        return HookResult.Continue;
    }

    /// <summary>Builds the menu a player sees, so a click always reflects live state.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    private void OpenMenu(int playerSlot)
    {
        var services = _services;
        var player = EnginePlayers.Controller(playerSlot);
        if (services is null || player is null)
        {
            return;
        }

        var config = services.Commands.Config;
        var language = config.Language;
        var session = services.Sessions.GetOrCreate(playerSlot);
        var menu = new ChatMenu(Phrases.Get(language, Phrases.Id.MenuTitle, Phrases.DescribeSession(session, language)));

        // PostSelectAction.Nothing keeps the menu on screen after a click, so a
        // player can set three things in a row without retyping !lf_menu.
        menu.PostSelectAction = PostSelectAction.Nothing;
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuMarkStand), (p, _) => MarkFromMenu(p, ThrowMode.Stand));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuMarkCrouch), (p, _) => MarkFromMenu(p, ThrowMode.Crouch));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuMarkJump), (p, _) => MarkFromMenu(p, ThrowMode.Jump));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuGrenade, Option.Token(session.GrenadeType)), (p, _) => CycleGrenade(p));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuThrow, Option.Token(session.ThrowMode)), (p, _) => CycleThrowMode(p));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuButton, Option.Token(session.Button)), (p, _) => CycleButton(p));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuFind), (p, _) => FindFromMenu(p));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuSet), (p, _) => services.Commands.Reply(p.Slot, Phrases.Id.MenuSet, Option.Token(session.GrenadeType)), disabled: true);
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuSave), (p, _) => services.Commands.Reply(p.Slot, Phrases.Id.NothingToSave), disabled: true);
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuList), (p, _) => ListFromMenu(p));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuDraw), (p, _) => services.Commands.RedrawLastResult(p.Slot));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuClear), (p, _) => ClearFromMenu(p));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuReload), (p, _) => ReloadFromMenu(p));

        if (config.VerboseMenu)
        {
            AddDiagnostics(menu, services, config, language);
        }

        MenuManager.OpenChatMenu(player, menu);
    }

    private void MarkFromMenu(CCSPlayerController player, ThrowMode mode)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        var slot = player.Slot;
        services.Sessions.GetOrCreate(slot).ThrowMode = mode;

        var origin = services.Movement.GetThrowOrigin(slot, mode);
        if (origin is null)
        {
            services.Commands.Reply(slot, Phrases.Id.NoPawn);
            return;
        }

        services.Commands.MarkStart(slot, origin.Feet, origin.Eyes, origin.Velocity);
    }

    /// <summary>Adds the settings, backend and path lines the operator asked to see.</summary>
    /// <param name="menu">Menu being built.</param>
    /// <param name="services">Live service graph.</param>
    /// <param name="config">Configuration in effect.</param>
    /// <param name="language">Language the entries are rendered in.</param>
    private static void AddDiagnostics(ChatMenu menu, ServiceGraph services, PluginConfig config, string language)
    {
        // Informational entries. They are disabled so a click cannot be mistaken for
        // an action, which is also why they carry no handler of their own.
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.SearchSettings, config.MaxCandidates, config.SolveTimeoutSeconds, config.MaxResults), static (_, _) => { }, disabled: true);
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MapDirectory, services.Lineups.GetMapDirectory(Server.MapName)), static (_, _) => { }, disabled: true);

        if (services.Simulator.UsingStub)
        {
            menu.AddMenuOption(Phrases.Get(language, Phrases.Id.StubSimulator), static (_, _) => { }, disabled: true);
        }

        if (!GameWorldGeometry.CanTrace(services.World))
        {
            menu.AddMenuOption(Phrases.Get(language, Phrases.Id.NoTraceBackend), static (_, _) => { }, disabled: true);
        }
    }

    private void ClearFromMenu(CCSPlayerController player)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        services.Commands.Clear(player.Slot);
        services.Commands.ClearVisuals();
    }

    private void CycleGrenade(CCSPlayerController player)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        var session = services.Sessions.GetOrCreate(player.Slot);
        session.GrenadeType = session.GrenadeType switch
        {
            GrenadeType.Smoke => GrenadeType.Flash,
            GrenadeType.Flash => GrenadeType.Molotov,
            GrenadeType.Molotov => GrenadeType.He,
            _ => GrenadeType.Smoke,
        };

        services.Commands.Reply(player.Slot, Phrases.Id.GrenadeSet, Option.Token(session.GrenadeType));
    }

    private void CycleThrowMode(CCSPlayerController player)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        var session = services.Sessions.GetOrCreate(player.Slot);
        session.ThrowMode = session.ThrowMode switch
        {
            ThrowMode.Stand => ThrowMode.Crouch,
            ThrowMode.Crouch => ThrowMode.Jump,
            _ => ThrowMode.Stand,
        };

        services.Commands.Reply(player.Slot, Phrases.Id.ThrowModeSet, Option.Token(session.ThrowMode));
    }

    private void CycleButton(CCSPlayerController player)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        var session = services.Sessions.GetOrCreate(player.Slot);
        session.Button = session.Button switch
        {
            ThrowButton.Primary => ThrowButton.Secondary,
            ThrowButton.Secondary => ThrowButton.Both,
            _ => ThrowButton.Primary,
        };

        services.Commands.Reply(player.Slot, Phrases.Id.ButtonSet, Option.Token(session.Button));
    }

    private void FindFromMenu(CCSPlayerController player)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        var slot = player.Slot;
        RefreshThrowOriginForSearch(services, slot);
        var session = services.Sessions.GetOrCreate(slot);
        Logger.LogInformation(
            "CS2LineupFinder: search pose: mode={Mode} carried=({X:0.0},{Y:0.0},{Z:0.0}) measuredJumpZ={Measured}",
            session.ThrowMode,
            session.ThrowVelocity.X,
            session.ThrowVelocity.Y,
            session.ThrowVelocity.Z,
            session.MeasuredJumpCarryZ?.ToString("0.0") ?? "none");
        _ = RunGuardedAsync(
            slot,
            () => services.Commands.FindAsync(slot, services.World, services.Cancellations.TokenFor(slot)));
    }

    /// <summary>
    /// Re-reads the thrower pose right before a menu-triggered search, exactly
    /// like the chat command path does, so the solve always starts from the pose
    /// the player is holding now rather than the pose held when they marked.
    /// </summary>
    private static void RefreshThrowOriginForSearch(ServiceGraph services, int slot)
    {
        var session = services.Sessions.GetOrCreate(slot);
        if (session.ThrowPoint is null)
        {
            return;
        }

        var mode = session.ThrowMode;
        var origin = services.Movement.GetThrowOrigin(slot, mode);
        if (origin is null)
        {
            return;
        }

        origin = ThrowOriginRules.ForSearch(
            origin,
            session.ThrowVelocity,
            mode,
            session.MeasuredJumpCarryZ,
            (float)services.Commands.Config.JumpCarryVelocity,
            session.MeasuredJumpReleaseRise,
            (float)services.Commands.Config.JumpReleaseRise);

        session.ThrowPoint = origin.Feet;
        session.EyePoint = origin.Eyes;
        session.ThrowVelocity = origin.Velocity;
    }

    /// <summary>
    /// Decides whether the stored launch capture may be paired with the pending
    /// suggestion. Extracted as a pure function so the pairing rules are unit
    /// testable: same suggestion number (a second find replaces the first, so a
    /// grenade still airborne from the first must never pair with it), and the
    /// detonation must arrive within one long smoke flight of the capture
    /// (telemetry-001's longest flight was 5.78 s, so the window is 8 s).
    /// </summary>
    internal static bool IsLaunchCaptureUsable(
        long captureTickMs,
        long captureSuggestionNumber,
        long nowMs,
        PendingSuggestion pending)
    {
        return captureTickMs != 0
            && captureSuggestionNumber != 0
            && captureSuggestionNumber == pending.SuggestionNumber
            && nowMs - captureTickMs <= 8_000;
    }

    private void ListFromMenu(CCSPlayerController player)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        var slot = player.Slot;
        services.Commands.ListLineups(slot, services.Sessions.GetOrCreate(slot).MapName, services.Commands.Config.MaxResults);
    }

    private void ReloadFromMenu(CCSPlayerController player)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        services.Apply(PluginConfig.Load(services.Root));
        services.Commands.Reply(player.Slot, Phrases.Id.ConfigReloaded, services.Commands.Config.ToString());
    }

    /// <summary>Drains work queued for the game thread; runs once per tick.</summary>
    private void OnTick()
    {
        // The 2026-10-15 telemetry captures caught OnTick firing twice per engine
        // tick (every recorded state appeared exactly twice, stretching the time
        // axis 2x and halving the least-squares gravity fit to 160). Guard every
        // entry with the engine tick counter so duplicate firings are inert.
        int engineTick = NativeAPI.GetTickCount();
        if (engineTick == _lastEngineTick)
        {
            return;
        }

        _lastEngineTick = engineTick;

        try
        {
            _services?.Commands.TickSearches();
            _services?.Beams.Tick();
            _services?.Visuals.Step();
            _services?.Dispatcher.Drain();
            TickTelemetry(engineTick);
            ResolveLaunchCarryProbe();

            int delay = _startupReplayDelayTicks;
            if (delay > 0 && System.Threading.Interlocked.Decrement(ref _startupReplayDelayTicks) == 0)
            {
                RunStartupReplay();
            }

            int probeDelay = _pawnProbeDelayTicks;
            if (probeDelay > 0 && System.Threading.Interlocked.Decrement(ref _pawnProbeDelayTicks) == 0)
            {
                RunPawnProbe();
            }

        }
        catch (Exception ex)
        {
            Logger.LogWarning("CS2LineupFinder: game thread work failed: {Message}", ex.Message);
        }
    }

    private void Guard(CommandInfo commandInfo, Action<LfCommandContext> body)
    {
        var context = Prepare(commandInfo);
        if (context is null)
        {
            return;
        }

        try
        {
            body(context);
        }
        catch (Exception ex)
        {
            Report(context.PlayerSlot, commandInfo, ex);
        }
    }

    private void GuardAsync(CommandInfo commandInfo, Func<LfCommandContext, Task> body)
    {
        var context = Prepare(commandInfo);
        if (context is null)
        {
            return;
        }

        _ = RunGuardedAsync(context.PlayerSlot, () => body(context));
    }

    private async Task RunGuardedAsync(int playerSlot, Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(playerSlot, null, ex);
        }
    }

    /// <summary>
    /// Resolves the calling player and builds a context, or reports why it could not.
    /// </summary>
    /// <param name="commandInfo">Command being handled.</param>
    /// <returns>The context, or <see langword="null"/> when the command cannot run.</returns>
    private LfCommandContext? Prepare(CommandInfo commandInfo)
    {
        var services = _services;
        if (services is null)
        {
            var reason = string.IsNullOrEmpty(_loadError) ? "the plugin is not loaded" : _loadError;
            commandInfo.ReplyToCommand("[LineupFinder] Unavailable: " + reason);
            return null;
        }

        var slot = commandInfo.CallingPlayer?.Slot ?? -1;
        if (slot < 0)
        {
            commandInfo.ReplyToCommand("[LineupFinder] This command runs on a player, not the server console.");
            return null;
        }

        Refresh(services, slot);
        var context = new LfCommandContext(
            services.Commands,
            services.Movement,
            services.World,
            services.Cancellations,
            services.Apply,
            services.Root,
            slot,
            Args(commandInfo));

        return CheckArguments(commandInfo, context) ? context : null;
    }

    /// <summary>
    /// Reports the usage line of a command that was called without its required
    /// argument. CounterStrikeSharp already enforces <c>MinArgs</c> and prints the
    /// same text, but it does so before the handler runs, so this is the belt to that
    /// pair of braces: the command layer must not have to guess whether a missing
    /// argument is an empty name or a missing one.
    /// </summary>
    /// <param name="commandInfo">Command being handled.</param>
    /// <param name="context">Context built for it.</param>
    /// <returns><see langword="true"/> when the handler may run.</returns>
    private bool CheckArguments(CommandInfo commandInfo, LfCommandContext context)
    {
        var spec = CommandSpec.Find(commandInfo.ArgByIndex(0));
        if (spec is null || spec.Value.TakesNoArguments || context.Arguments.Count > 0)
        {
            return true;
        }

        commandInfo.ReplyToCommand(Phrases.Prefixed(
            Config.Language,
            Phrases.Id.Usage,
            Option.UsageFor(spec.Value.Console)));
        return false;
    }

    /// <summary>Keeps the session's identity fields current before a command reads them.</summary>
    /// <param name="services">Live service graph.</param>
    /// <param name="playerSlot">Engine slot of the player.</param>
    private static void Refresh(ServiceGraph services, int playerSlot)
    {
        var live = EnginePlayers.Name(playerSlot);
        var map = Server.MapName;
        services.Sessions.GetOrCreate(playerSlot, session =>
        {
            if (!string.IsNullOrEmpty(live))
            {
                session.PlayerName = live;
            }

            if (!string.IsNullOrEmpty(map))
            {
                session.MapName = map;
            }
        });
    }

    private void Report(int playerSlot, CommandInfo? commandInfo, Exception ex)
    {
        Logger.LogError(ex, "CS2LineupFinder: command failed for slot {Slot}.", playerSlot);
        var message = Phrases.Prefixed(
            Config.Language,
            Phrases.Id.SearchFailed,
            ex.GetType().Name,
            ex.Message);

        if (commandInfo is not null)
        {
            commandInfo.ReplyToCommand(message);
            return;
        }

        _services?.Report.Reply(playerSlot, message);
    }

    private static string[] Args(CommandInfo commandInfo)
    {
        var count = Math.Max(commandInfo.ArgCount - 1, 0);
        var arguments = new string[count];
        for (var i = 0; i < count; i++)
        {
            arguments[i] = commandInfo.ArgByIndex(i + 1);
        }

        return arguments;
    }

    /// <summary>
    /// Everything the plugin builds at load time, in one object, so a command can
    /// tell at a glance whether the plugin is running and what it is running with.
    /// </summary>
    private sealed class ServiceGraph
    {
        private readonly LineupFinderPlugin _plugin;

        internal ServiceGraph(LineupFinderPlugin plugin, string root)
        {
            _plugin = plugin;
            Root = root;
        Dispatcher = new GameThreadDispatcher { OnError = ex => plugin.Logger.LogWarning("CS2LineupFinder: queued work failed: {Message}", ex.Message) };
        Report = new MessageSink(plugin, Dispatcher);
            Trace = TraceBackendFactory.Create(Report);
            Tracer = new GameWorldTracer(Trace, () => plugin.Config)
            {
                Log = text => plugin.Logger.LogInformation("CS2LineupFinder: {Text}", text),
            };
            World = new GameWorldGeometry(Tracer) { Config = () => plugin.Config };
            Beams = new BeamRenderer(Dispatcher, () => plugin.Config, Report);
            Visuals = new EngineVisuals(Dispatcher, Beams, () => plugin.Config);
            Sessions = new SessionStore();
            Lineups = LineupRepository.ForPluginRoot(root);
            Landings = new LandingCalibrator(Path.Combine(root, "data", "calibration"));
            Simulator = new SimulatorBridge(() => TimeSpan.FromSeconds(plugin.Config.SolveTimeoutSeconds))
            {
                Config = () => plugin.Config,
            };
            Cancellations = new CancellationRegistry();
            Movement = new GameMovementProvider(Tracer, () => plugin.Config);
            Commands = new CommandService(
                Sessions,
                Lineups,
                Simulator,
                Report,
                Visuals,
                new SystemClock(),
                () => Server.MapName,
                () => plugin.Config,
                config =>
                {
                    plugin.Config = config;
                    return config;
                },
                AimPlayer);
        }

        internal string Root { get; }

        internal MessageSink Report { get; }

        internal GameThreadDispatcher Dispatcher { get; }

        internal ITraceBackend Trace { get; }

        internal GameWorldTracer Tracer { get; }

        internal GameWorldGeometry World { get; }

        internal BeamRenderer Beams { get; }

        internal EngineVisuals Visuals { get; }

        internal SessionStore Sessions { get; }

        internal LineupRepository Lineups { get; }

        internal LandingCalibrator Landings { get; }

        internal SimulatorBridge Simulator { get; }

        internal CancellationRegistry Cancellations { get; }

        internal GameMovementProvider Movement { get; }
 
        /// <summary>
        /// Snaps a player's view onto the given contract angles. Runs through the
        /// dispatcher so a call from a tick slice or a queued reply still lands on
        /// the game thread, which owns every player entity.
        /// </summary>
        /// <remarks>
        /// The turn is executed by the client itself via <c>setang</c>. CS2 makes the
        /// client authoritative over its own camera: it re-derives its view from the
        /// mouse every frame, so nothing the server writes into the pawn's view
        /// fields (m_angEyeAngles, v_angle, m_ServerViewAngleChanges) survives long
        /// enough to be seen, and Teleport rotates the whole pawn - which dragged the
        /// eye offset to ground level and left players crawling. A client command is
        /// the one channel that turns the real camera. <c>setang</c> is marked
        /// cheat-protected, so the server runs with <c>sv_cheats 1</c>; on this
        /// offline practice server that is acceptable. The sign flip is the single
        /// mirror between the plugin's positive-up pitch convention and the engine's
        /// positive-down one. Argument order matters and is easy to get wrong:
        /// <c>setang</c> takes pitch first, yaw second. Swapping them puts the yaw
        /// into the pitch slot, which the engine clamps to 89 degrees, so the camera
        /// ends up staring straight up no matter what the solver asked for.
        /// </remarks>
        internal bool AimPlayer(int playerSlot, float yawDegrees, float pitchDegrees)
        {
            var player = EnginePlayers.Controller(playerSlot);
            if (player is null || !player.IsValid)
            {
                return false;
            }
 
            Dispatcher.Post(() =>
            {
                player.ExecuteClientCommandFromServer(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "setang {0:0.####} {1:0.####}",
                        -pitchDegrees,
                        yawDegrees));
            });
            return true;
        }
 
        internal CommandService Commands { get; }

        /// <summary>One line describing which simulator and solver are in use.</summary>
        internal string CoreSummary { get; private set; } = "built-in stubs";

        /// <summary>Wires the catalogue and looks for the real Core and Math assemblies.</summary>
        internal void Build()
        {
            CoreSummary = CoreLoader.Install(Root, Simulator, Report);
            Commands.MapEnabled = MapCatalog.Load(Root).IsEnabled(Server.MapName);
        }

        /// <summary>Publishes a reloaded configuration to everything that reads it.</summary>
        /// <param name="config">Configuration read from disk.</param>
        /// <returns>The configuration that is now in effect.</returns>
        internal PluginConfig Apply(PluginConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            _plugin.Config = config;
            if (!Simulator.UsingStub)
            {
                CoreLoader.ApplyPhysicsOverrides(Simulator.Simulator, config, Report);
            }

            return config;
        }

        /// <summary>Releases entities and parked work at unload time.</summary>
        internal void Shutdown()
        {
            Cancellations.CancelAll();
            Dispatcher.Post(Beams.Clear);
            Dispatcher.Shutdown();
            Beams.Dispose();
            Visuals.Dispose();
            Cancellations.Dispose();
        }
    }

    /// <summary>Routes the command layer's chat and log output into the running server.</summary>
    private sealed class MessageSink : IPluginMessages
    {
        private readonly LineupFinderPlugin _plugin;
        private readonly GameThreadDispatcher? _dispatcher;

        internal MessageSink(LineupFinderPlugin plugin, GameThreadDispatcher? dispatcher)
        {
            _plugin = plugin;
            _dispatcher = dispatcher;
        }

        /// <inheritdoc />
        public void Reply(int playerSlot, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // Native calls must land on the game thread. An async continuation after
            // ConfigureAwait(false) resumes on a worker thread, so hop back when needed.
            if (_dispatcher is { } dispatcher && !dispatcher.IsGameThread)
            {
                dispatcher.Post(() => Reply(playerSlot, text));
                return;
            }

            var player = EnginePlayers.Controller(playerSlot);
            if (player is not null)
            {
                player.PrintToChat(text);
                return;
            }

            // No player behind the slot: the console has to hear it instead.
            _plugin.Logger.LogInformation("CS2LineupFinder (slot {Slot}): {Text}", playerSlot, Strip(text));
        }

        /// <inheritdoc />
        public void Log(string text) => _plugin.Logger.LogInformation("{Text}", Strip(text));

        /// <inheritdoc />
        public void LogWarning(string text) => _plugin.Logger.LogWarning("{Text}", Strip(text));

        /// <summary>Console output does not need the chat colour codes the messages carry.</summary>
        private static string Strip(string text) => text.Replace('\u0001', ' ').Replace('\u0003', ' ').Trim();
    }
}
