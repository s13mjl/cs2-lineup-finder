using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;
using Xunit;

namespace CS2LineupFinder.Math.Tests;

/// <summary>
/// The engine raises every throw by the profile's aim bias, so the pitch a player
/// aims (engine convention, negative looks up) is bias minus the physical release
/// elevation. Before the fix the candidates told players to aim the physical
/// elevation itself, putting every throw roughly ten degrees off.
/// </summary>
public class ProfileAimBiasTests
{
    [Fact]
    public async Task EnumerateCandidates_ConvertsElevationIntoAimPitch_UsingTheProfileBias()
    {
        var profile = Fixture.Smoke() with { AimBiasDegrees = 10f };
        var candidates = new List<AngleCandidate>();
        await foreach (var candidate in new LineupSolver().EnumerateCandidatesAsync(
            Fixture.Request(FixtureMaxLevel), profile))
        {
            candidates.Add(candidate);
        }

        // Level throw at 400 u/s under 800 u/s^2 gravity: the 200 unit mark is the
        // maximum level range, where both vacuum elevations coincide at 45 degrees.
        // The pitch a player aims is the exact inverse of the pitch-dependent bias:
        // (45 - 10) * 90 / (90 - 10) = 39.375. The naive constant 45 - 10 = 35
        // over-aimed, and candidates use the contract convention (positive looks
        // up), matching the Core simulator.
        Assert.Contains(candidates, c => System.Math.Abs(c.Pitch - 39.375f) < 0.2f);
    }

    private static readonly Vec3 FixtureMaxLevel = new(200f, 0f, 64f);
}
