// <copyright file="CommandService.Replay.cs" company="CS2LineupFinder">
// The css_lf_replay diagnostic: replays recorded calibration landings on the
// live map geometry so the simulator is judged against the real world, not an
// analytic plane.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Calibration;
using CS2LineupFinder.Plugin.Diagnostics;
using CS2LineupFinder.Plugin.Game;
using CS2LineupFinder.Plugin.State;

namespace CS2LineupFinder.Plugin.Commands;

/// <summary>Command-service partial hosting the calibration replay.</summary>
public sealed partial class CommandService
{
    /// <summary>
    /// Replays every recorded sample recorded on the current map: rebuilds the
    /// original launch from the sample's aim, simulates it against the live map
    /// and reports the planar distance to where the game actually detonated.
    /// </summary>
    public void ReplayCalibration(int playerSlot, IWorldGeometry world, string calibrationDirectory)
    {
        ArgumentNullException.ThrowIfNull(world);

        string mapName = _currentMap();
        var samples = LandingReplayFile.LoadAll(calibrationDirectory);
        int replayed = 0;
        double total = 0.0;

        foreach (LandingSampleDto sample in samples)
        {
            if (!string.Equals(sample.Map, mapName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Vec3 impact = SimulateSample(sample, world);
            float error = HorizontalPlanar(impact, sample.ActualImpact.ToVec3());
            replayed++;
            total += error;
            Reply(
                playerSlot,
                Phrases.Id.ReplayResult,
                sample.Index,
                error,
                Format(sample.PredictedImpact),
                Format(sample.ActualImpact));
        }

        Reply(playerSlot, Phrases.Id.ReplaySummary, replayed, replayed > 0 ? total / replayed : 0.0);
    }

    /// <summary>
    /// Log variant of the replay: runs on map start with the live world geometry
    /// and writes one line per sample plus a summary through the sink.
    /// </summary>
    public void ReplayCalibrationToLog(
        string calibrationDirectory,
        string mapName,
        IWorldGeometry world,
        Action<string> sink)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(sink);

        var samples = LandingReplayFile.LoadAll(calibrationDirectory);
        int replayed = 0;
        double total = 0.0;
        foreach (LandingSampleDto sample in samples)
        {
            if (!string.Equals(sample.Map, mapName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                SampleSimulation simulation = SimulateSampleWithResult(sample, world);
                Vec3 impact = simulation.Result.Impact.Position;
                float error = HorizontalPlanar(impact, sample.ActualImpact.ToVec3());
                Vec3 delta = impact - sample.ActualImpact.ToVec3();
                replayed++;
                total += error;
                sink(string.Create(
                    CultureInfo.InvariantCulture,
                    $"replay #{sample.Index} (yaw {sample.AimYaw:0.#} pitch {sample.AimPitch:0.#}): error {error:0.#}u " +
                    $"pred ({impact.X:0.#},{impact.Y:0.#},{impact.Z:0.#}) " +
                    $"delta ({delta.X:0.#},{delta.Y:0.#},{delta.Z:0.#})"));

                // A large replay error is a trajectory question, not a score:
                // print the launch state and every contact so the first surface
                // the simulator disagrees on is visible in the log. This is the
                // evidence the startup replay previously lacked.
                if (error > 50f)
                {
                    ThrowParams launch = simulation.Parameters;
                    sink(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  launch ({launch.Origin.X:0.####},{launch.Origin.Y:0.####},{launch.Origin.Z:0.####}) " +
                        $"v ({launch.Velocity.X:0.####},{launch.Velocity.Y:0.####},{launch.Velocity.Z:0.####})"));
                    DumpContacts(sink, _simulator.Simulator);
                    for (var i = 0; i < simulation.Result.Points.Count; i++)
                    {
                        TrajectoryPoint point = simulation.Result.Points[i];
                        bool bounce = i == 0 || point.BounceCount != simulation.Result.Points[i - 1].BounceCount;
                        if (bounce)
                        {
                            sink(string.Create(
                                CultureInfo.InvariantCulture,
                                $"  tick {i,3} t={point.Time:0.0000} p ({point.Position.X:0.####},{point.Position.Y:0.####},{point.Position.Z:0.####}) " +
                                $"v ({point.Velocity.X:0.####},{point.Velocity.Y:0.####},{point.Velocity.Z:0.####}) b{point.BounceCount}"));
                        }
                    }

                    sink(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  bounces {simulation.Result.BounceCount} flight {simulation.Result.FlightTime:0.0000}s"));
                }
            }
            catch (Exception)
            {
                // One bad sample must never stop the rest.
            }
        }

        sink(string.Create(
            CultureInfo.InvariantCulture,
            $"replay done: {replayed} sample(s), mean error {(replayed > 0 ? total / replayed : 0.0):0.#}u"));
    }

    private Vec3 SimulateSample(LandingSampleDto sample, IWorldGeometry world)
        => SimulateSampleWithResult(sample, world).Result.Impact.Position;

    private SampleSimulation SimulateSampleWithResult(LandingSampleDto sample, IWorldGeometry world)
        => SimulateSampleWithResult(sample, world, _simulator.Simulator);

    private SampleSimulation SimulateSampleWithResult(
        LandingSampleDto sample,
        IWorldGeometry world,
        ITrajectorySimulator simulator)
    {
        // NEW samples carry the projectile's own launch state, read straight off
        // the spawned smokegrenade_projectile entity the tick after release.
        // This is the only reconstruction that contains the thrower's inherited
        // velocity: telemetry-002 row 0 measured 678.63 u/s against the 675
        // full-throw constant. The 1015 palace sample (stationary, 675.4 u/s)
        // originally replayed 132.8u under the synthesised launch, but that was
        // the R4 q=2 embedded scale (now gated on q=1), not a missing thrower
        // velocity; its live replay is 1.8u. The measured launch state is still
        // required for genuinely moving throws.
        if (sample.LaunchPosition is { } launchPos
            && sample.LaunchVelocity is { } launchVel
            && (launchVel.X != 0 || launchVel.Y != 0 || launchVel.Z != 0))
        {
            var measured = new ThrowParams(
                launchPos.ToVec3(),
                launchVel.ToVec3(),
                GrenadeType.Smoke,
                1f,
                64)
            { IgnoreEntityIndex = -1 };
            return new SampleSimulation(simulator.Simulate(measured, world), measured);
        }

        Vec3 aim = AngleDirection(sample.AimYaw, sample.AimPitch);
        Vec3 unbiased = aim.Normalized();

        // The engine adds a +10 degree up-bias to the aim before launching;
        // Core's ThrowVelocityCalculator.ApplyAimBias does the same. The plugin
        // does not compile against Core, so the rotation is repeated here (see
        // Core.ThrowVelocityCalculator.ApplyAimBias for the authoritative copy).
        Vec3 forward = RotateUpward(unbiased, (float)Config.PhysicsAimBiasDegrees);
        float speed = (float)Config.PhysicsFullThrowSpeed;
        Vec3 velocity = forward * speed;

        // Samples recorded with the release position present must replay through
        // the exact point the successful simulation released from. Rebuilding it
        // from feet + eye height + aim offset loses the small lateral shift the
        // biased direction introduces and reintroduces aim-pitch coupling into
        // what is supposed to be a pure physics measurement.
        Vec3 release = sample.ReleasePosition is { X: not 0, Y: not 0 }
            ? sample.ReleasePosition.ToVec3()
            : sample.Origin.ToVec3()
                + (unbiased * 16f)
                + new Vec3(0f, 0f, sample.EyeHeight);

        var throwParams = new ThrowParams(
            release,
            velocity,
            GrenadeType.Smoke,
            1f,
            64)
        { IgnoreEntityIndex = -1 };

        return new SampleSimulation(simulator.Simulate(throwParams, world), throwParams);
    }

    /// <summary>
    /// Prints the simulator's own contact log. The Core type is loaded by
    /// reflection, so the fields are read by name and never referenced at
    /// compile time; a Core without the log simply produces no lines.
    /// </summary>
    private static void DumpContacts(Action<string> sink, ITrajectorySimulator simulator)
    {
        object? value = simulator.GetType().GetProperty("LastContacts")?.GetValue(simulator);
        if (value is not System.Collections.IEnumerable contacts)
        {
            return;
        }

        var index = 0;
        foreach (object contact in contacts)
        {
            object? Get(string name) => contact.GetType().GetProperty(name)?.GetValue(contact);
            sink(string.Create(
                CultureInfo.InvariantCulture,
                $"  contact {index++}: p {FormatVec(Get("Position"))} n {FormatVec(Get("Normal"))} " +
                $"b{Get("BounceCount")} push={Get("IsPush")} embedded={Get("IsEmbedded")} " +
                $"frac={Get("Fraction")} class={Get("ClassName")} contents=0x{Convert.ToUInt64(Get("ContentsRaw") ?? 0UL):X}"));
        }
    }

    private static string FormatVec(object? value)
    {
        if (value is null)
        {
            return "?";
        }

        object? Get(string name) => value.GetType().GetProperty(name)?.GetValue(value);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"({Convert.ToSingle(Get("X"), CultureInfo.InvariantCulture):0.####}," +
            $"{Convert.ToSingle(Get("Y"), CultureInfo.InvariantCulture):0.####}," +
            $"{Convert.ToSingle(Get("Z"), CultureInfo.InvariantCulture):0.####})");
    }

    private readonly record struct SampleSimulation(TrajectoryResult Result, ThrowParams Parameters);

    /// <summary>
    /// Live-map forensic replay: rebuilds the recorded engine launch state from
    /// a telemetry CSV (row 0 IS the launch state - the mapping the offline
    /// forensic tests proved) and compares every simulated tick against the
    /// recording on the geometry the running server traces. The first divergent
    /// tick pins down exactly where the live trace path disagrees with the
    /// engine physics the CSV captured. Runs on an unmetered tracer so the
    /// search sweep budget is untouched.
    /// </summary>
    public void ReplayTelemetryLiveMap(int playerSlot, IWorldGeometry world, string telemetryDirectory, int? index)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_simulator.UsingStub)
        {
            Reply(playerSlot, Phrases.Id.ReplayTelemetryNotAvailable);
            return;
        }

        // Unmetered: this diagnostic runs hundreds of thousands of sweeps and must
        // not consume (or trip) the per-search budget the solver relies on.
        var unmetered = new GameWorldGeometry(new UnmeteredTraceBackend(
            TraceBackendFactory.Current ?? throw new InvalidOperationException("no trace backend")));
        var shim = new GrenadeSimulatorShim(_simulator.Simulator);

        IEnumerable<string> files;
        if (index is { } chosen)
        {
            files = new[] { string.Create(CultureInfo.InvariantCulture, $"telemetry-{chosen:D3}.csv") };
        }
        else
        {
            var names = new List<string>();
            try
            {
                foreach (string path in Directory.EnumerateFiles(telemetryDirectory, "telemetry-*.csv"))
                {
                    names.Add(Path.GetFileName(path));
                }
            }
            catch (Exception)
            {
                // Enumerate failures behave like an empty directory.
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            files = names;
        }

        int replayed = 0;
        foreach (string file in files)
        {
            string csvPath = System.IO.Path.Combine(telemetryDirectory, file);
            LiveMapReplayResult? result;
            try
            {
                result = LiveMapTelemetryReplay.Run(csvPath, shim, unmetered);
            }
            catch (Exception ex)
            {
                Reply(playerSlot, Phrases.Id.ReplayTelemetryFileFailed, file, ex.Message);
                continue;
            }

            if (result is null)
            {
                continue;
            }

            replayed++;
            Reply(playerSlot, Phrases.Id.ReplayTelemetryDescribe, LiveMapTelemetryReplay.Describe(result, file));
            foreach (string line in LiveMapTelemetryReplay.DetailLines(result))
            {
                Reply(playerSlot, Phrases.Id.ReplayTelemetryDescribe, line);
            }
        }

        if (replayed == 0)
        {
            Reply(playerSlot, Phrases.Id.ReplayTelemetryNoneFound, telemetryDirectory);
        }
    }

    /// <summary>
    /// Log variant of <see cref="ReplayTelemetryLiveMap"/>: the same tick-by-tick
    /// forensic replay, run unattended on map start and written to the plugin log
    /// through the sink. One result line per capture plus detail lines around the
    /// first divergence, so the fidelity evidence is captured without a player
    /// having to run a command.
    /// </summary>
    public void ReplayTelemetryToLog(
        string telemetryDirectory,
        IWorldGeometry world,
        Action<string> sink)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(sink);

        if (_simulator.UsingStub)
        {
            sink("telemetry replay: Core simulator not loaded; skipped.");
            return;
        }

        var unmetered = new GameWorldGeometry(new UnmeteredTraceBackend(
            TraceBackendFactory.Current ?? throw new InvalidOperationException("no trace backend")));
        var shim = new GrenadeSimulatorShim(_simulator.Simulator);

        var probe = TraceBackendFactory.AsProbe(TraceBackendFactory.Current!);
        if (TraceBackendFactory.Current is ReflectiveTraceBackend reflective)
        {
            sink("trace api: " + reflective.DescribeApiSignature());
        }
        var names = new List<string>();
        try
        {
            foreach (string path in Directory.EnumerateFiles(telemetryDirectory, "telemetry-*.csv"))
            {
                names.Add(Path.GetFileName(path));
            }
        }
        catch (Exception)
        {
            // Enumerate failures behave like an empty directory.
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        int replayed = 0;
        foreach (string file in names)
        {
            string csvPath = System.IO.Path.Combine(telemetryDirectory, file);
            LiveMapReplayResult? result;
            try
            {
                result = LiveMapTelemetryReplay.Run(csvPath, shim, unmetered);
            }
            catch (Exception ex)
            {
                sink(string.Create(CultureInfo.InvariantCulture, $"telemetry replay {file} failed: {ex.Message}"));
                continue;
            }

            if (result is null)
            {
                continue;
            }

            replayed++;
            sink(LiveMapTelemetryReplay.Describe(result, file));
            foreach (string line in LiveMapTelemetryReplay.DetailLines(result))
            {
                sink(line);
            }
            if (probe is not null && result.FirstDivergentTick > 0)
            {
                foreach (string line in DivergenceProbe.Describe(
                    System.IO.Path.Combine(telemetryDirectory, file),
                    result.FirstDivergentTick,
                    probe))
                {
                    sink(line);
                }
            }
            foreach (string line in TraceMaskAudit.Run(
                System.IO.Path.Combine(telemetryDirectory, file),
                probe!))
            {
                sink(line);
            }
            foreach (string line in BounceRuleAudit.Run(
                System.IO.Path.Combine(telemetryDirectory, file),
                probe!))
            {
                sink(line);
            }
        }

        sink(string.Create(CultureInfo.InvariantCulture, $"telemetry replay done: {replayed} capture(s)"));

        foreach (string line in PawnCollisionProbe.Run(
            TraceBackendFactory.Current ?? throw new InvalidOperationException("no trace backend")))
        {
            sink(line);
        }
    }

    /// <summary>
    /// Pass-through backend: the engine trace API has no per-call budget, the
    /// metering lives in GameWorldTracer, so diagnostics that must not disturb
    /// the solver share the real backend directly.
    /// </summary>
    private sealed class UnmeteredTraceBackend : ITraceBackend
    {
        private readonly ITraceBackend _inner;

        public UnmeteredTraceBackend(ITraceBackend inner) => _inner = inner;

        public string Name => _inner.Name;

        public bool IsAvailable => _inner.IsAvailable;

        public TraceSample Trace(Vec3 start, Vec3 end, float hullRadius, int ignoreEntityIndex) =>
            _inner.Trace(start, end, hullRadius, ignoreEntityIndex);

        public bool SupportsMaskedTrace => _inner.SupportsMaskedTrace;

        public TraceSample Trace(Vec3 start, Vec3 end, float hullRadius, int ignoreEntityIndex, ulong interactsWith, ulong interactsExclude) =>
            _inner.Trace(start, end, hullRadius, ignoreEntityIndex, interactsWith, interactsExclude);
    }

    private static Vec3 RotateUpward(Vec3 forward, float degrees)
    {
        // The engine bias is pitch-dependent: 10 degrees at level aim, lerping to
        // zero at the vertical extremes (weapon_basecsgrenade.cpp ThrowGrenade).
        // A constant rotation over-rotated every oblique recorded throw.
        float aimPitch = MathF.Asin(Math.Clamp(forward.Z, -1f, 1f)) * 180f / MathF.PI;
        float effective = degrees * ((90f - MathF.Abs(aimPitch)) / 90f);
        if (MathF.Abs(effective) < 1e-4f)
        {
            return forward;
        }

        float radians = effective * MathF.PI / 180f;
        float c = MathF.Cos(radians);
        float s = MathF.Sin(radians);
        Vec3 up = new(0f, 0f, 1f);
        Vec3 axis = Vec3.Cross(forward, up);
        if (axis.LengthSquared <= 1e-8f)
        {
            axis = new Vec3(1f, 0f, 0f);
        }

        axis = axis.Normalized();
        return (forward * c) + (Vec3.Cross(axis, forward) * s) + (axis * (Vec3.Dot(axis, forward) * (1f - c)));
    }

    /// <summary>
    /// Contract angle convention: yaw 0 looks down +X, positive pitch looks up.
    /// Mirrors Core.GrenadeSimulator.DirectionFromAngles; the plugin does not
    /// compile against Core.
    /// </summary>
    private static Vec3 AngleDirection(float yawDegrees, float pitchDegrees)
    {
        float yaw = yawDegrees * MathF.PI / 180f;
        float pitch = pitchDegrees * MathF.PI / 180f;
        float cosPitch = MathF.Cos(pitch);
        return new Vec3(
            cosPitch * MathF.Cos(yaw),
            cosPitch * MathF.Sin(yaw),
            MathF.Sin(pitch)).Normalized();
    }

    private static float HorizontalPlanar(Vec3 a, Vec3 b)
        => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static string Format(Vec3Dto v)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"({v.X:0.#}, {v.Y:0.#}, {v.Z:0.#})");
}
