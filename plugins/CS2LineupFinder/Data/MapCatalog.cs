using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2LineupFinder.Plugin.Commands;

namespace CS2LineupFinder.Plugin.Data;

/// <summary>
/// The map point catalogue that lives in <c>plugins/CS2LineupFinder/data/</c>.
/// </summary>
/// <remarks>
/// Two jobs, both driven by files an operator can edit without touching the DLL:
/// which map the plugin is willing to solve on, and which named points on that map
/// a future UI can offer. Nothing here is required - a server that never creates
/// the folder still gets every command, because the catalogue degrades to the map
/// the server is running and an empty point list.
///
/// Loading is deliberately tolerant. A malformed file is reported once at map
/// start and then ignored: a practice server must not fail to load a plugin because
/// of a stray comma in a hand-written JSON file.
/// </remarks>
public sealed class MapCatalog
{
    /// <summary>Relative path of the optional map list inside the plugin directory.</summary>
    public const string MapsFile = "data/maps.json";

    /// <summary>Relative path of the optional point catalogue inside the plugin directory.</summary>
    public const string PointsFile = "data/points.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>Named points per map, keyed case-insensitively.</summary>
    private Dictionary<string, MapPoints> _points = new(StringComparer.OrdinalIgnoreCase);

    private MapCatalog(string directory) => Directory = directory;

    /// <summary>Plugin root the catalogue was read from, for diagnostics.</summary>
    public string Directory { get; }

    /// <summary>Maps listed in <c>data/maps.json</c>; empty means every map is allowed.</summary>
    public IReadOnlyList<string> Maps { get; private init; } = Array.Empty<string>();

    /// <summary>Files that could not be read, reported once at map start.</summary>
    public IReadOnlyList<string> Problems { get; private init; } = Array.Empty<string>();

    /// <summary>True when the operator restricted the plugin to a fixed map list.</summary>
    public bool HasMapList => Maps.Count > 0;

    /// <summary>
    /// Reads the catalogue. Never throws: a failure is recorded in
    /// <see cref="Problems"/> so the plugin can log it and carry on with defaults.
    /// </summary>
    /// <param name="pluginRootDirectory">Directory holding <c>config/</c> and <c>data/</c>.</param>
    /// <returns>The loaded catalogue, possibly empty.</returns>
    public static MapCatalog Load(string pluginRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRootDirectory);

        var problems = new List<string>();
        var maps = ReadMaps(pluginRootDirectory, problems);
        var points = new Dictionary<string, MapPoints>(StringComparer.OrdinalIgnoreCase);
        ReadPoints(pluginRootDirectory, points, problems);

        return new MapCatalog(pluginRootDirectory)
        {
            Maps = maps,
            Problems = problems.ToArray(),
            _points = points,
        };
    }

    /// <summary>Whether the plugin should solve on a map.</summary>
    /// <param name="mapName">Engine map name, e.g. <c>de_mirage</c>.</param>
    /// <returns><see langword="true"/> when no list is configured or the map is on it.</returns>
    public bool IsEnabled(string? mapName)
    {
        if (!HasMapList || string.IsNullOrWhiteSpace(mapName))
        {
            return !HasMapList;
        }

        foreach (var map in Maps)
        {
            if (string.Equals(map, mapName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Named points recorded for a map, empty when the map has none.</summary>
    /// <param name="mapName">Engine map name.</param>
    /// <returns>The points, never <see langword="null"/>.</returns>
    public IReadOnlyList<MapPoint> PointsFor(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            return Array.Empty<MapPoint>();
        }

        return _points.TryGetValue(mapName, out var entry) ? entry.Points : Array.Empty<MapPoint>();
    }

    /// <summary>One line describing the catalogue, for the map start log entry.</summary>
    /// <returns>A short summary.</returns>
    public string Describe()
    {
        var maps = HasMapList ? Maps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "all";
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"maps={maps} pointMaps={_points.Count} problems={Problems.Count}");
    }

    private static IReadOnlyList<string> ReadMaps(string root, List<string> problems)
    {
        var path = Path.Combine(root, MapsFile.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        try
        {
            var document = JsonSerializer.Deserialize<MapsDocument>(File.ReadAllText(path), Options);
            var maps = document?.Maps;
            if (maps is null || maps.Count == 0)
            {
                return Array.Empty<string>();
            }

            var cleaned = new List<string>(maps.Count);
            foreach (var map in maps)
            {
                if (!string.IsNullOrWhiteSpace(map))
                {
                    cleaned.Add(map.Trim());
                }
            }

            return cleaned;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problems.Add(MapsFile + ": " + ex.Message);
            return Array.Empty<string>();
        }
    }

    private static void ReadPoints(string root, Dictionary<string, MapPoints> target, List<string> problems)
    {
        var path = Path.Combine(root, PointsFile.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<PointsDocument>(File.ReadAllText(path), Options);
            if (document?.Maps is null)
            {
                return;
            }

            foreach (var (map, points) in document.Maps)
            {
                if (string.IsNullOrWhiteSpace(map) || points is null)
                {
                    continue;
                }

                target[map.Trim()] = new MapPoints { Points = points };
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problems.Add(PointsFile + ": " + ex.Message);
        }
    }

    /// <summary>Shape of <c>data/maps.json</c>.</summary>
    private sealed class MapsDocument
    {
        /// <summary>Map names the plugin is enabled on.</summary>
        [JsonPropertyName("maps")]
        public List<string>? Maps { get; set; }
    }

    /// <summary>Shape of <c>data/points.json</c>.</summary>
    private sealed class PointsDocument
    {
        /// <summary>Map name to named points.</summary>
        [JsonPropertyName("maps")]
        public Dictionary<string, List<MapPoint>>? Maps { get; set; }
    }

    /// <summary>Points of one map, kept as a node so the dictionary stays serializable.</summary>
    private sealed class MapPoints
    {
        /// <summary>Named points on the map.</summary>
        internal IReadOnlyList<MapPoint> Points { get; init; } = Array.Empty<MapPoint>();
    }
}

/// <summary>A named position an operator recorded for a map.</summary>
public sealed class MapPoint
{
    /// <summary>Name players will see, e.g. <c>window</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Kind of point: <c>throw</c> for a standing spot, <c>land</c> for a target.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "throw";

    /// <summary>Description shown next to the name.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>Horizontal position.</summary>
    [JsonPropertyName("x")]
    public float X { get; set; }

    /// <summary>Second horizontal position.</summary>
    [JsonPropertyName("y")]
    public float Y { get; set; }

    /// <summary>Vertical position.</summary>
    [JsonPropertyName("z")]
    public float Z { get; set; }
}
