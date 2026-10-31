using System.Collections.Generic;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Game;
using Xunit;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// Stepping past an entity a grenade flies through must keep every resumed
/// trace inside the distance the caller asked for, and each attempt must
/// start past the previous hit. A sweep that ran past its own end point, or
/// restarted from the origin, would let a grenade stop on geometry it never
/// reached.
/// </summary>
public sealed class SweepRangeTests
{
    [Fact]
    public void SteppedSweep_IssuesForwardProgressingTraces()
    {
        var backend = new RecordingBackend();
        var tracer = new GameWorldTracer(backend);

        backend.Walls.Add(8f);
        backend.Blockers.Add(4f);
        backend.Blockers.Add(12f);

        // A throwaway first sweep proves the wall is hit before the blocker.
        var probe = tracer.Sweep(
            new Vec3(0f, 0f, 0f),
            new Vec3(1f, 0f, 0f),
            20f,
            0f,
            -1,
            static name => true);

        Assert.True(probe.DidHit, "the keep predicate reports every hit");
        Assert.Equal(8f, probe.Position.X, 3);
        Assert.Single(backend.Issued);

        var hit = tracer.Sweep(
            new Vec3(0f, 0f, 0f),
            new Vec3(1f, 0f, 0f),
            20f,
            0f,
            -1,
            static name => name is not "blocker");

        Assert.True(hit.DidHit, "the solid wall must stop the sweep");
        Assert.Equal(8f, hit.Position.X, 3);
        Assert.Equal(2, backend.Issued.Count);
        Assert.Equal(new Vec3(0f, 0f, 0f), backend.Issued[0].From);
        Assert.Equal(new Vec3(20f, 0f, 0f), backend.Issued[0].To);
        Assert.Equal(new Vec3(0f, 0f, 0f), backend.Issued[1].From);
        Assert.Equal(new Vec3(20f, 0f, 0f), backend.Issued[1].To);
    }

    /// <summary>
    /// A sweep that meets only entities a grenade flies through must report a
    /// miss: stopping on one of them would park the grenade in mid-air, which is
    /// exactly the "landed inside the zone after 64 simulations" failure.
    /// </summary>
    /// <remarks>
    /// Every entry in <see cref="RecordingBackend.Blockers"/> needs its own
    /// entity index. The tracer refuses to step past the same index twice, so a
    /// backend that reuses one index would look like a loop and stop the sweep
    /// early instead of walking past three separate entities.
    /// </remarks>
    [Fact]
    public void SweepPastOnlyPassThroughEntities_Misses()
    {
        var backend = new RecordingBackend();
        var tracer = new GameWorldTracer(backend);

        // Nothing solid anywhere: only entities a grenade flies through.
        backend.Blockers.Add(4f);
        backend.Blockers.Add(12f);
        backend.Blockers.Add(16f);

        // The predicate says "this does not stop a grenade", so the loop steps
        // past every entity it is handed until the step-over budget runs out.
        var hit = tracer.Sweep(
            new Vec3(0f, 0f, 0f),
            new Vec3(1f, 0f, 0f),
            20f,
            0f,
            -1,
            static name => name is not "blocker");

        Assert.False(hit.DidHit, "nothing solid was in the way");
        Assert.Equal(new Vec3(20f, 0f, 0f), hit.Position);
        Assert.Equal(4, backend.Issued.Count);
        Assert.Equal(new Vec3(17f, 0f, 0f), backend.Issued[3].From);

        // Every trace after the first starts past the entity it stepped over,
        // and none of them runs past the requested end point.
        for (var i = 1; i < backend.Issued.Count; i++)
        {
            var (from, to) = backend.Issued[i];
            Assert.True(to.X <= 20f + 1e-4f, $"a trace ran past the requested range: {to.X}");
            Assert.True(from.X > backend.Issued[i - 1].From.X, "a resumed trace must advance");
            Assert.True(to.X > from.X, "a trace must make forward progress");
        }
    }
}

/// <summary>
/// Trace backend that records every issued trace and answers from two fixed
/// lists: entities a grenade flies through, and geometry that stops it.
/// </summary>
internal sealed class RecordingBackend : ITraceBackend
{
    internal List<float> Blockers { get; } = new();

    internal List<float> Walls { get; } = new();

    internal List<(Vec3 From, Vec3 To)> Issued { get; } = new();

    public string Name => "recording";

    public bool IsAvailable => true;

    public TraceSample Trace(Vec3 start, Vec3 end, float hullRadius, int ignoreEntityIndex)
    {
        Issued.Add((start, end));

        float? hit = null;
        string? name = null;

        foreach (var wall in Walls)
        {
            if (wall > start.X && wall <= end.X && ignoreEntityIndex is -1)
            {
                hit = wall;
                name = "wall";
                break;
            }
        }

        if (hit is null)
        {
            // One index per blocker: the tracer steps past the entities it is
            // handed, so each of them has to be distinguishable.
            for (var i = 0; i < Blockers.Count; i++)
            {
                var blocker = Blockers[i];
                if (blocker > start.X && blocker <= end.X)
                {
                    hit = blocker;
                    name = "blocker";
                    break;
                }
            }
        }

        if (hit is null)
        {
            return TraceSample.Miss(end);
        }

        return new TraceSample(
            true,
            new Vec3(hit.Value, 0f, 0f),
            new Vec3(-1f, 0f, 0f),
            (hit.Value - start.X) / (end.X - start.X),
            Blockers.IndexOf(hit.Value) + 5,
            name);
    }
}
