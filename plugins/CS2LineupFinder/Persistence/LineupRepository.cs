using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Persistence;

/// <summary>
/// Reads and writes saved line-ups under <c>data/lineups/*.json</c>. Files are
/// grouped by map, so a server hosting several maps keeps one directory per map
/// and <c>css_lf_list</c> never shows a line-up from the wrong map.
/// </summary>
public sealed class LineupRepository
{
    /// <summary>Directory that holds the per-map directories.</summary>
    public const string LineupsRelativePath = "data/lineups";

    /// <summary>Extension of a saved line-up.</summary>
    public const string FileExtension = ".json";

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private readonly string _rootDirectory;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Creates a repository rooted at a plugin data directory.</summary>
    /// <param name="dataDirectory">Absolute path of <c>data/lineups</c>.</param>
    public LineupRepository(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _rootDirectory = dataDirectory;
    }

    /// <summary>
    /// Creates the repository a plugin root directory should use, i.e. the one
    /// rooted at <see cref="LineupsRelativePath"/>.
    /// </summary>
    /// <param name="pluginRootDirectory">Directory holding <c>config/</c> and <c>data/</c>.</param>
    /// <returns>A repository over <c>&lt;pluginRootDirectory&gt;/data/lineups</c>.</returns>
    /// <remarks>
    /// Built from the constant rather than a literal so the path documented in
    /// <c>data/README.md</c>, the one <c>data/.gitignore</c> excludes and the one
    /// saved files actually land in cannot drift apart.
    /// </remarks>
    public static LineupRepository ForPluginRoot(string pluginRootDirectory) => new(
        Path.Combine(pluginRootDirectory, LineupsRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Root directory the repository reads and writes.</summary>
    public string RootDirectory => _rootDirectory;

    /// <summary>Returns the directory holding the line-ups of one map.</summary>
    /// <param name="mapName">Map name, e.g. <c>de_mirage</c>.</param>
    /// <returns>The per-map directory, created on demand.</returns>
    public string GetMapDirectory(string mapName)
    {
        var safeMap = Sanitize(mapName);
        if (safeMap.Length == 0)
        {
            safeMap = "unknown";
        }

        return Path.Combine(_rootDirectory, safeMap);
    }

    /// <summary>Returns the file path a line-up is stored at.</summary>
    /// <param name="mapName">Map the line-up belongs to.</param>
    /// <param name="name">Line-up name.</param>
    /// <returns>The absolute file path.</returns>
    public string GetPath(string mapName, string name) => Path.Combine(GetMapDirectory(mapName), Sanitize(name) + FileExtension);

    /// <summary>Writes a line-up, creating directories as needed.</summary>
    /// <param name="record">Record to store.</param>
    /// <returns>The path that was written.</returns>
    /// <exception cref="ArgumentException">Thrown when the name is empty or not a legal file name.</exception>
    public string Save(LineupRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var name = Sanitize(record.Name);
        if (name.Length == 0)
        {
            throw new ArgumentException("Line-up name must contain at least one file-system safe character.", nameof(record));
        }

        if (string.IsNullOrWhiteSpace(record.Map))
        {
            throw new ArgumentException("Line-up map must be set before saving.", nameof(record));
        }

        record.Name = name;
        var path = Path.Combine(GetMapDirectory(record.Map), name + FileExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a sibling temp file first so a crash cannot truncate a good line-up.
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(record));
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>Serializes a record to JSON without touching the file system.</summary>
    /// <param name="record">Record to serialize.</param>
    /// <returns>Indented JSON.</returns>
    public static string Serialize(LineupRecord record) => JsonSerializer.Serialize(record, SerializerOptions);

    /// <summary>Parses a record from JSON.</summary>
    /// <param name="json">JSON text.</param>
    /// <returns>The parsed record.</returns>
    /// <exception cref="JsonException">Thrown when the payload is not a line-up.</exception>
    public static LineupRecord Deserialize(string json) => JsonSerializer.Deserialize<LineupRecord>(json, SerializerOptions)
        ?? throw new JsonException("Line-up file is empty.");

    /// <summary>Loads one line-up.</summary>
    /// <param name="mapName">Map the line-up belongs to.</param>
    /// <param name="name">Line-up name.</param>
    /// <returns>The record, or <see langword="null"/> when the file does not exist.</returns>
    /// <exception cref="JsonException">Thrown when the file is not parseable.</exception>
    public LineupRecord? Load(string mapName, string name)
    {
        var path = GetPath(mapName, name);
        return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : null;
    }

    /// <summary>Lists the line-ups stored for a map.</summary>
    /// <param name="mapName">Map to list.</param>
    /// <returns>Records ordered by name; unreadable files are skipped.</returns>
    public IReadOnlyList<LineupRecord> List(string mapName)
    {
        var directory = GetMapDirectory(mapName);
        if (!Directory.Exists(directory))
        {
            return Array.Empty<LineupRecord>();
        }

        var records = new List<LineupRecord>();
        foreach (var path in Directory.EnumerateFiles(directory, "*" + FileExtension).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                records.Add(Deserialize(File.ReadAllText(path)));
            }
            catch (JsonException)
            {
                // A hand-edited or half-written file must not break css_lf_list.
            }
            catch (IOException)
            {
            }
        }

        return records;
    }

    /// <summary>Deletes a saved line-up.</summary>
    /// <param name="mapName">Map the line-up belongs to.</param>
    /// <param name="name">Line-up name.</param>
    /// <returns><see langword="true"/> when a file was removed.</returns>
    public bool Delete(string mapName, string name)
    {
        var path = GetPath(mapName, name);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Strips path separators, traversal sequences and characters that are illegal in a
    /// file name. A name of <c>..</c> or <c>a/b</c> therefore cannot escape the data directory.
    /// </summary>
    /// <param name="name">Raw name typed by the player.</param>
    /// <returns>The safe file name stem, possibly empty.</returns>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim().Replace(' ', '_');
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (Array.IndexOf(InvalidNameChars, c) >= 0 || c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
            {
                continue;
            }

            builder.Append(c);
        }

        var cleaned = builder.ToString().Trim('.');
        return cleaned == "." ? string.Empty : cleaned;
    }

    /// <summary>Formats a distance for chat output, in world units.</summary>
    /// <param name="distance">Distance in units.</param>
    /// <returns>The formatted distance.</returns>
    public static string FormatDistance(float distance) => string.Create(CultureInfo.InvariantCulture, $"{distance:0.0}u");
}
