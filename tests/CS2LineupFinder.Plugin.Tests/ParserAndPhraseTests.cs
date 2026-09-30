using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Commands;
using Xunit;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// Grammar and text that the engine never has to be running for: argument
/// parsing, angle formatting and the localized reply table.
/// </summary>
public sealed class ParserAndPhraseTests
{
    private static GroundZone Zone(params string[] arguments)
    {
        var parsed = CommandParser.TryParseZone(arguments, 128f, out var zone, out var error);
        Assert.True(parsed, $"Failed to parse '{string.Join(' ', arguments)}': {error}");
        Assert.Null(error);
        return Assert.IsType<GroundZone>(zone);
    }

    [Fact]
    public void CircleZone_AcceptsEverySpelling_AndFallsBackToTheConfiguredRadius()
    {
        foreach (var keyword in new[] { "circle", "c", "r", "radius", "CIRCLE" })
        {
            var zone = Zone(keyword, "250");
            Assert.Equal(GroundZoneType.Circle, zone.Type);
            Assert.Equal(250f, zone.Radius);
        }

        Assert.Equal(128f, Zone("circle").Radius);
    }

    [Fact]
    public void RectZone_AcceptsASquare_AndAWidthHeightPair()
    {
        var square = Zone("rect", "300");
        Assert.Equal(GroundZoneType.Rectangle, square.Type);
        Assert.Equal(300f, square.Width);
        Assert.Equal(300f, square.Height);

        var rectangle = Zone("rectangle", "300", "120");
        Assert.Equal(300f, rectangle.Width);
        Assert.Equal(120f, rectangle.Height);
    }

    [Fact]
    public void ZoneGrammar_ExplainsItself()
    {
        Assert.False(CommandParser.TryParseZone(Array.Empty<string>(), 128f, out var none, out var missing));
        Assert.Null(none);
        Assert.Equal("zone circle <radius> | zone rect <width> <height>", missing);

        Assert.False(CommandParser.TryParseZone(new[] { "blob", "10" }, 128f, out var shape, out var unknown));
        Assert.Null(shape);
        Assert.Equal("blob", unknown);

        Assert.False(CommandParser.TryParseZone(new[] { "rect" }, 128f, out _, out var noExtent));
        Assert.Equal("zone rect <width> <height>", noExtent);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-12")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("")]
    public void ZoneGrammar_RejectsAnImpossibleRadius_WithoutAnErrorString(string radius)
    {
        // A null error means "the argument was there, but its value is out of range",
        // which is what lets the caller print the radius specific message.
        Assert.False(CommandParser.TryParseZone(new[] { "circle", radius }, 128f, out var zone, out var error));
        Assert.Null(zone);
        Assert.Null(error);
    }

    [Fact]
    public void ParsePositive_UsesTheInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // A German locale writes 128.5 as 128,5; the command grammar must not.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(128.5f, CommandParser.ParsePositive("128.5"));
            Assert.Null(CommandParser.ParsePositive("128,5"));
            Assert.Null(CommandParser.ParsePositive("   "));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(190f, -170f)]
    [InlineData(-190f, 170f)]
    [InlineData(360f, 0f)]
    [InlineData(-540f, -180f)]
    [InlineData(-127.5f, -127.5f)]
    public void NormalizeYaw_WrapsIntoTheContractRange(float raw, float expected) =>
        Assert.Equal(expected, CommandParser.NormalizeYaw(raw));

    [Theory]
    [InlineData(12.34f, 12.3f)]
    [InlineData(12.35f, 12.4f)]
    [InlineData(-12.34f, -12.3f)]
    [InlineData(0f, 0f)]
    public void RoundAngle_KeepsOneDecimal_AwayFromZero(float raw, float expected) =>
        Assert.Equal(expected, CommandParser.RoundAngle(raw));

    [Fact]
    public void PlayerPitch_IsTheMirrorOfTheContractPitch()
    {
        Assert.Equal(-12.4f, CommandParser.RoundPlayerPitch(12.35f));
        Assert.Equal(12.4f, CommandParser.RoundPlayerPitch(-12.35f));
        Assert.Equal(-8f, CommandParser.ToPlayerPitch(8f));
        Assert.Equal(8f, CommandParser.FromPlayerPitch(-8f));
    }

