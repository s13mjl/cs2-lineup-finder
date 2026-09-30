using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CS2LineupFinder.Plugin.Config;

/// <summary>
/// Minimal, dependency free reader for the subset of TOML the plugin needs:
/// <c>[section]</c> headers and <c>key = value</c> pairs with string, integer,
/// float or boolean values. Keys are flattened to <c>section.key</c>.
/// </summary>
/// <remarks>
/// CounterStrikeSharp loads the plugin as a single DLL, so pulling in
/// <c>Tomlyn</c> just for six settings is not worth the deployment weight. Unknown
/// keys are preserved and reported as warnings by <see cref="PluginConfig"/>.
/// </remarks>
public sealed class TomlDocument
{
    private readonly Dictionary<string, string> _values;

    private TomlDocument(Dictionary<string, string> values) => _values = values;

    /// <summary>Flattened <c>section.key</c> to raw value map, in file order.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Parses TOML text.</summary>
    /// <param name="text">File contents.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="TomlFormatException">Thrown when a line is not valid TOML.</exception>
    public static TomlDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') )
            {
                if (!line.EndsWith(']'))
                {
                    throw new TomlFormatException(lineNumber, "Section header is missing its closing ']'.");
                }

                section = line[1..^1].Trim();
                if (section.Length == 0)
                {
                    throw new TomlFormatException(lineNumber, "Section name is empty.");
                }

                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new TomlFormatException(lineNumber, "Expected 'key = value'.");
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                throw new TomlFormatException(lineNumber, "Key or value is empty.");
            }

            var fullKey = section.Length == 0 ? key : section + "." + key;
            values[fullKey] = Unquote(value, lineNumber);
        }

        return new TomlDocument(values);
    }

    /// <summary>Reads and parses a TOML file.</summary>
    /// <param name="path">File to read.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="TomlFormatException">Thrown when the file is not valid TOML.</exception>
    public static TomlDocument Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Returns a string value, or <paramref name="fallback"/> when the key is absent.</summary>
    /// <param name="key">Flattened key.</param>
    /// <param name="fallback">Value used when the key is missing or blank.</param>
    /// <returns>The configured value.</returns>
    public string GetString(string key, string fallback)
    {
        if (!_values.TryGetValue(key, out var raw) || raw.Length == 0)
        {
            return fallback;
        }

        return raw;
    }

    /// <summary>Returns an integer value, or <paramref name="fallback"/> when the key is absent.</summary>
    /// <param name="key">Flattened key.</param>
    /// <param name="fallback">Value used when the key is missing.</param>
    /// <returns>The configured value.</returns>
    public int GetInt(string key, int fallback) => TryGetDouble(key, out var value)
        ? (int)Math.Round(value, MidpointRounding.AwayFromZero)
        : fallback;

    /// <summary>Returns a floating point value, or <paramref name="fallback"/> when the key is absent.</summary>
    /// <param name="key">Flattened key.</param>
    /// <param name="fallback">Value used when the key is missing.</param>
    /// <returns>The configured value.</returns>
    public double GetDouble(string key, double fallback) => TryGetDouble(key, out var value) ? value : fallback;

    /// <summary>Returns a boolean value, or <paramref name="fallback"/> when the key is absent.</summary>
    /// <param name="key">Flattened key.</param>
    /// <param name="fallback">Value used when the key is missing or unparsable.</param>
    /// <returns>The configured value.</returns>
    public bool GetBool(string key, bool fallback)
    {
        if (!_values.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        return raw.ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => fallback,
        };
    }

    private bool TryGetDouble(string key, out double value)
    {
        value = 0d;
        return _values.TryGetValue(key, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inString = !inString;
            }
            else if (c == '#' && !inString)
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string Unquote(string value, int lineNumber)
    {
        if (!value.StartsWith('"'))
        {
            return value;
        }

        if (value.Length < 2 || !value.EndsWith('"'))
        {
            throw new TomlFormatException(lineNumber, "String value is missing its closing quote.");
        }

        return value[1..^1];
    }
}

/// <summary>Raised when a configuration file is not valid TOML.</summary>
public sealed class TomlFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TomlFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Diagnostic text.</param>
    public TomlFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    /// <param name="message">Diagnostic text.</param>
    /// <param name="innerException">Underlying failure.</param>
    public TomlFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception pointing at the offending line.</summary>
    /// <param name="lineNumber">1-based line number.</param>
    /// <param name="message">Diagnostic text.</param>
    public TomlFormatException(int lineNumber, string message)
        : base(string.Create(CultureInfo.InvariantCulture, $"config.toml line {lineNumber}: {message}"))
    {
        LineNumber = lineNumber;
    }

    /// <summary>1-based line that failed to parse, or 0 when unknown.</summary>
    public int LineNumber { get; }
}
