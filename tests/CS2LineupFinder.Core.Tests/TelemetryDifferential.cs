// <copyright file="TelemetryDifferential.cs" company="CS2LineupFinder">
// Automated replay of recorded in-game evidence against the simulator, split
// into the differentials that localise a landing error. Two evidence sources
// are supported, in descending order of directness:
//
//   1. Landing samples  (plugins/CS2LineupFinder/Calibration): the plugin told
//      the player an aim, the player threw, and the game reported where the
//      smoke actually came to rest. This is the ONLY source that measures the
//      full solver path, because it carries the aim angles the solver produced.
//   2. Raw telemetry     (plugins/CS2LineupFinder/Telemetry): every tick of one
//      real flight, straight off the projectile entity. It carries the engine's
//      own release state but no aim angles, so it cannot test the inputs the
//      solver derives.
//
// The split is the whole point. The simulator already reproduces a recorded
// ballistic flight to well under a unit when handed the true release point and
// the true launch velocity, so a real in-game miss has to originate in the two
// inputs the solver DERIVES rather than measures: the release point (ReleaseOffset
// plus the feet->eye->forward chain) and the launch velocity (AimBiasDegrees and
// VelocityInheritance). This harness measures each of those against the engine's
// own record, so there is no guessing which side is wrong.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;

namespace CS2LineupFinder.Core.Tests;

/// <summary>One row of a telemetry capture.</summary>
public readonly record struct TelemetryRow(float Time, Vec3 Position, Vec3 Velocity)
{
    /// <summary>Speed magnitude recorded by the engine for this tick.</summary>
    public float Speed => Velocity.Length;
}

/// <summary>A parsed telemetry capture plus its recorded endpoint.</summary>
public sealed class TelemetryCapture
{
    /// <summary>Tick rows in file order.</summary>
    public IReadOnlyList<TelemetryRow> Rows { get; }

    /// <summary>Endpoint the game reported through the detonate event.</summary>
    public Vec3 RecordedEnd { get; }

    /// <summary>Number of rows.</summary>
    public int Count => Rows.Count;

    /// <summary>First row, the launch state.</summary>
    public TelemetryRow Launch => Rows[0];

    private TelemetryCapture(IReadOnlyList<TelemetryRow> rows, Vec3 recordedEnd)
    {
        Rows = rows;
        RecordedEnd = recordedEnd;
    }

    /// <summary>Reads a telemetry CSV and its sidecar JSON endpoint.</summary>
    /// <param name="csvPath">Path to the telemetry CSV.</param>
    /// <returns>The parsed capture, or <see langword="null"/> when unreadable.</returns>
    public static TelemetryCapture? Load(string csvPath)
    {
        if (!File.Exists(csvPath))
        {
            return null;
        }

        string jsonPath = Path.ChangeExtension(csvPath, ".json");
        Vec3 end = Vec3.Zero;
        if (File.Exists(jsonPath))
        {
            string json = File.ReadAllText(jsonPath);
            end = new Vec3(ReadJsonNumber(json, "X"), ReadJsonNumber(json, "Y"), ReadJsonNumber(json, "Z"));
        }

        var rows = new List<TelemetryRow>();
        bool header = true;
        foreach (string line in File.ReadLines(csvPath))
        {
            if (header)
            {
                header = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] f = line.Split(",");
            if (f.Length < 7)
            {
                continue;
            }

            rows.Add(new TelemetryRow(
                Parse(f[0]),
                new Vec3(Parse(f[1]), Parse(f[2]), Parse(f[3])),
                new Vec3(Parse(f[4]), Parse(f[5]), Parse(f[6]))));
        }

        if (rows.Count == 0)
        {
            return null;
        }

        if (end == Vec3.Zero)
        {
            end = rows[^1].Position;
        }

        return new TelemetryCapture(rows, end);
    }

    private static float Parse(string s) => float.Parse(s.Trim(), CultureInfo.InvariantCulture);

    // The sidecar is a flat object with X/Y/Z in one place, so a plain substring
    // search is enough and avoids a JSON dependency in the test layer.
    private static float ReadJsonNumber(string json, string key)
    {
        int i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0)
        {
            return 0f;
        }

