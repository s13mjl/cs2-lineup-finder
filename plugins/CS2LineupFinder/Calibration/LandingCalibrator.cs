using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.State;

namespace CS2LineupFinder.Plugin.Calibration;

/// <summary>
/// One recorded throw pairing the suggestion the plugin made with where the
/// grenade actually landed in game. The shape mirrors
/// <c>docs/calibration/sample-throws.json</c> entries so a collected file can be
/// fed straight into <c>CalibrationRunner</c> without conversion.
/// </summary>
public sealed class LandingSample
{
    /// <summary>Serial number of the throw, also used in the sample name.</summary>
    public int Index { get; set; }

    /// <summary>Map the throw happened on.</summary>
    public string Map { get; set; } = string.Empty;

    /// <summary>Throw origin: feet position and eye height at the moment of the throw.</summary>
    public Vec3 Origin { get; set; }

    /// <summary>Eye height above the origin when the throw was released.</summary>
    public float EyeHeight { get; set; }

    /// <summary>Yaw the plugin told the player to aim at (contract convention).</summary>
    public float AimYaw { get; set; }

    /// <summary>Pitch the plugin told the player to aim at (contract convention, positive up).</summary>
    public float AimPitch { get; set; }

    /// <summary>Grenade family of the recorded throw.</summary>
    public string GrenadeType { get; set; } = "Smoke";

    /// <summary>Where the simulated grenade was predicted to come to rest.</summary>
    public Vec3 PredictedImpact { get; set; }

    /// <summary>
    /// Exact release position used by the successful simulation, when the
    /// recording plugin version captured it. Null/zero for older files, where
    /// replay rebuilds it from origin + eye + aim offset.
    /// </summary>
    public Vec3? ReleasePosition { get; set; }

    /// <summary>Environment.TickCount64 (ms) at which the suggestion was recorded; used to pair the capture.</summary>
    public long SuggestedTickMs { get; set; }

    /// <summary>
    /// Monotonic number of the suggestion this sample pairs with. The launch
    /// capture stores the same number, so a grenade thrown before a second
    /// find can never be paired with the newer suggestion.
    /// </summary>
    public long SuggestionNumber { get; set; }

    /// <summary>
    /// The projectile's own position one tick after release, read straight off
    /// the spawned smokegrenade_projectile entity. Null for older files.
    /// When present, replay must use THIS and <see cref="LaunchVelocity"/>
    /// instead of synthesising the launch from aim + throw speed: the synthesis
    /// drops the thrower's inherited velocity, which the engine adds to every
    /// real throw (telemetry-002 row 0 measures 678.63 u/s against the 675
    /// full-throw constant - the delta IS the inherited player velocity).
    /// </summary>
    public Vec3? LaunchPosition { get; set; }

    /// <summary>The projectile's actual launch velocity; see <see cref="LaunchPosition"/>.</summary>
    public Vec3? LaunchVelocity { get; set; }

    /// <summary>
    /// Post-hoc prediction re-simulated from <see cref="LaunchPosition"/> and
    /// <see cref="LaunchVelocity"/> at detonation time. Null when the launch
    /// capture was unusable. <see cref="Error"/> measures the pre-throw
    /// prediction (what the player was told) against reality; comparing it
    /// with <see cref="ReplayError"/> splits that miss into "the throw left
    /// differently than simulated" versus "the physics diverged from the real
    /// flight".
    /// </summary>
    public Vec3? ReplayImpact { get; set; }

    /// <summary>Planar distance between <see cref="ReplayImpact"/> and reality, in units.</summary>
    public float? ReplayError { get; set; }

    /// <summary>Where the game actually detonated the grenade.</summary>
    public Vec3 ActualImpact { get; set; }

    /// <summary>Planar distance between prediction and reality, in units.</summary>
    public float Error { get; set; }

    /// <summary>When the throw was recorded (UTC).</summary>
    public string RecordedAt { get; set; } = string.Empty;
}

