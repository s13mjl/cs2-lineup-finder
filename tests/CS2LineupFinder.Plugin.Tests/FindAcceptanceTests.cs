using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Commands;
using CS2LineupFinder.Plugin.Game;
using CS2LineupFinder.Plugin.Simulation;
using Xunit;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// Acceptance path of <c>css_lf_find</c>: a prepared session, a search that
/// finishes inside the three second budget and the chat plus world feedback a
/// player sees afterwards.
/// </summary>
public sealed class FindAcceptanceTests
{
    [Fact]
    public async Task Find_UnderTheStub_ReturnsInsideTheBudget_WithAnglesAtOneDecimal()
    {
        using var harness = new Harness();
        harness.PrepareSession();

        var stopwatch = Stopwatch.StartNew();
        var outcome = await harness.Service.FindAsync(Harness.Slot, harness.World);
        stopwatch.Stop();

        Assert.NotNull(outcome);
        Assert.Equal(SolverStatus.Success, outcome!.Status);
        Assert.NotEmpty(outcome.Results);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"The stub took {stopwatch.Elapsed.TotalSeconds:0.00}s, which is over the 3s budget.");
        Assert.True(outcome.Elapsed < TimeSpan.FromSeconds(3));

        // Every reply is prefixed, so the plugin's lines stay readable in a busy chat.
        Assert.All(harness.Messages.Lines, line => Assert.True(
            line.StartsWith("[LineupFinder] ", StringComparison.Ordinal),
            $"Unprefixed reply: {line}"));

        var found = harness.Messages.Lines.Single(line => line.Contains("line-up(s) in", StringComparison.Ordinal));
        Assert.Contains("stub-coarse-grid-v0", found);

        var solution = outcome.Results[0];
        Assert.True(solution.TargetDistance < 120f, $"The stub reported a {solution.TargetDistance} unit miss.");

        var resultLine = harness.Messages.Lines.Single(line => line.Contains("#1", StringComparison.Ordinal));
        Assert.Matches(@"yaw -?\d+\.\d", resultLine);
        Assert.Matches(@"pitch -?\d+\.\d", resultLine);
        Assert.Contains($"yaw {CommandParser.RoundAngle(solution.Yaw):0.0}", resultLine);

