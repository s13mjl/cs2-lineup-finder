using System;
using System.Text.Json.Serialization;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Persistence;

/// <summary>
/// On-disk shape of one saved line-up, serialized to
/// <c>data/lineups/&lt;name&gt;.json</c>.
/// </summary>
/// <remarks>
/// The schema is frozen by the task brief:
/// <c>{ name, map, grenadeType, throwMode, button, origin, zone, yaw, pitch, createdAt, author }</c>.
/// Enums are written as readable strings rather than integers so a saved file stays
/// diffable and survives an enum reordering.
/// </remarks>
public sealed class LineupRecord
{
    /// <summary>Player facing name of the line-up, also the file name stem.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Map the line-up was recorded on, e.g. <c>de_mirage</c>.</summary>
    [JsonPropertyName("map")]
    public string Map { get; set; } = string.Empty;

    /// <summary>Grenade family, serialized as <c>smoke</c> / <c>flash</c> / <c>molotov</c> / <c>he</c>.</summary>
    [JsonPropertyName("grenadeType")]
    [JsonConverter(typeof(JsonStringEnumConverter<GrenadeType>))]
    public GrenadeType GrenadeType { get; set; }

    /// <summary>Stance the throw is executed from, serialized as <c>stand</c> / <c>crouch</c> / <c>jump</c>.</summary>
    [JsonPropertyName("throwMode")]
    [JsonConverter(typeof(JsonStringEnumConverter<ThrowMode>))]
    public ThrowMode ThrowMode { get; set; }

    /// <summary>Mouse button combination, serialized as <c>primary</c> / <c>secondary</c> / <c>both</c>.</summary>
    [JsonPropertyName("button")]
    [JsonConverter(typeof(JsonStringEnumConverter<ThrowButton>))]
    public ThrowButton Button { get; set; }

    /// <summary>Where the player stood when the line-up was found.</summary>
    [JsonPropertyName("origin")]
    public LineupVector Origin { get; set; } = new();

    /// <summary>Landing area the grenade has to reach.</summary>
    [JsonPropertyName("zone")]
    public LineupZoneRecord Zone { get; set; } = new();

    /// <summary>Suggested view yaw in degrees, normalized to -180..180.</summary>
    [JsonPropertyName("yaw")]
    public float Yaw { get; set; }

    /// <summary>
    /// Suggested view pitch in degrees, in the contract convention where positive
    /// looks up. Chat prints its mirror image (see
    /// <c>CommandParser.RoundPlayerPitch</c>) because players read pitch in the
    /// engine convention, where positive looks down.
    /// </summary>
    [JsonPropertyName("pitch")]
    public float Pitch { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Steam persona name of the player who saved the line-up.</summary>
    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    /// <summary>Builds a record from a session and one solver result.</summary>
    /// <param name="name">Line-up name.</param>
    /// <param name="mapName">Current map.</param>
    /// <param name="session">Session holding the origin, zone and throw parameters.</param>
    /// <param name="solution">Solution being stored.</param>
    /// <param name="createdAt">Creation timestamp, UTC.</param>
    /// <returns>The populated record.</returns>
    public static LineupRecord FromSolution(
        string name,
        string mapName,
        State.PlayerSession session,
        LineupSolution solution,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(solution);

        return new LineupRecord
        {
            Name = name,
            Map = mapName,
            GrenadeType = session.GrenadeType,
            ThrowMode = session.ThrowMode,
            Button = session.Button,
            Origin = LineupVector.From(session.ThrowPoint ?? solution.ReleasePosition),
            Zone = LineupZoneRecord.From(session.Zone ?? new GroundZone { Type = GroundZoneType.Circle, Center = solution.ImpactPosition }),
            Yaw = solution.Yaw,
            Pitch = solution.Pitch,
            CreatedAt = createdAt,
            Author = session.PlayerName,
        };
    }

    /// <summary>Converts the record back into a solver request.</summary>
    /// <param name="mapName">Map to fall back to when the record has none.</param>
    /// <returns>The request, or <see langword="null"/> when origin or zone is degenerate.</returns>
    public LineupRequest? ToRequest(string mapName)
    {
        var origin = new ThrowOrigin { Feet = Origin.ToVec3(), Eyes = Origin.ToVec3() + new Vec3(0f, 0f, DefaultEyeHeight) };
        var zone = Zone.ToGroundZone();
        if (origin.Feet.IsZero(0.01f) || zone.Center.IsZero(0.01f))
        {
            return null;
        }

        return new LineupRequest
        {
            MapName = string.IsNullOrWhiteSpace(Map) ? mapName : Map,
            GrenadeType = GrenadeType,
            ThrowMode = ThrowMode,
            Button = Button,
            Origin = origin,
            TargetZone = zone,
        };
    }

    /// <summary>Eye height assumed when a stored record has to be turned back into a request.</summary>
    public const float DefaultEyeHeight = 64f;
}

/// <summary>Plain x/y/z triple so the JSON stays readable.</summary>
public sealed class LineupVector
{
    /// <summary>X component.</summary>
    [JsonPropertyName("x")]
    public float X { get; set; }

    /// <summary>Y component.</summary>
    [JsonPropertyName("y")]
    public float Y { get; set; }

    /// <summary>Z component.</summary>
    [JsonPropertyName("z")]
    public float Z { get; set; }

    /// <summary>Converts to the contract vector.</summary>
    /// <returns>The same point as a <see cref="Vec3"/>.</returns>
    public Vec3 ToVec3() => new(X, Y, Z);

    /// <summary>Creates the JSON form of a contract vector.</summary>
    /// <param name="value">Vector to convert.</param>
    /// <returns>The plain triple.</returns>
    public static LineupVector From(Vec3 value) => new() { X = value.X, Y = value.Y, Z = value.Z };
}

/// <summary>On-disk shape of a landing zone.</summary>
public sealed class LineupZoneRecord
{
    /// <summary>Zone shape, <c>circle</c> or <c>rect</c>.</summary>
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter<GroundZoneType>))]
    public GroundZoneType Type { get; set; } = GroundZoneType.Circle;

    /// <summary>Radius of a circular zone.</summary>
    [JsonPropertyName("radius")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Radius { get; set; }

    /// <summary>Width of a rectangular zone.</summary>
    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Width { get; set; }

    /// <summary>Height of a rectangular zone.</summary>
    [JsonPropertyName("height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Height { get; set; }

    /// <summary>In-plane rotation of a rectangular zone in degrees.</summary>
    [JsonPropertyName("yaw")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Yaw { get; set; }

    /// <summary>Zone centre.</summary>
    [JsonPropertyName("center")]
    public LineupVector Center { get; set; } = new();

    /// <summary>Creates the JSON form of a contract zone.</summary>
    /// <param name="zone">Zone to convert.</param>
    /// <returns>The plain record.</returns>
    public static LineupZoneRecord From(GroundZone zone) => new()
    {
        Type = zone.Type,
        Radius = zone.Radius,
        Width = zone.Width,
        Height = zone.Height,
        Yaw = zone.Yaw,
        Center = LineupVector.From(zone.Center),
    };

    /// <summary>Converts to the contract zone.</summary>
    /// <returns>The same area as a <see cref="GroundZone"/>.</returns>
    public GroundZone ToGroundZone() => new()
    {
        Type = Type,
        Center = Center.ToVec3(),
        Radius = Radius,
        Width = Width,
        Height = Height,
        Yaw = Yaw,
    };
}
