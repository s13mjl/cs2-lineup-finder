using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Config;
using CS2LineupFinder.Plugin.Game;
using Xunit;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// The per-search sweep budget must be refillable: a search that runs after
/// another one would otherwise find every sweep answered with a miss and every
/// line-up report "no solution", which is exactly the leak players hit.
/// </summary>
public sealed class GameWorldTracerTests
{
    [Fact]
    public void Budget_ExhaustedSweepsMiss_RefillRestoresHits()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2lf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(
                Path.Combine(root, "config", "config.toml"),
                "[solver]\nmaxTracesPerSolve = 1\n");
            var config = PluginConfig.Load(root);

            var backend = new FakeTraceBackend { GroundZ = 0f };
            var tracer = new GameWorldTracer(backend, () => config);

            Vec3 Down() => new(0f, 0f, -1f);

            var first = tracer.Sweep(new Vec3(0f, 0f, 10f), Down(), 20f, 0f, -1, ThrowPredicates.StopsProjectile);
            Assert.True(first.DidHit, "the first sweep within budget must reach the floor");

            var exhausted = tracer.Sweep(new Vec3(0f, 0f, 10f), Down(), 20f, 0f, -1, ThrowPredicates.StopsProjectile);
            Assert.False(exhausted.DidHit, "a sweep past the budget must report a miss");

            tracer.ResetBudget();
            var refilled = tracer.Sweep(new Vec3(0f, 0f, 10f), Down(), 20f, 0f, -1, ThrowPredicates.StopsProjectile);
            Assert.True(refilled.DidHit, "ResetBudget must restore the meter");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>
/// Backend that mirrors the measured engine signature for embedded starts:
/// a sweep that begins with its hull inside the clip band reports
/// IsAllSolid=1 (exposed here as IsEmbedded) with clip content bits on the
/// default-mask overload, while the clip-excluded overload used by the main
/// sweep never sees the brush and hits the floor normally (live tracer,
/// 2026-10-19 09:48:18, probes A/J/F).
/// </summary>
internal sealed class EmbeddedClipBackend : ITraceBackend
{
    public float GroundZ { get; set; }

    /// <summary>Z range of the clip brush; a start whose hull overlaps it is embedded.</summary>
    public float ClipBottom { get; set; }

    public float ClipTop { get; set; }

    public string Name => "embedded-clip";

    public bool IsAvailable => true;

    public bool SupportsMaskedTrace => true;

    public TraceSample Trace(Vec3 start, Vec3 end, float hullRadius, int ignoreEntityIndex)
    {
        // Default mask: the clip brush is solid, so a hull overlapping the
        // band starts embedded - the engine reports IsAllSolid=1, Fraction=0.
        if (start.Z + hullRadius >= ClipBottom && start.Z - hullRadius <= ClipTop)
        {
            return new TraceSample(
                false, end, Vec3.UnitZ, 1f, -1, null,
                contentsRaw: 0x10UL | 0x20UL | 0x40000000UL,
                isEmbedded: true);
        }

        return FloorHit(start, end, hullRadius);
    }

    public TraceSample Trace(Vec3 start, Vec3 end, float hullRadius, int ignoreEntityIndex, ulong interactsWith, ulong interactsExclude)
        => FloorHit(start, end, hullRadius);

    private TraceSample FloorHit(Vec3 start, Vec3 end, float hullRadius)
    {
        var delta = end - start;
        if (delta.Z >= 0f || start.Z - hullRadius <= GroundZ)
        {
            return TraceSample.Miss(end);
        }

        // Hull centre stops one radius above the floor, matching the mock
        // worlds and the engine's swept-hull contract.
        var fraction = (start.Z - hullRadius - GroundZ) / (start.Z - end.Z);
        var position = new Vec3(
            start.X + (delta.X * fraction),
            start.Y + (delta.Y * fraction),
            GroundZ + hullRadius);
        return new TraceSample(true, position, Vec3.UnitZ, fraction);
    }
}

public sealed class GameWorldTracerEmbeddedTests
{
    private static GameWorldTracer Tracer(EmbeddedClipBackend backend) => new(backend);

    [Fact]
    public void Sweep_StartInsideClipBrush_MarksEmbedded()
    {
        var backend = new EmbeddedClipBackend { GroundZ = -10f, ClipBottom = -6f, ClipTop = -4.5f };
        var tracer = Tracer(backend);

        var hit = tracer.Sweep(
            new Vec3(0f, 0f, -5f), new Vec3(0f, 0f, -1f), 10f, 2f, -1, ThrowPredicates.StopsProjectile);

        Assert.True(hit.DidHit, "the clip-excluded sweep must still reach the floor");
        Assert.True(hit.IsEmbedded, "a hull starting inside the clip band must be marked embedded");
        Assert.Equal(0x40000030UL, hit.ContentsRaw);
    }

    [Fact]
    public void Sweep_StartAboveClipBrush_NotEmbedded()
    {
        var backend = new EmbeddedClipBackend { GroundZ = -10f, ClipBottom = -6f, ClipTop = -4.5f };
        var tracer = Tracer(backend);

        var hit = tracer.Sweep(
            new Vec3(0f, 0f, 0f), new Vec3(0f, 0f, -1f), 10f, 2f, -1, ThrowPredicates.StopsProjectile);

        Assert.True(hit.DidHit);
        Assert.False(hit.IsEmbedded, "a hull starting above the clip band is a normal contact");
        Assert.Equal(0UL, hit.ContentsRaw);
    }

    [Fact]
    public void Sweep_EmbeddedProbe_DoesNotConsumeBudget()
    {
        var backend = new EmbeddedClipBackend { GroundZ = -10f, ClipBottom = -6f, ClipTop = -4.5f };
        var root = Path.Combine(Path.GetTempPath(), "cs2lf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(
                Path.Combine(root, "config", "config.toml"),
                "[solver]\nmaxTracesPerSolve = 2\n");
            var config = PluginConfig.Load(root);
            var tracer = new GameWorldTracer(backend, () => config);

            var first = tracer.Sweep(
                new Vec3(0f, 0f, -5f), new Vec3(0f, 0f, -1f), 10f, 2f, -1, ThrowPredicates.StopsProjectile);
            Assert.True(first.DidHit);
            Assert.True(first.IsEmbedded);

            Assert.False(tracer.BudgetExhausted,
                "the diagnostic embedded probe must not consume the sweep budget");

            var second = tracer.Sweep(
                new Vec3(0f, 0f, -5f), new Vec3(0f, 0f, -1f), 10f, 2f, -1, ThrowPredicates.StopsProjectile);
            Assert.True(second.DidHit, "the next legal sweep must still work after an embedded contact");
            Assert.True(second.IsEmbedded, "the second embedded contact must also carry the flag");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
