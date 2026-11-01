using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Calibration;
using CS2LineupFinder.Plugin.Simulation;
using Xunit;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// R5 records the projectile's own launch state on every new calibration
/// sample and the replay prefers it over the synthesised aim+speed launch.
/// These tests close the two seams a silent failure could hide: the JSON
/// round trip (a renamed property would silently drop the fields) and the
/// replay branch itself (a regression would send the measured state to the
/// simulator's synthesised path, restoring the moving-throw misses).
/// </summary>
public sealed class LaunchCaptureReplayTests
{
    [Fact]
    public void Record_ThenLoad_RoundTripsLaunchFields()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2lf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var calibrator = new LandingCalibrator(dir);
            var suggestion = new PendingSuggestion(
                -160f, 5f, new Vec3(10f, 20f, 30f), 64f, GrenadeType.Smoke,
                new Vec3(-800f, -2300f, -170f), ReleasePosition: null, SuggestedTickMs: 4242);
            var sample = calibrator.Record(
                1, "de_mirage", new Vec3(10f, 20f, 30f), 64f, suggestion,
                new Vec3(-810f, -2310f, -170f),
                releasePosition: new Vec3(11f, 21f, 93f),
                launchPosition: new Vec3(12f, 22f, 94f),
                launchVelocity: new Vec3(600f, 0f, 200f));

            Assert.NotNull(sample);
            var loaded = LandingReplayFile.LoadAll(dir);

            var dto = Assert.Single(loaded);
            Assert.NotNull(dto.LaunchPosition);
            Assert.NotNull(dto.LaunchVelocity);
            Assert.Equal(12f, dto.LaunchPosition!.ToVec3().X, 3f);
            Assert.Equal(94f, dto.LaunchPosition!.ToVec3().Z, 3f);
            Assert.Equal(600f, dto.LaunchVelocity!.ToVec3().X, 3f);
            Assert.Equal(200f, dto.LaunchVelocity!.ToVec3().Z, 3f);
            Assert.Equal(4242L, dto.SuggestedTickMs);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_OldFileWithoutLaunchFields_FieldsStayNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2lf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            // Old-schema file: exactly the fields the 2026-10-15 writer produced.
            File.WriteAllText(
                Path.Combine(dir, "landings-20261015.json"),
                "{\"samples\":[{\"index\":1,\"map\":\"de_mirage\"," +
                "\"origin\":{\"x\":0,\"y\":0,\"z\":0},\"eyeHeight\":64," +
                "\"aimYaw\":0,\"aimPitch\":0,\"grenadeType\":\"Smoke\"," +
                "\"predictedImpact\":{\"x\":500,\"y\":0,\"z\":0}," +
                "\"actualImpact\":{\"x\":520,\"y\":0,\"z\":0},\"error\":20," +
                "\"recordedAt\":\"2026-10-15T00:00:00Z\"}]}");

            var loaded = LandingReplayFile.LoadAll(dir);