/// <summary>
/// Collects real landing points and pairs them with the last suggestion the
/// plugin made. Every sample is appended to
/// <c>data/calibration/landings-YYYYMMDD.json</c>; once a file holds enough
/// throws it can be run through <c>CalibrationRunner</c> to fit the physics
/// parameters against real game behaviour instead of synthetic data.
/// </summary>
public sealed class LandingCalibrator
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new Vec3JsonConverter() },
    };

    private readonly string _directory;
    private readonly object _gate = new();
    private readonly List<LandingSample> _samples = new();

    /// <summary>Creates the collector writing under <paramref name="directory"/>.</summary>
    public LandingCalibrator(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        LoadExistingSamples();
    }

    /// <summary>Number of samples collected this session.</summary>
    public int Count
    {
        get { lock (_gate) { return _samples.Count; } }
    }

    /// <summary>
    /// Pairs a real detonation with the pending suggestion and appends it to the
    /// day file. Returns the created sample, or null when there was nothing to
    /// pair with (no suggestion, or the grenade type did not match).
    /// </summary>
    public LandingSample? Record(
        int index,
        string map,
        Vec3 origin,
        float eyeHeight,
        PendingSuggestion suggestion,
        Vec3 actualImpact,
        Vec3? releasePosition = null,
        Vec3? launchPosition = null,
        Vec3? launchVelocity = null,
        long? suggestionNumber = null,
        Vec3? replayImpact = null)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (string.IsNullOrEmpty(map))
        {
            throw new ArgumentException("Map must be provided", nameof(map));
        }

        float planar = MathF.Sqrt(
            ((suggestion.PredictedImpact.X - actualImpact.X) * (suggestion.PredictedImpact.X - actualImpact.X)) +
            ((suggestion.PredictedImpact.Y - actualImpact.Y) * (suggestion.PredictedImpact.Y - actualImpact.Y)));

        float? replayError = replayImpact is { } replay
            ? MathF.Sqrt(
                ((replay.X - actualImpact.X) * (replay.X - actualImpact.X)) +
                ((replay.Y - actualImpact.Y) * (replay.Y - actualImpact.Y)))
            : null;

        var sample = new LandingSample
        {
            Index = index,
            Map = map,
            Origin = origin,
            EyeHeight = eyeHeight,
            AimYaw = suggestion.Yaw,
            AimPitch = suggestion.Pitch,
            GrenadeType = suggestion.GrenadeType.ToString(),
            PredictedImpact = suggestion.PredictedImpact,
            ReleasePosition = releasePosition,
            SuggestedTickMs = suggestion.SuggestedTickMs,
            SuggestionNumber = suggestionNumber ?? suggestion.SuggestionNumber,
            LaunchPosition = launchPosition,
            LaunchVelocity = launchVelocity,
            ReplayImpact = replayImpact,
            ReplayError = replayError,
            ActualImpact = actualImpact,
            Error = planar,
            RecordedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };

        List<LandingSample> snapshot;
        lock (_gate)
        {
            _samples.Add(sample);
            snapshot = new List<LandingSample>(_samples);
        }

        WriteDayFile(snapshot);
        return sample;
    }

    private void WriteDayFile(List<LandingSample> samples)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var name = "landings-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".json";
            var path = Path.Combine(_directory, name);
            var document = new SamplesDocument { Samples = samples };
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(document, SerializerOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Calibration must never take the game thread down over IO.
        }
    }

    /// <summary>
    /// Loads today's day file, if any, so samples recorded before a restart are
    /// appended to instead of silently overwritten. The Index is advanced past
    /// the loaded entries and the session counter (owned by the plugin) starts
    /// from there, so "#1 个样本" after a restart really is the first throw of a
    /// continued series rather than a truncated file.
    /// </summary>
    private void LoadExistingSamples()
    {
        try
        {
            var name = "landings-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".json";
            var path = Path.Combine(_directory, name);
            if (!File.Exists(path))
            {
                return;
            }

            string json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<SamplesDocument>(json, SerializerOptions);
            if (document?.Samples is { Count: > 0 } existing)
            {
                _samples.AddRange(existing);
            }
        }
        catch (Exception)
        {
            // A broken day file must never stop the plugin from loading.
        }
    }

    private sealed class SamplesDocument
    {
        [JsonPropertyName("samples")]
        public List<LandingSample> Samples { get; set; } = new();
    }

    /// <summary>
    /// Reads and writes Vec3 as a plain X/Y/Z object. The default serializer
    /// also emits the struct's computed Length2D/Length/LengthSquared, but on
    /// read Vec3's read-only properties cannot be populated - every vector in
    /// a reloaded day file came back as zeros (2026-10-31: samples #1-#3 were
    /// wiped when the server restarted and the next recording rewrote them).
    /// </summary>
    private sealed class Vec3JsonConverter : JsonConverter<Vec3>
    {
        public override Vec3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                return Vec3.Zero;
            }

            float x = 0f;
            float y = 0f;
            float z = 0f;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                string name = reader.GetString() ?? string.Empty;
                reader.Read();
                if (reader.TokenType != JsonTokenType.Number)
                {
                    reader.Skip();
                    continue;
                }

                float value = (float)reader.GetDouble();
                if (name.Equals("X", StringComparison.OrdinalIgnoreCase))
                {
                    x = value;
                }
                else if (name.Equals("Y", StringComparison.OrdinalIgnoreCase))
                {
                    y = value;
                }
                else if (name.Equals("Z", StringComparison.OrdinalIgnoreCase))
                {
                    z = value;
                }
            }

            return new Vec3(x, y, z);
        }

        public override void Write(Utf8JsonWriter writer, Vec3 value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("X", value.X);
            writer.WriteNumber("Y", value.Y);
            writer.WriteNumber("Z", value.Z);
            writer.WriteEndObject();
        }
    }
}

/// <summary>What the plugin suggested in its most recent find / load / set.</summary>
public sealed record PendingSuggestion(
    float Yaw,
    float Pitch,
    Vec3 Origin,
    float EyeHeight,
    GrenadeType GrenadeType,
    Vec3 PredictedImpact,
    Vec3? ReleasePosition = null,
    long SuggestedTickMs = 0,
    long SuggestionNumber = 0);