        // Players read pitch in the engine convention, so chat shows the mirror of
        // the contract angle: a positive contract pitch is a downward look.
        var playerPitch = CommandParser.RoundPlayerPitch(solution.Pitch);
        Assert.Contains($"pitch {playerPitch:0.0}", resultLine);
        Assert.Equal(-CommandParser.RoundAngle(solution.Pitch), playerPitch);
    }

    [Fact]
    public async Task Find_DrawsOneBeamAndOneMarker_AtTheSolutionImpact()
    {
        using var harness = new Harness();
        var session = harness.PrepareSession();

        var outcome = await harness.Service.FindAsync(Harness.Slot, harness.World);

        Assert.NotNull(outcome);
        var solution = outcome!.Results[0];
        var expectedOrigin = session.EyePoint!.Value;
        var expectedDirection = CommandParser.DirectionFromAngles(solution.Yaw, solution.Pitch);

        // The beam comes from the thrower's eye to the suggested aim direction.
        var beam = Assert.Single(harness.Visuals.Beams);
        Assert.Equal(Harness.Slot, beam.Slot);
        Assert.Equal(expectedOrigin, beam.Origin);
        Assert.Equal(expectedDirection, beam.Direction);
        // The contract pitch is measured up from the horizon, so an underhand arc
        // that has to clear the ground always looks up in contract terms.
        Assert.True(beam.Direction.Z > 0f, "A lobbed throw points above the horizon.");
        Assert.Equal(harness.Config.BeamDurationSeconds, beam.Duration, 3);

        // The cross marks the simulated impact, which has to sit in the marked zone.
        var marker = Assert.Single(harness.Visuals.Markers);
        Assert.Equal(solution.ImpactPosition, marker);
        Assert.True(session.Zone!.Contains(marker), $"The impact {marker} landed outside the zone.");
    }

    [Fact]
    public async Task Find_StillReports_WhenVisualizationIsDisabled()
    {
        using var harness = new Harness();
        harness.PrepareSession();
        harness.Visuals.Enabled = false;

        var outcome = await harness.Service.FindAsync(Harness.Slot, harness.World);

        Assert.NotNull(outcome);
        Assert.Equal(SolverStatus.Success, outcome!.Status);
        Assert.Empty(harness.Visuals.Beams);
        Assert.Empty(harness.Visuals.Markers);
        Assert.Contains(harness.Messages.Lines, line => line.Contains("#1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Find_TimesOut_WhenTheSolverIgnoresTheBudget()
    {
        // A solver that publishes candidates and then never stops: without the
        // race in the bridge this call would never return.
        const string configToml = "[solver]\ntimeoutSeconds = 0.3\n";
        using var harness = new Harness(configToml);
        harness.PrepareSession();
        harness.Bridge.UseCoreSolver(new BlackHoleSolver());

        var stopwatch = Stopwatch.StartNew();
        var outcome = await harness.Service.FindAsync(Harness.Slot, harness.World);
        stopwatch.Stop();

        Assert.NotNull(outcome);
        Assert.Equal(SolverStatus.Timeout, outcome!.Status);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"The 0.3s budget took {stopwatch.Elapsed.TotalSeconds:0.00}s.");
        Assert.Contains("timed out", harness.Messages.Last);
    }

    [Fact]
    public async Task Find_ReportsAFaultedSimulator_InsteadOfThrowing()
    {
        using var harness = new Harness();
        harness.PrepareSession();
        harness.Bridge.UseCoreSimulator(new StuckSimulator());

        var outcome = await harness.Service.FindAsync(Harness.Slot, harness.World);

        Assert.NotNull(outcome);
        Assert.Equal(SolverStatus.Failed, outcome!.Status);
        Assert.Contains("solver exploded", harness.Messages.Last);
    }

    [Fact]
    public async Task SolveAsync_FallsBackToThreeSeconds_WhenTheConfiguredBudgetIsUnusable()
    {
        var world = new GameWorldGeometry(new FakeTraceBackend());
        using var harness = new Harness();
        harness.PrepareSession();

        var request = harness.Service.BuildRequest(Harness.Slot);
        Assert.NotNull(request);

        // A zero budget would make every search fail instantly; the bridge treats it
        // as "unset" and uses the documented default instead.
        var bridge = new SimulatorBridge(() => TimeSpan.Zero);
        var outcome = await bridge.SolveAsync(request!, world);

        Assert.Equal(SolverStatus.Success, outcome.Status);
        Assert.NotEmpty(outcome.Results);
    }

    [Fact]
    public async Task Find_ReportsCancellation_InChat()
    {
        using var harness = new Harness();
        harness.PrepareSession();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var outcome = await harness.Service.FindAsync(Harness.Slot, harness.World, cancellation.Token);

        Assert.NotNull(outcome);
        Assert.Equal(SolverStatus.Cancelled, outcome!.Status);
        Assert.Contains("cancelled", harness.Messages.Last);
    }

    [Fact]
    public async Task Find_RefusesASecondSearch_ThenReleasesTheSlot()
    {
        using var harness = new Harness();
        harness.PrepareSession();

        var first = harness.Service.FindAsync(Harness.Slot, harness.World);
        var second = await harness.Service.FindAsync(Harness.Slot, harness.World);

        Assert.Null(second);
        Assert.Contains("already running", harness.Messages.Last);

        var firstOutcome = await first;
        Assert.NotNull(firstOutcome);

        // The slot is released in a finally block, so the player can search again.
        var third = await harness.Service.FindAsync(Harness.Slot, harness.World);
        Assert.NotNull(third);
        Assert.Equal(SolverStatus.Success, third!.Status);
    }

    [Fact]
    public async Task Find_ExplainsWhatIsMissing_BeforeItSearches()
    {
        using var harness = new Harness();

        var withoutAnything = await harness.Service.FindAsync(Harness.Slot, harness.World);
        Assert.Null(withoutAnything);
        Assert.Contains("css_lf_start", harness.Messages.Last);

        var session = harness.Sessions.GetOrCreate(Harness.Slot);
        session.MapName = harness.Map;
        session.ThrowPoint = new Vec3(0f, 0f, 0f);
        session.EyePoint = new Vec3(0f, 0f, 64f);

        var withoutZone = await harness.Service.FindAsync(Harness.Slot, harness.World);
        Assert.Null(withoutZone);
        Assert.Contains("css_lf_zone circle", harness.Messages.Last);
    }

    [Fact]
    public async Task Find_ExplainsOnAMapTheCatalogueExcludes()
    {
        using var harness = new Harness();
        harness.PrepareSession();
        harness.Service.MapEnabled = false;

        var outcome = await harness.Service.FindAsync(Harness.Slot, harness.World);

        Assert.Null(outcome);
        Assert.Contains("disabled on de_mirage", harness.Messages.Last);
    }

    [Fact]
    public void ReportSettings_NamesTheStub_UntilACoreSolverIsInstalled()
    {
        using var harness = new Harness();

        harness.Service.ReportSettings(Harness.Slot);
        Assert.Contains(harness.Messages.Lines, line => line.Contains("Candidates: 64", StringComparison.Ordinal));
        Assert.Contains(harness.Messages.Lines, line => line.Contains("built-in stub", StringComparison.Ordinal));

        harness.Bridge.UseCoreSolver(new StubLineupSolver());
        var before = harness.Messages.Lines.Count;
        harness.Service.ReportSettings(Harness.Slot);

        var fresh = harness.Messages.Lines.Skip(before).ToArray();
        Assert.NotEmpty(fresh);
        Assert.DoesNotContain(fresh, line => line.Contains("built-in stub", StringComparison.Ordinal));
    }
}