            var dto = Assert.Single(loaded);
            Assert.Null(dto.LaunchPosition);
            Assert.Null(dto.LaunchVelocity);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReplayCalibrationToLog_MeasuredLaunchState_ReachesSimulator()
    {
        using var harness = new Harness();
        var recorder = new RecordingSimulator();
        harness.Bridge.UseCoreSimulator(recorder);
        var dir = Path.Combine(harness.Root, "calibration");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "landings-20261019.json"),
            "{\"samples\":[{\"index\":7,\"map\":\"de_mirage\"," +
            "\"origin\":{\"x\":0,\"y\":0,\"z\":0},\"eyeHeight\":64," +
            "\"aimYaw\":0,\"aimPitch\":0,\"grenadeType\":\"Smoke\"," +
            "\"predictedImpact\":{\"x\":1257,\"y\":0,\"z\":-2}," +
            "\"actualImpact\":{\"x\":1257,\"y\":0,\"z\":-2},\"error\":0," +
            "\"recordedAt\":\"2026-10-19T00:00:00Z\"," +
            "\"launchPosition\":{\"x\":1000,\"y\":0,\"z\":-100}," +
            "\"launchVelocity\":{\"x\":600,\"y\":0,\"z\":200}}]}");

        var log = new List<string>();
        harness.Service.ReplayCalibrationToLog(dir, "de_mirage", harness.World, log.Add);

        var call = Assert.Single(recorder.Calls);
        // The measured launch state must arrive verbatim. The synthesised path
        // would instead start near (15.9, 0, 64+) with a ~675 u/s aim-aligned
        // velocity - i.e. the exact reconstruction that dropped the inherited
        // thrower velocity and produced the 75.9u/132.8u moving-throw misses.
        Assert.Equal(1000f, call.Origin.X, 3f);
        Assert.Equal(-100f, call.Origin.Z, 3f);
        Assert.Equal(600f, call.Velocity.X, 3f);
        Assert.Equal(200f, call.Velocity.Z, 3f);
    }

    [Fact]
    public void ReplayCalibrationToLog_ZeroLaunchVelocity_FallsBackToSynthesis()
    {
        using var harness = new Harness();
        var recorder = new RecordingSimulator();
        harness.Bridge.UseCoreSimulator(recorder);
        var dir = Path.Combine(harness.Root, "calibration");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "landings-20261019.json"),
            "{\"samples\":[{\"index\":7,\"map\":\"de_mirage\"," +
            "\"origin\":{\"x\":0,\"y\":0,\"z\":0},\"eyeHeight\":64," +
            "\"aimYaw\":0,\"aimPitch\":0,\"grenadeType\":\"Smoke\"," +
            "\"predictedImpact\":{\"x\":500,\"y\":0,\"z\":-2}," +
            "\"actualImpact\":{\"x\":500,\"y\":0,\"z\":-2},\"error\":0," +
            "\"recordedAt\":\"2026-10-19T00:00:00Z\"," +
            "\"launchPosition\":{\"x\":1000,\"y\":0,\"z\":-100}," +
            "\"launchVelocity\":{\"x\":0,\"y\":0,\"z\":0}}]}");

        var log = new List<string>();
        harness.Service.ReplayCalibrationToLog(dir, "de_mirage", harness.World, log.Add);

        var call = Assert.Single(recorder.Calls);
        // A zero measured velocity must NOT reach the simulator as-is (it would
        // be a degenerate throw); the synthesised full-throw path is the
        // documented fallback for files that carry no usable launch state.
        Assert.Equal(675f, call.Velocity.Length, 1f);
        Assert.NotEqual(-100f, call.Origin.Z, 3f);
    }

    private sealed class RecordingSimulator : ITrajectorySimulator
    {
        public List<(Vec3 Origin, Vec3 Velocity)> Calls { get; } = new();

        public TrajectoryResult Simulate(ThrowParams parameters, IWorldGeometry world)
        {
            Calls.Add((parameters.Origin, parameters.Velocity));
            var impact = new FinalImpact(parameters.Origin, Vec3.UnitZ, 0, 0f, true, parameters.GrenadeType);
            return new TrajectoryResult(new List<TrajectoryPoint>(), impact, false);
        }

        public Vec3 DeriveCarriedVelocity(Vec3 launchVelocity, Vec3 aimForward, ThrowButton button) => Vec3.Zero;

        public TrajectoryResult SimulateWithDiagnostics(ThrowParams parameters, IWorldGeometry world, out SimulationDiagnostics diagnostics)
        {
            diagnostics = new SimulationDiagnostics(0f, 0f, 0f, 0, 0);
            return Simulate(parameters, world);
        }

        public Task<SolverOutcome> SolveLineupAsync(LineupRequest request, ILineupSolver solver, IWorldGeometry world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("replay tests never solve");

        public ILineupSearch? BeginLineupSearch(LineupRequest request, ILineupSolver solver, IWorldGeometry world) => null;
    }
}
