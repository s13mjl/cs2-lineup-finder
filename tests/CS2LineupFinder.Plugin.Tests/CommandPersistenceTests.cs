using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Commands;
using CS2LineupFinder.Plugin.Persistence;
using Xunit;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// <c>css_lf_save</c>, <c>css_lf_load</c> and <c>css_lf_list</c> end to end: a
/// search, a save, a wiped session, a load, and everything the search needs is
/// back where it was.
/// </summary>
public sealed class CommandPersistenceTests
{
    [Fact]
    public async Task SaveThenLoad_Crouched_KeepsTheLowerEyeLine()
    {
        // The schema stores the feet, not the eyes, so the eye line has to be
        // reconstructed from the stance. Rebuilding a crouched line-up at the
        // standing height moves the beam origin 18 units up and the suggested arc
        // off the hands that actually threw it.
        using var harness = new Harness();
        harness.PrepareSession(eyeZ: 46f);
        harness.Sessions.GetOrCreate(Harness.Slot).ThrowMode = ThrowMode.Crouch;
        Assert.NotNull(await harness.Service.FindAsync(Harness.Slot, harness.World));

        Assert.NotNull(harness.Service.SaveLineup(Harness.Slot, "crouched"));
        harness.Service.Clear(Harness.Slot);

        var loaded = harness.Service.LoadLineup(Harness.Slot, "crouched", "de_mirage");

        Assert.NotNull(loaded);
        Assert.Equal(ThrowMode.Crouch, loaded!.ThrowMode);
        var session = harness.Sessions.GetOrCreate(Harness.Slot);
        Assert.Equal(LineupRecord.CrouchedEyeHeight, session.EyePoint!.Value.Z - session.ThrowPoint!.Value.Z);

        // The stance also survives the round trip, so the next search uses it too.
        var request = harness.Service.BuildRequest(Harness.Slot, explain: false);
        Assert.NotNull(request);
        Assert.Equal(ThrowMode.Crouch, request!.ThrowMode);
        Assert.Equal(LineupRecord.CrouchedEyeHeight, request.Origin.Eyes.Z - request.Origin.Feet.Z);
    }

    [Fact]
    public async Task SaveThenLoad_RestoresTheWholeSessionAndTheAngles()
    {
        using var harness = new Harness();
        var before = harness.PrepareSession();
        var outcome = await harness.Service.FindAsync(Harness.Slot, harness.World);
        Assert.NotNull(outcome);
        var solution = outcome!.Results[0];
        var zoneBefore = before.Zone!;

        var path = harness.Service.SaveLineup(Harness.Slot, "mirage window");
        Assert.NotNull(path);
        Assert.Contains("Saved line-up 'mirage_window'", harness.Messages.Last);

        // Wipe everything a load has to put back.
        harness.Service.Clear(Harness.Slot);
        var wiped = harness.Sessions.GetOrCreate(Harness.Slot);
        Assert.Null(wiped.ThrowPoint);
        Assert.Null(wiped.Zone);
        Assert.Null(wiped.LastOutcome);

        var visualsBefore = harness.Visuals.Beams.Count;
        var loaded = harness.Service.LoadLineup(Harness.Slot, "mirage_window", "de_mirage");

        Assert.NotNull(loaded);
        var session = harness.Sessions.GetOrCreate(Harness.Slot);
        Assert.Equal(before.ThrowPoint, session.ThrowPoint);
        Assert.Equal(before.EyePoint, session.EyePoint);
        Assert.Equal(before.GrenadeType, session.GrenadeType);
        Assert.Equal(before.ThrowMode, session.ThrowMode);
        Assert.Equal(before.Button, session.Button);
        Assert.True(session.MarkingMode);

        Assert.NotNull(session.Zone);
        Assert.Equal(GroundZoneType.Circle, session.Zone!.Type);
        Assert.Equal(zoneBefore.Radius, session.Zone.Radius);
        Assert.Equal(zoneBefore.Center.X, session.Zone.Center.X);
        Assert.Equal(zoneBefore.Center.Y, session.Zone.Center.Y);
        Assert.Equal(zoneBefore.Center.Z, session.Zone.Center.Z);

        // The stored angles become the current result, so css_lf_draw works right
        // after a load even though no search ran in this session.
        var restored = session.SelectedSolution;
        Assert.NotNull(restored);
        Assert.Equal(CommandParser.RoundAngle(solution.Yaw), restored!.Yaw);
        Assert.Equal(solution.Pitch, restored.Pitch);
        Assert.Contains("Loaded line-up 'mirage_window'", harness.Messages.Last);
        Assert.Contains($"pitch {CommandParser.RoundPlayerPitch(solution.Pitch):0.0}", harness.Messages.Last);

        // Loading redraws the zone and the aim beam.
        Assert.Equal(visualsBefore + 1, harness.Visuals.Beams.Count);
        Assert.NotEmpty(harness.Visuals.Zones);
    }

