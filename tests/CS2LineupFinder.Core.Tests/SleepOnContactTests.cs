// <copyright file="SleepOnContactTests.cs" company="CS2LineupFinder">
// The engine does not stop a grenade with a speed threshold. telemetry-001 shows
// the last three ticks carrying a perfectly constant horizontal velocity of
// 25.7591 / -9.8880 - zero friction, no damping - and then a final tick whose
// displacement is only 0.563 of a full tick. That final tick is the grenade being
// moved to the exact ground contact and sleeping there.
// </copyright>

using System;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Core.Tests;

public class SleepOnContactTests
{
    private readonly ITestOutputHelper _output;

    public SleepOnContactTests(ITestOutputHelper output) => _output = output;

    private const float Gravity = 320f;
    private const float Tick = 1f / 64f;

    // The four rows that end telemetry-001, read verbatim from the CSV.
    private static readonly Vec3[] Positions =
    {
        new(-907.7542f, -2401.6330f, -165.8849f),
        new(-908.1567f, -2401.7870f, -165.8080f),
        new(-908.5591f, -2401.9410f, -165.8092f),
        new(-908.9616f, -2402.0950f, -165.8886f),
        new(-909.1882f, -2402.1820f, -165.9688f),
    };

    private static readonly Vec3[] Velocities =
    {
        new(-25.7591f, -9.8880f, 7.4191f),
        new(-25.7591f, -9.8880f, 2.4191f),
        new(-25.7591f, -9.8880f, -2.5809f),
        new(-25.7591f, -9.8880f, -7.5809f),
        new(0f, 0f, 0f),
    };

    // The surface the hull comes to rest on. The recorder reads the hull centre,
    // and the centre settles 2 units above the surface, so the contact height the
    // sweep has to reach is the resting centre itself.
    private const float ContactCentreZ = -165.9688f;

    /// <summary>
    /// The three rows before the stop show an undamped horizontal velocity, so
    /// there is no friction to model and no threshold that could have fired: the
    /// grenade is still moving at 28 u/s when it stops.
    /// </summary>
    [Fact]
    public void TheGrenadeIsStillMovingWhenItStops()
    {
        _output.WriteLine($"horizontal velocity on the last three ticks: " +
            $"{Velocities[0].Length2D:0.####}, {Velocities[1].Length2D:0.####}, {Velocities[2].Length2D:0.####}");
        _output.WriteLine($"speed on the tick it stops: {Velocities[3].Length:0.####}");

        // Identical horizontal velocity across three ticks means nothing is
        // damping it, so a "fell below the rest speed" rule cannot explain the stop.
        Assert.True(
            MathF.Abs(Velocities[0].Length2D - Velocities[2].Length2D) < 0.001f,
            "the horizontal velocity is constant while rolling");
        Assert.True(
            Velocities[3].Length > 20f,
            $"the grenade is still travelling at {Velocities[3].Length:0.##} u/s when it stops");
    }

    /// <summary>
    /// The final tick moved the grenade to the ground contact and no further:
    /// integrating from the previous row for the time it takes the hull centre to
    /// reach the contact height reproduces all three displacement components.
    /// </summary>
    [Fact]
    public void TheFinalTickEndsExactlyOnTheContactHeight()
    {
        Vec3 from = Positions[3];
        Vec3 velocity = Velocities[3];

        // Time for the centre to fall from its current height to the contact
        // height, integrating gravity first (the engine applies gravity at the
        // head of the tick, which every other row in this capture proves).
        //   drop = (vz - g*t) * t
        // drop is negative here (the grenade is falling), so with
        // drop = vz*t - 0.5*g*t^2 the coefficients are
        //   0.5*g*t^2 - vz*t + drop = 0
        // and the small positive root is the one inside this tick.
        //   drop = vz*t - 0.5*g*t^2
        //   0.5*g*t^2 - vz*t + drop = 0
        // with a = 0.5*g, b = -vz, c = drop, and the wanted root the small
        // positive one, t = (-b + sqrt(b^2 - 4ac)) / (2a).
        float drop = ContactCentreZ - from.Z;
        float a = Gravity * 0.5f;
        float b = -velocity.Z;
        float discriminant = (b * b) - (4f * a * drop);
        float contactTime = (-b + MathF.Sqrt(discriminant)) / (2f * a);

        Vec3 predicted = from + (velocity * contactTime) + (new Vec3(0f, 0f, -Gravity) * (0.5f * contactTime * contactTime));
        Vec3 actual = Positions[4];
        Vec3 error = predicted - actual;

        _output.WriteLine($"contact time in the final tick: {contactTime:0.000000}s ({contactTime / Tick:0.000} of a tick)");
        _output.WriteLine($"predicted ({predicted.X:0.####},{predicted.Y:0.####},{predicted.Z:0.####})");
        _output.WriteLine($"actual    ({actual.X:0.####},{actual.Y:0.####},{actual.Z:0.####})");
        _output.WriteLine($"error     ({error.X:0.####},{error.Y:0.####},{error.Z:0.####}) length {error.Length:0.####}u");

        // The prediction is the whole point: it lands within a rounding error of
        // the recorded stop, using nothing but gravity and the recorded velocity.
        Assert.True(error.Length < 0.01f,
            $"the contact model should reproduce the stop to 0.01u, got {error.Length:0.####}u");
    }

    /// <summary>
    /// The final tick is a fraction of a tick, which is what "moved to the
    /// contact and slept" looks like in the samples.
    /// </summary>
    [Fact]
    public void TheFinalTickIsAFractionOfATick()
    {
        Vec3 from = Positions[3];
        Vec3 velocity = Velocities[3];

        // Read the fraction straight off the horizontal displacement, which is
        // unaffected by gravity and therefore the cleanest measure.
        float fraction = (Positions[4].X - from.X) / (velocity.X * Tick);

        _output.WriteLine($"final tick length: {fraction:0.0000} of a full tick");

        Assert.True(
            fraction > 0f && fraction < 1f,
            $"the final tick should be a partial tick, got {fraction:0.0000}");
    }
}
