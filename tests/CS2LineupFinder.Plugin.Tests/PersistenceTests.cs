using System;
using System.IO;
using System.Linq;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Persistence;
using Xunit;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// The frozen on-disk schema of <c>data/lineups/&lt;map&gt;/&lt;name&gt;.json</c> and the
/// round trip the brief calls lossless.
/// </summary>
public sealed class PersistenceTests
{
    private static LineupRecord Sample(string name = "mirage_window", string map = "de_mirage") => new()
    {
        Name = name,
        Map = map,
        GrenadeType = GrenadeType.Smoke,
        ThrowMode = ThrowMode.Jump,
        Button = ThrowButton.Both,
        Origin = new LineupVector { X = 123.5f, Y = -456.25f, Z = 0f },
        Zone = new LineupZoneRecord
        {
            Type = GroundZoneType.Circle,
            Radius = 96f,
            Center = new LineupVector { X = 1500f, Y = 0f, Z = 0f },
        },
        Yaw = -127.5f,
        Pitch = 12.25f,
        CreatedAt = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
        Author = "Tester",
    };

    [Fact]
    public void SaveThenLoad_ReturnsEveryFieldUnchanged()
    {
        using var harness = new Harness();
        var original = Sample();

        var path = harness.Lineups.Save(original);
        var loaded = harness.Lineups.Load(original.Map, original.Name);

        Assert.True(File.Exists(path));
        Assert.EndsWith("data/lineups/de_mirage/mirage_window.json", path.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.NotNull(loaded);
        Assert.Equal(original.Name, loaded!.Name);
        Assert.Equal(original.Map, loaded.Map);
        Assert.Equal(original.GrenadeType, loaded.GrenadeType);
        Assert.Equal(original.ThrowMode, loaded.ThrowMode);
        Assert.Equal(original.Button, loaded.Button);
        Assert.Equal(original.Origin.X, loaded.Origin.X);
        Assert.Equal(original.Origin.Y, loaded.Origin.Y);
        Assert.Equal(original.Origin.Z, loaded.Origin.Z);
        Assert.Equal(original.Zone.Type, loaded.Zone.Type);
        Assert.Equal(original.Zone.Radius, loaded.Zone.Radius);
        Assert.Equal(original.Zone.Center.X, loaded.Zone.Center.X);
        Assert.Equal(original.Zone.Center.Y, loaded.Zone.Center.Y);
        Assert.Equal(original.Zone.Center.Z, loaded.Zone.Center.Z);
        Assert.Equal(original.Yaw, loaded.Yaw);
        Assert.Equal(original.Pitch, loaded.Pitch);
        Assert.Equal(original.CreatedAt, loaded.CreatedAt);
        Assert.Equal(original.Author, loaded.Author);
    }

    [Fact]
    public void RectangularZone_KeepsItsExtentsAndRotation()
    {
        using var harness = new Harness();
        var original = Sample("mirage_rect");
        original.Zone = new LineupZoneRecord
        {
            Type = GroundZoneType.Rectangle,
            Width = 256f,
            Height = 128f,
            Yaw = 35f,
            Center = new LineupVector { X = 900f, Y = -250f, Z = 64f },
        };

        harness.Lineups.Save(original);
        var loaded = harness.Lineups.Load(original.Map, original.Name);

        Assert.NotNull(loaded);
        Assert.Equal(GroundZoneType.Rectangle, loaded!.Zone.Type);
        Assert.Equal(256f, loaded.Zone.Width);
        Assert.Equal(128f, loaded.Zone.Height);
        Assert.Equal(35f, loaded.Zone.Yaw);
        Assert.Equal(900f, loaded.Zone.Center.X);
        Assert.Equal(-250f, loaded.Zone.Center.Y);
        Assert.Equal(64f, loaded.Zone.Center.Z);

        // The zero valued members are omitted, so a circular zone does not carry
        // four meaningless keys and a rectangular one does not carry a radius.
        var json = File.ReadAllText(harness.Lineups.GetPath(original.Map, original.Name));
        Assert.Contains("\"width\": 256", json);
        Assert.DoesNotContain("\"radius\"", json);
    }

    [Fact]
    public void Serialize_WritesTheSchemaTheBriefFreezes()
    {
        var json = LineupRepository.Serialize(Sample());

        foreach (var key in new[] { "name", "map", "grenadeType", "throwMode", "button", "origin", "zone", "yaw", "pitch", "createdAt", "author" })
        {
            Assert.Contains($"\"{key}\"", json);
        }

        // Enums are readable words, not ordinals: a saved file has to survive an
        // enum reordering and stay diffable in review.
        Assert.Contains("\"grenadeType\": \"Smoke\"", json);
        Assert.Contains("\"throwMode\": \"Jump\"", json);
        Assert.Contains("\"button\": \"Both\"", json);
    }

    [Fact]
    public void Deserialize_RoundTripsTheSerializedText()
    {
        var original = Sample();
        var copy = LineupRepository.Deserialize(LineupRepository.Serialize(original));

        Assert.Equal(original.Zone.Type, copy.Zone.Type);
        Assert.Equal(original.Zone.Radius, copy.Zone.Radius);
        Assert.Equal(original.Yaw, copy.Yaw);
        Assert.Equal(original.Pitch, copy.Pitch);
        Assert.Equal(LineupRepository.Serialize(original), LineupRepository.Serialize(copy));
    }

    [Fact]
    public void Deserialize_RejectsTextThatIsNotALineup()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => LineupRepository.Deserialize("not json at all"));
        Assert.Throws<System.Text.Json.JsonException>(() => LineupRepository.Deserialize("   "));
    }

    [Theory]
    [InlineData("..", "")]
    [InlineData("../secrets", "secrets")]
    [InlineData("..\\..\\windows", "windows")]
    [InlineData("a/b", "ab")]
    [InlineData("a\\b", "ab")]
    [InlineData("mirage window", "mirage_window")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(".hidden.", "hidden")]
    public void Sanitize_KeepsNamesInsideTheDataDirectory(string raw, string expected) =>
        Assert.Equal(expected, LineupRepository.Sanitize(raw));

    [Fact]
    public void Sanitize_StripsEveryCharacterTheFileSystemForbids()
    {
        // The platform list is used rather than a hard coded set, so the test says the
        // same thing on the Windows CI runner and on a Linux developer machine.
        var invalid = Path.GetInvalidFileNameChars().Where(c => c is not ('/' or '\\')).ToArray();
        var raw = "a" + new string(invalid) + "b";

        Assert.Equal("ab", LineupRepository.Sanitize(raw));
    }

    [Fact]
    public void GetPath_StaysUnderTheMapDirectory_ForAHostileName()
    {
        using var harness = new Harness();

        var path = Path.GetFullPath(harness.Lineups.GetPath("de_mirage", "..\\..\\evil"));
        var directory = Path.GetFullPath(harness.Lineups.GetMapDirectory("de_mirage"));

        Assert.StartsWith(directory, path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void List_ReturnsRecordsOrderedByName_AndSkipsACorruptFile()
    {
        using var harness = new Harness();
        harness.Lineups.Save(Sample("charlie"));
        harness.Lineups.Save(Sample("alpha"));
        harness.Lineups.Save(Sample("bravo"));
        File.WriteAllText(Path.Combine(harness.Lineups.GetMapDirectory("de_mirage"), "broken.json"), "{ half a file");

        var names = harness.Lineups.List("de_mirage").Select(record => record.Name).ToArray();

        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, names);
    }

    [Fact]
    public void LoadAndList_ReturnNullOrNothing_ForAMapWithNoDirectory()
    {
        using var harness = new Harness();

        Assert.Null(harness.Lineups.Load("de_vertigo", "anything"));
        Assert.Empty(harness.Lineups.List("de_vertigo"));
    }

    [Fact]
    public void Delete_ReportsWhetherAFileWasRemoved()
    {
        using var harness = new Harness();
        harness.Lineups.Save(Sample());

        Assert.True(harness.Lineups.Delete("de_mirage", "mirage_window"));
        Assert.False(harness.Lineups.Delete("de_mirage", "mirage_window"));
        Assert.Null(harness.Lineups.Load("de_mirage", "mirage_window"));
    }

    [Fact]
    public void Save_RefusesAnEmptyNameOrMap()
    {
        using var harness = new Harness();
        var record = Sample("..", "");

        Assert.Throws<ArgumentException>(() => harness.Lineups.Save(record));

        record = Sample();
        record.Map = "  ";
        Assert.Throws<ArgumentException>(() => harness.Lineups.Save(record));
    }

    [Fact]
    public void Save_NormalizesTheName_SoTheFileAndTheRecordAgree()
    {
        using var harness = new Harness();
        var record = Sample("mirage window");

        var path = harness.Lineups.Save(record);

        Assert.Equal("mirage_window", record.Name);
        Assert.Equal("mirage_window.json", Path.GetFileName(path));
        // Saving the same line-up twice overwrites rather than duplicating.
        Assert.Equal(path, harness.Lineups.Save(Sample("mirage_window")));
        Assert.Single(harness.Lineups.List("de_mirage"));
    }

    [Fact]
    public void FormatDistance_ShowsOneDecimal()
    {
        Assert.Equal("12.3u", LineupRepository.FormatDistance(12.34f));
        Assert.Equal("0.0u", LineupRepository.FormatDistance(0f));
    }

    [Fact]
    public void Record_ConvertsBackIntoARequest_EvenAtTheWorldOrigin()
    {
        var request = Sample().ToRequest("de_dust2");

        Assert.NotNull(request);
        Assert.Equal("de_mirage", request!.MapName);
        Assert.Equal(123.5f, request.Origin.Feet.X);
        Assert.Equal(LineupRecord.DefaultEyeHeight, request.Origin.Eyes.Z - request.Origin.Feet.Z);
        Assert.Equal(1500f, request.TargetZone!.Center.X);

        // The world origin is a legal throw point, so a record sitting there still
        // converts: a search started from (0, 0, 0) has to work like any other.
        var atOrigin = Sample();
        atOrigin.Origin = new LineupVector();
        var originRequest = atOrigin.ToRequest("de_mirage");
        Assert.NotNull(originRequest);
        Assert.Equal(LineupRecord.DefaultEyeHeight, originRequest!.Origin.Eyes.Z);
    }
}