    [Fact]
    public void DirectionFromAngles_PointsWhereTheContractSaysItDoes()
    {
        var straight = CommandParser.DirectionFromAngles(0f, 0f);
        Assert.Equal(1f, straight.X, 4);
        Assert.Equal(0f, straight.Y, 4);
        Assert.Equal(0f, straight.Z, 4);

        // Positive yaw turns towards +Y, positive contract pitch looks up.
        var turned = CommandParser.DirectionFromAngles(90f, 0f);
        Assert.Equal(0f, turned.X, 4);
        Assert.Equal(1f, turned.Y, 4);

        var raised = CommandParser.DirectionFromAngles(0f, 90f);
        Assert.Equal(1f, raised.Z, 4);

        var lowered = CommandParser.DirectionFromAngles(0f, -90f);
        Assert.Equal(-1f, lowered.Z, 4);

        Assert.Equal(1f, straight.Length, 4);
        // The player convention is the contract convention with the pitch mirrored.
        Assert.Equal(lowered, CommandParser.DirectionFromPlayerAngles(0f, 90f));
    }

    [Fact]
    public void FormatPoint_AndFormatUnits_UseOneDecimal()
    {
        Assert.Equal("(0, 0, 64)", CommandParser.FormatPoint(new Vec3(0f, 0f, 64f)));
        // Points are rounded to whole units, which is the precision players read.
        Assert.Equal("(13, -3, 8)", CommandParser.FormatPoint(new Vec3(12.5f, -3.25f, 8f)));
        Assert.Equal("12.3u", CommandParser.FormatUnits(12.34f));
        Assert.Equal("yaw -127.5", CommandParser.FormatAngle("yaw", -127.45f));
    }