        int colon = json.IndexOf(":", i);
        if (colon < 0)
        {
            return 0f;
        }

       int start = colon + 1;
        int stop = start;
        while (stop < json.Length && (char.IsDigit(json[stop]) || json[stop] == '\u002D' || json[stop] == '.'))
        {
            stop++;
        }

        return float.TryParse(json.AsSpan(start, stop - start), CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }
}

/// <summary>One landing sample written by the in-game calibration pair.</summary>
public sealed record LandingSample(
    string Map,
    Vec3 Feet,
    float EyeHeight,
    float AimYaw,
    float AimPitch,
    Vec3 PredictedImpact,
    Vec3 ActualImpact)
{
    /// <summary>
    /// Reads a calibration file written by the in-game landing pair. The file is
    /// a JSON object with a "samples" array whose entries carry the stance, the
    /// aim the plugin asked for, and both the predicted and the actual landing.
    /// A missing or unreadable file yields an empty list, so the caller treats
    /// "no evidence" and "malformed evidence" the same way.
    /// </summary>
    public static IReadOnlyList<LandingSample> Parse(string jsonPath)
    {
        var samples = new List<LandingSample>();
        if (!File.Exists(jsonPath))
        {
            return samples;
        }

        string json = File.ReadAllText(jsonPath);
        foreach (string block in Blocks(json))
        {
            samples.Add(new LandingSample(
                Text(block, "Map") ?? string.Empty,
                Vector(block, "Origin"),
                Number(block, "EyeHeight", 64f),
                Number(block, "AimYaw", 0f),
                Number(block, "AimPitch", 0f),
                Vector(block, "PredictedImpact"),
                Vector(block, "ActualImpact")));
        }

        return samples;
    }

   /// <summary>Test seam: the first raw sample block, for parser diagnostics.</summary>
   public static string DumpFirstBlock(string jsonPath)
   {
       List<string> blocks = Blocks(File.ReadAllText(jsonPath));
       return blocks.Count == 0 ? "<none>" : blocks[0];
   }

    /// <summary>Test seam: a named sub-object, for parser diagnostics.</summary>
    public static string DumpVector(string block, string key)
    {
        int i = IndexOfKey(block, key);
        if (i < 0)
        {
            return "IndexOfKey=" + i;
        }

        int brace = block.IndexOf('{', i);
        if (brace < 0)
        {
            return "brace=-1 (key at " + i + ")";
        }

        int close = block.IndexOf('}', brace);
        if (close < 0)
        {
            return "close=-1";
        }

        return block[brace..(close + 1)];
    }

    /// <summary>Test seam: a named number, for parser diagnostics.</summary>
    public static float DumpNumber(string block, string key)
    {
        int i = IndexOfKey(block, key);
        return i < 0 ? -9999f : Number(block, key, -1f);
    }

    /// <summary>Test seam: a named string, for parser diagnostics.</summary>
    public static string? DumpText(string block, string key)
    {
        int i = IndexOfKey(block, key);
        return i < 0 ? "IndexOfKey=" + i : Text(block, key);
    }

    // Every sample is an object whose first member is "Index", so the opening
    // brace sits BEFORE that member. A plain brace-depth split cannot be used
    // here: the samples live inside an array, so the outermost object is the
    // wrapper and the split would stop there and yield one block for the whole
    // file.
   private static List<string> Blocks(string json)
   {
       var blocks = new List<string>();
       int cursor = 0;
        while (true)
        {
            int index = json.IndexOf("\"Index\"", cursor, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            // Walk back to the brace that opens the object holding this member.
            int open = -1;
            for (int i = index; i >= 0; i--)
            {
                if (json[i] == '{')
                {
                    open = i;
                    break;
                }
            }

            if (open < 0)
            {
                break;
            }

            // Walk forward to its matching close, tracking depth.
            int depth = 0;
            int close = -1;
            for (int i = open; i < json.Length; i++)
            {
                if (json[i] == '{')
                {
                    depth++;
                }
                else if (json[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        close = i;
                        break;
                    }
                }
            }

            if (close < 0)
            {
                break;
            }

            blocks.Add(json[open..(close + 1)]);
            cursor = close + 1;
        }

        return blocks;
   }
    // Reads a named sub-object of a sample block, e.g. "Origin": { X, Y, Z }.
    private static Vec3 Vector(string block, string key)
    {
        int i = IndexOfKey(block, key);
        if (i < 0)
        {
            return Vec3.Zero;
        }

        int brace = block.IndexOf('{', i);
        if (brace < 0)
        {
            return Vec3.Zero;
        }

        int close = block.IndexOf('}', brace);
        if (close < 0)
        {
            return Vec3.Zero;
        }

        string body = block[(brace + 1)..close];
        return new Vec3(
            Field(body, "X"),
            Field(body, "Y"),
            Field(body, "Z"));
    }

    private static float Field(string body, string key)
    {
        int i = IndexOfKey(body, key);
        if (i < 0)
        {
            return 0f;
        }

        return Number(body, key, 0f);
    }

    private static int IndexOfKey(string body, string key)
    {
        int i = body.IndexOf('"' + key + '"', StringComparison.Ordinal);
        return i < 0 ? body.IndexOf(key, StringComparison.Ordinal) : i;
    }

    private static string? Text(string block, string key)
    {
        int i = IndexOfKey(block, key);
        if (i < 0)
        {
            return null;
        }

        int colon = block.IndexOf(':', i);
        if (colon < 0)
        {
            return null;
        }

        int open = block.IndexOf('"', colon);
        if (open < 0)
        {
            return null;
        }

        int close = block.IndexOf('"', open + 1);
        return close < 0 ? null : block[(open + 1)..close];
    }

   private static float Number(string block, string key, float fallback)
   {
       int i = IndexOfKey(block, key);
       if (i < 0)
       {
           return fallback;
       }

       int colon = block.IndexOf(':', i);
       if (colon < 0)
       {
           return fallback;
       }

       int start = colon + 1;
        while (start < block.Length && char.IsWhiteSpace(block[start]))
        {
            start++;
        }

        int stop = start;
        while (stop < block.Length && (char.IsDigit(block[stop]) || block[stop] == '\u002D' || block[stop] == '.'))
        {
            stop++;
        }

        return float.TryParse(block.AsSpan(start, stop - start), CultureInfo.InvariantCulture, out float v) ? v : fallback;
    }
}


/// <summary>The outcome of one differential replay.</summary>
public sealed record DifferentialResult(
    string Label,
    Vec3 Predicted,
    Vec3 Actual,
    float HorizontalError,
    float VerticalError,
    int Bounces,
    float FlightTime);

/// <summary>
/// Replays recorded evidence and reports where a landing error comes from.
/// </summary>
public sealed class TelemetryDifferential
{
    /// <summary>
    /// Replays one capture against the simulator. The capture is replayed on the
    /// terrain it itself proves: the endpoint sits one hull half-height above the
    /// surface it rests on, so the replay floor is the recorded endpoint minus that
    /// half-height, and no map geometry is assumed.
    /// </summary>
    /// <param name="capture">Parsed telemetry capture.</param>
    /// <param name="parameters">Physics parameters under test.</param>
    /// <param name="launchYaw">Yaw the plugin solved for, when the capture was one of its own suggestions.</param>
    /// <param name="launchPitch">Pitch the plugin solved for.</param>
    /// <param name="feet">Throw-time feet position, when the capture was one of its own suggestions.</param>
    /// <param name="eyeHeight">Eye height above the feet.</param>
    /// <returns>Ballistic, launch-velocity and release-point results, in that order.</returns>
    public static IReadOnlyList<DifferentialResult> Run(
        TelemetryCapture capture,
        PhysicsParameters parameters,
        float launchYaw = 0f,
        float launchPitch = 0f,
        Vec3? feet = null,
        float eyeHeight = 64f)
    {
        var results = new List<DifferentialResult>();

        // 1. Pure ballistic flight: hand the simulator the engine's own launch
        //    state. Nothing here is derived by the plugin, so any residual is a
        //    genuine model error on this terrain.
        results.Add(Replay(
            "ballistic (engine launch state)",
            capture.Launch.Position,
            capture.Launch.Velocity,
            capture.RecordedEnd,
            MockWorldGeometry.Empty().AddInfiniteFloor(capture.RecordedEnd.Z - 2f),
            new GrenadeSimulator(parameters)));

        if (feet is not null)
        {
            Vec3 aim = GrenadeSimulator.DirectionFromAnglesForTest(launchYaw, launchPitch);

            // 2. Launch velocity only: keep the engine's release point, recompute
            //    the velocity from the solved angles. A residual here isolates the
            //    velocity model (AimBias, VelocityInheritance, launch speed).
            Vec3 velocity = ThrowVelocityCalculator.Compute(
                aim, Vec3.Zero, ThrowButton.Primary, GrenadeType.Smoke, 1f, parameters);
            results.Add(Replay(
                "launch velocity (recomputed from angles)",
                capture.Launch.Position,
                velocity,
                capture.RecordedEnd,
                MockWorldGeometry.Empty().AddInfiniteFloor(capture.RecordedEnd.Z - 2f),
                new GrenadeSimulator(parameters)));

            // 3. Release point too: derive it from the solved angles and the feet
            //    position, exactly like the solver does.
            Vec3 release = ThrowVelocityCalculator.ReleasePoint(
                feet.Value + new Vec3(0f, 0f, eyeHeight), aim, parameters);
            results.Add(Replay(
                "release point (solver-derived)",
                release,
                velocity,
                capture.RecordedEnd,
                MockWorldGeometry.Empty().AddInfiniteFloor(capture.RecordedEnd.Z - 2f),
                new GrenadeSimulator(parameters)));
        }

        return results;
    }

    /// <summary>
    /// Runs the full solver path on a landing sample and reports the same three
    /// components. A landing sample is the only evidence that carries the aim
    /// angles the solver produced, so it is the only place the derived inputs can
    /// be scored against a real throw.
    /// </summary>
    public static IReadOnlyList<DifferentialResult> Run(
        LandingSample sample,
        PhysicsParameters parameters,
        Vec3 engineLaunchVelocity)
    {
        var results = new List<DifferentialResult>();

        Vec3 aim = GrenadeSimulator.DirectionFromAnglesForTest(sample.AimYaw, sample.AimPitch);
        Vec3 derived = ThrowVelocityCalculator.Compute(
            aim, Vec3.Zero, ThrowButton.Primary, GrenadeType.Smoke, 1f, parameters);
        Vec3 release = ThrowVelocityCalculator.ReleasePoint(
            sample.Feet + new Vec3(0f, 0f, sample.EyeHeight), aim, parameters);

        IWorldGeometry world = MockWorldGeometry.Empty().AddInfiniteFloor(sample.ActualImpact.Z - 2f);
        // 1. The full solver path: derived release point AND derived velocity.
        results.Add(Replay(
            "solver path (aim -> release + velocity)",
            release,
            derived,
            sample.ActualImpact,
            world,
            new GrenadeSimulator(parameters)));

        // 2. Derived release point with the engine's true launch velocity, which
        //    splits the release-point error away from the velocity error.
        //    When the engine velocity is unknown (Vec3.Zero) this row is skipped
        //    rather than replayed, because a zero velocity is not a measurement.
        results.Add(Replay(
            "ballistic (engine launch velocity, derived release point)",
            release,
            engineLaunchVelocity,
            sample.ActualImpact,
            world,
            new GrenadeSimulator(parameters)));

       if (engineLaunchVelocity.IsZero())
       {
           results.RemoveAt(results.Count - 1);
       }

        return results;
    }

    private static DifferentialResult Replay(
        string label,
        Vec3 release,
        Vec3 velocity,
        Vec3 actual,
        IWorldGeometry world,
        ITrajectorySimulator simulator)
    {
        TrajectoryResult r = simulator.Simulate(
            new ThrowParams(release, velocity, GrenadeType.Smoke, 1f, 64),
            world);

        Vec3 d = r.Impact.Position - actual;
        return new DifferentialResult(
            label,
            r.Impact.Position,
            actual,
            new Vec3(d.X, d.Y, 0f).Length,
            d.Z,
            r.BounceCount,
            r.FlightTime);
    }
}