    [Fact]
    public async Task SavedFile_OnDisk_CarriesTheSchemaFromTheBrief()
    {
        using var harness = new Harness();
        harness.PrepareSession();
        await harness.Service.FindAsync(Harness.Slot, harness.World);
        harness.Service.SaveLineup(Harness.Slot, "window");

        var json = File.ReadAllText(harness.Lineups.GetPath("de_mirage", "window"));
        var record = LineupRepository.Deserialize(json);

        Assert.Equal("window", record.Name);
        Assert.Equal("de_mirage", record.Map);
        Assert.Equal("Tester", record.Author);
        Assert.Equal(GrenadeType.Smoke, record.GrenadeType);
        Assert.Equal(ThrowMode.Stand, record.ThrowMode);
        Assert.Equal(ThrowButton.Primary, record.Button);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero), record.CreatedAt);
        Assert.Equal(0f, record.Origin.X);
        Assert.Equal(0f, record.Origin.Z);
        Assert.Equal(500f, record.Zone.Center.X);
        Assert.Equal(120f, record.Zone.Radius);
        Assert.NotEqual(0f, record.Yaw + record.Pitch);
    }

    [Fact]
    public void Save_ExplainsABadName_AndAnEmptyResult()
    {
        using var harness = new Harness();
        harness.PrepareSession();

        Assert.Null(harness.Service.SaveLineup(Harness.Slot, ".."));
        Assert.Contains("Invalid name", harness.Messages.Last);

        // A name is fine now, but nothing has been solved yet.
        Assert.Null(harness.Service.SaveLineup(Harness.Slot, "window"));
        Assert.Contains("Nothing to save yet", harness.Messages.Last);
    }

    [Fact]
    public void Load_ReportsAMissingLineup_AndAnUnreadableOne()
    {
        using var harness = new Harness();
        harness.PrepareSession();

        Assert.Null(harness.Service.LoadLineup(Harness.Slot, "nope", "de_mirage"));
        Assert.Contains("not found on this map", harness.Messages.Last);

        Directory.CreateDirectory(harness.Lineups.GetMapDirectory("de_mirage"));
        File.WriteAllText(harness.Lineups.GetPath("de_mirage", "broken"), "{ not json");

        Assert.Null(harness.Service.LoadLineup(Harness.Slot, "broken", "de_mirage"));
        Assert.Contains("Could not read the line-up", harness.Messages.Last);
        Assert.NotEmpty(harness.Messages.Warnings);
    }

    [Fact]
    public async Task List_PrintsEveryRecordForTheMap()
    {
        using var harness = new Harness();
        harness.PrepareSession();
        await harness.Service.FindAsync(Harness.Slot, harness.World);
        harness.Service.SaveLineup(Harness.Slot, "alpha");
        harness.Service.SaveLineup(Harness.Slot, "bravo");

        var records = harness.Service.ListLineups(Harness.Slot, "de_mirage");

        Assert.Equal(2, records.Count);
        var lines = harness.Messages.Lines;
        Assert.Contains(lines, line => line.Contains("2 line-up(s) saved on de_mirage", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("  alpha  (", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("smoke stand/primary", StringComparison.Ordinal));
    }

    [Fact]
    public void List_OnAMapWithNoLineups_SaysZeroAndStops()
    {
        using var harness = new Harness();

        var records = harness.Service.ListLineups(Harness.Slot, "de_vertigo");

        Assert.Empty(records);
        Assert.Single(harness.Messages.Lines);
        Assert.Contains("0 line-up(s) saved on de_vertigo", harness.Messages.Lines.Single());
    }

    [Fact]
    public async Task Save_UsesTheSessionsMap_NotTheLiveServerOne()
    {
        using var harness = new Harness();
        harness.PrepareSession();
        await harness.Service.FindAsync(Harness.Slot, harness.World);
        harness.SetMap("de_ancient");

        harness.Service.SaveLineup(Harness.Slot, "roundtrip");

        // The session still remembers where the line-up was recorded, so the file
        // does not follow the server onto the next map of the rotation.
        Assert.True(File.Exists(harness.Lineups.GetPath("de_ancient", "roundtrip")));
        Assert.Equal("de_ancient", harness.Lineups.Load("de_ancient", "roundtrip")!.Map);
    }
}
