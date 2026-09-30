using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Config;

/// <summary>
/// Runtime settings loaded from <c>config/config.toml</c>. Defaults match the
/// shipped template, so a missing file degrades to a working configuration
/// instead of a failed plugin load.
/// </summary>
public sealed class PluginConfig
{
    /// <summary>Relative path of the configuration file inside the plugin directory.</summary>
    public const string RelativePath = "config/config.toml";

    /// <summary>Wall clock budget for <c>css_lf_find</c>, enforced by the plugin.</summary>
    public double SolveTimeoutSeconds { get; private set; } = 3.0;

    /// <summary>Upper bound on how many solutions are printed and stored per search.</summary>
    public int MaxResults { get; private set; } = 5;

    /// <summary>Master switch for every in-world visual (beams, rings, cross markers).</summary>
    public bool EnableVisualization { get; private set; } = true;

    /// <summary>Seconds the aim beam stays visible after a successful search.</summary>
    public double BeamDurationSeconds { get; private set; } = 3.0;

    /// <summary>Length of the aim beam drawn from the player's eyes, in units.</summary>
    public double BeamLength { get; private set; } = 1024.0;

    /// <summary>Number of beam segments used to approximate a landing zone ring.</summary>
    public int RingSegments { get; private set; } = 24;

    /// <summary>Radius applied when the player creates a zone without one, in units.</summary>
    public double DefaultZoneRadius { get; private set; } = 128.0;

    /// <summary>Language of player-facing text: <c>en</c> or <c>zh-CN</c>.</summary>
    public string Language { get; private set; } = "en";

    /// <summary>Upper bound on candidates handed to the simulator per search.</summary>
    public int MaxCandidates { get; private set; } = 64;

    /// <summary>Keys present in the file that the plugin does not understand.</summary>
    public IReadOnlyList<string> UnknownKeys { get; private set; } = Array.Empty<string>();

    /// <summary>Directory the settings were loaded from, for diagnostics.</summary>
    public string SourceDirectory { get; private set; } = string.Empty;

    /// <summary>Loads settings from a plugin root directory.</summary>
    /// <param name="pluginRootDirectory">Directory holding <c>config/</c> and <c>data/</c>.</param>
    /// <param name="document">Parsed document, when the caller already has one.</param>
    /// <returns>The loaded configuration.</returns>
    /// <exception cref="TomlFormatException">Thrown when the file is not valid TOML.</exception>
    public static PluginConfig Load(string pluginRootDirectory, TomlDocument? document = null)
    {
        var config = new PluginConfig { SourceDirectory = pluginRootDirectory };
        var path = Path.Combine(pluginRootDirectory, RelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (document is null)
        {
            if (!File.Exists(path))
            {
                return config;
            }

            document = TomlDocument.Load(path);
        }

        config.SolveTimeoutSeconds = ClampPositive(document.GetDouble("solver.timeoutSeconds", config.SolveTimeoutSeconds), 0.1, 60.0);
        config.MaxResults = (int)ClampPositive(document.GetInt("solver.maxResults", config.MaxResults), 1, 50);
        config.MaxCandidates = (int)ClampPositive(document.GetInt("solver.maxCandidates", config.MaxCandidates), 1, 4096);
        config.EnableVisualization = document.GetBool("visuals.enabled", config.EnableVisualization);
        config.BeamDurationSeconds = ClampPositive(document.GetDouble("visuals.beamDurationSeconds", config.BeamDurationSeconds), 0.1, 60.0);
        config.BeamLength = ClampPositive(document.GetDouble("visuals.beamLength", config.BeamLength), 64.0, 32768.0);
        config.RingSegments = (int)ClampPositive(document.GetInt("visuals.ringSegments", config.RingSegments), 3, 256);
        config.DefaultZoneRadius = ClampPositive(document.GetDouble("zone.defaultRadius", config.DefaultZoneRadius), 1.0, 8192.0);
        config.Language = NormalizeLanguage(document.GetString("general.language", config.Language));
        config.UnknownKeys = FindUnknownKeys(document);
        return config;
    }

    /// <summary>Builds the environment handed to the simulator.</summary>
    /// <returns>Simulation settings for a 64 tick server.</returns>
    public SimulationEnvironment BuildEnvironment() => new()
    {
        Gravity = 800f,
        TickInterval = 1f / 64f,
        MaxSteps = 4096,
        AirDrag = 0f,
    };

    private static readonly string[] KnownKeys =
    {
        "solver.timeoutSeconds",
        "solver.maxResults",
        "solver.maxCandidates",
        "visuals.enabled",
        "visuals.beamDurationSeconds",
        "visuals.beamLength",
        "visuals.ringSegments",
        "zone.defaultRadius",
        "general.language",
    };

    private static IReadOnlyList<string> FindUnknownKeys(TomlDocument document)
    {
        List<string>? unknown = null;
        foreach (var key in document.Values.Keys)
        {
            if (Array.IndexOf(KnownKeys, key) >= 0)
            {
                continue;
            }

            (unknown ??= new List<string>()).Add(key);
        }

        return (IReadOnlyList<string>?)unknown ?? Array.Empty<string>();
    }

    private static double ClampPositive(double value, double min, double max)
    {
        if (double.IsNaN(value))
        {
            return min;
        }

        return Math.Clamp(value, min, max);
    }

    private static string NormalizeLanguage(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }

        return "en";
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"timeout={SolveTimeoutSeconds:0.##}s maxResults={MaxResults} visuals={EnableVisualization} lang={Language}");
}