    [Theory]
    [InlineData("!lf_zone r 200", "lf_zone")]
    [InlineData("/lf_menu", "lf_menu")]
    [InlineData(".lf_find", "lf_find")]
    [InlineData("  !lf_save  window ", "lf_save")]
    [InlineData("css_lf_find", "css_lf_find")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void CommandNameFromChat_ReadsWhatAPlayerTyped(string message, string expected) =>
        Assert.Equal(expected, CommandUsage.CommandNameFromChat(message));

    [Theory]
    [InlineData("smoke", GrenadeType.Smoke)]
    [InlineData("FLASH", GrenadeType.Flash)]
    [InlineData("flashbang", GrenadeType.Flash)]
    [InlineData("molo", GrenadeType.Molotov)]
    [InlineData("frag", GrenadeType.He)]
    [InlineData(" stand ", ThrowMode.Stand)]
    [InlineData("crouching", ThrowMode.Crouch)]
    [InlineData("jumping", ThrowMode.Jump)]
    [InlineData("lmb", ThrowButton.Primary)]
    [InlineData("right", ThrowButton.Secondary)]
    [InlineData("both", ThrowButton.Both)]
    public void Tokens_InverseRoundTrip(string token, object expected)
    {
        switch (expected)
        {
            case GrenadeType grenade:
                Assert.True(CommandUsage.TryParseGrenadeType(token, out var parsedGrenade));
                Assert.Equal(grenade, parsedGrenade);
                // The canonical token parses back to the same family, whatever alias
                // the player used, so chat output can always be retyped.
                Assert.True(CommandUsage.TryParseGrenadeType(CommandUsage.Token(grenade), out var again));
                Assert.Equal(grenade, again);
                break;
            case ThrowMode mode:
                Assert.True(CommandUsage.TryParseThrowMode(token, out var parsedMode));
                Assert.Equal(mode, parsedMode);
                Assert.True(CommandUsage.TryParseThrowMode(CommandUsage.Token(mode), out var modeAgain));
                Assert.Equal(mode, modeAgain);
                break;
            default:
                Assert.True(CommandUsage.TryParseThrowButton(token, out var parsedButton));
                Assert.Equal((ThrowButton)expected, parsedButton);
                Assert.True(CommandUsage.TryParseThrowButton(CommandUsage.Token(parsedButton), out var buttonAgain));
                Assert.Equal(parsedButton, buttonAgain);
                break;
        }
    }

    [Theory]
    [InlineData("nuke")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownTokens_AreRejected(string? token)
    {
        Assert.False(CommandUsage.TryParseGrenadeType(token, out _));
        Assert.False(CommandUsage.TryParseThrowMode(token, out _));
        Assert.False(CommandUsage.TryParseThrowButton(token, out _));
    }

    [Fact]
    public void UsageFor_NamesTheCommand_APlayerCanPasteIntoChat()
    {
        Assert.Equal("!lf_zone circle <radius> | rect <width> <height>", CommandUsage.UsageFor("css_lf_zone"));
        Assert.Equal("!lf_grenade smoke | flash | molotov | he", CommandUsage.UsageFor("CSS_LF_GRENADE"));
        Assert.Equal(string.Empty, CommandUsage.UsageFor("css_lf_nope"));
        Assert.Equal("lf_save <name>", CommandSpec.Find("css_lf_save")!.Value.FullUsage);
        Assert.Equal("!lf_save <name>", CommandUsage.UsageFor("css_lf_save"));
    }

    [Fact]
    public void DescribeSession_ShowsWhatIsSet()
    {
        var session = new CS2LineupFinder.Plugin.State.PlayerSession(7);
        Assert.Equal("origin unset | zone unset | smoke | stand | primary", Phrases.DescribeSession(session, "en"));

        session.ThrowPoint = new Vec3(0f, 0f, 0f);
        session.Zone = new GroundZone { Type = GroundZoneType.Circle, Center = new Vec3(100f, 20f, 0f), Radius = 128f };
        session.GrenadeType = GrenadeType.Flash;
        session.ThrowMode = ThrowMode.Jump;
        session.Button = ThrowButton.Both;

        Assert.Equal("origin set | zone circle r=128u @ (100, 20, 0) | flash | jump | both", Phrases.DescribeSession(session, "en"));
    }

    [Fact]
    public void Phrases_AreComplete_InBothLanguages()
    {
        // Id 0 is the prefix template and every other index is a named constant, so
        // walking the table proves the two languages stay the same length.
        for (var id = 0; id <= Phrases.Id.Usage; id++)
        {
            var english = Phrases.Get("en", id, "A", "B", "C", "D");
            var chinese = Phrases.Get("zh-CN", id, "A", "B", "C", "D");

            Assert.False(string.IsNullOrWhiteSpace(english), $"English entry {id} is empty.");
            Assert.False(string.IsNullOrWhiteSpace(chinese), $"Chinese entry {id} is empty.");
            // Compare the raw templates, not the formatted text: a purely structural
            // entry such as "  {0}  ({1})" is identical in both languages, and its
            // formatted form would otherwise look translated because the arguments
            // happen to be Latin letters. Get() with no arguments falls back to the
            // template, so it is the honest view of what each language actually holds.
            var englishTemplate = Phrases.Get("en", id);
            var chineseTemplate = Phrases.Get("zh-CN", id);
            if (englishTemplate.Any(char.IsAsciiLetter))
            {
                Assert.NotEqual(englishTemplate, chineseTemplate);
            }
        }
    }

    [Fact]
    public void Phrases_SurviveAnUnknownIndexOrLanguage()
    {
        Assert.Equal("#999", Phrases.Get("en", 999));
        Assert.Equal("#-1", Phrases.Get("en", -1));
        Assert.Equal(Phrases.Get("en", Phrases.Id.Found, 1, 0.5, "stub"), Phrases.Get("fr", Phrases.Id.Found, 1, 0.5, "stub"));
        Assert.True(Phrases.IsChinese("zh"));
        Assert.True(Phrases.IsChinese("zh-TW"));
        Assert.False(Phrases.IsChinese("en"));
        Assert.False(Phrases.IsChinese(null));
    }

    [Fact]
    public void Phrases_PrefixEveryReply()
    {
        var line = Phrases.Prefixed("en", Phrases.Id.Found, 2, 1.5, "stub");
        Assert.StartsWith("[LineupFinder] ", line, StringComparison.Ordinal);
        Assert.Contains("Found 2 line-up(s) in 1.5s (solver: stub).", line);

        var chinese = Phrases.Prefixed("zh-CN", Phrases.Id.Found, 1, 0.2, "stub");
        Assert.StartsWith("[瞄点预测] ", chinese, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryNamedMessageId_IsInRange()
    {
        var ids = typeof(Phrases.Id)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (field.Name, Value: (int)field.GetRawConstantValue()!))
            .ToArray();

        Assert.NotEmpty(ids);
        foreach (var (name, value) in ids)
        {
            Assert.InRange(value, 1, Phrases.Id.Usage);
            Assert.False(string.IsNullOrWhiteSpace(Phrases.Get("en", value)), $"English entry for {name} is empty.");
            Assert.False(string.IsNullOrWhiteSpace(Phrases.Get("zh-CN", value)), $"Chinese entry for {name} is empty.");
        }

        Assert.Equal(ids.Length, ids.Select(entry => entry.Value).Distinct().Count());
        foreach (var number in Enumerable.Range(1, Phrases.Id.Usage))
        {
            Assert.Contains(ids, entry => entry.Value == number);
        }
    }
}
