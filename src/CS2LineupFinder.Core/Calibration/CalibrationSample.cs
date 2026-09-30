// <copyright file="CalibrationSample.cs" company="CS2LineupFinder">
// JSON shapes for calibration input and output.
// </copyright>

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Core.Calibration;

/// <summary>
/// One measured throw. <see cref="LandingX"/>/<see cref="LandingY"/> are the
/// coordinates the grenade actually came to rest at, read off an in-game
/// measurement. Everything else describes how the throw was made.
/// </summary>
public sealed class CalibrationSample
{
    /// <summary>Free-form label, usually the map and lineup name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("origin")]
    public CalibrationVector Origin { get; set; } = new();

    /// <summary>Horizontal aim angle, degrees. 0 is +X.</summary>
    [JsonPropertyName("aimYaw")]
    public float AimYaw { get; set; }

    /// <summary>Vertical aim angle, degrees. Positive is up.</summary>
    [JsonPropertyName("aimPitch")]
    public float AimPitch { get; set; }

    /// <summary>Thrower's own velocity in u/s, so a moving throw can be fitted.</summary>
    [JsonPropertyName("playerVelocity")]
    public CalibrationVector PlayerVelocity { get; set; } = new();

    [JsonPropertyName("grenadeType")]
    public GrenadeType GrenadeType { get; set; } = GrenadeType.He;

    [JsonPropertyName("throwButton")]
    public ThrowButton ThrowButton { get; set; } = ThrowButton.Primary;

    [JsonPropertyName("throwStrength")]
    public float ThrowStrength { get; set; } = 1f;

    [JsonPropertyName("gameTickRate")]
    public int GameTickRate { get; set; } = 64;

    /// <summary>
    /// Optional flat floor height. When set, the sample is simulated against a
    /// synthetic infinite floor at this Z, which is what makes it possible to
    /// calibrate open-field throws with no map files present.
    /// </summary>
    [JsonPropertyName("floorHeight")]
    public float? FloorHeight { get; set; }

    /// <summary>Measured resting X of the grenade, world units.</summary>
    [JsonPropertyName("landingX")]
    public float LandingX { get; set; }

    /// <summary>Measured resting Y of the grenade, world units.</summary>
    [JsonPropertyName("landingY")]
    public float LandingY { get; set; }

    /// <summary>
    /// Optional override for the measured landing Z. Without it the runner
    /// compares horizontal range only, which is the robust choice when the
    /// resting height is hard to read off in game.
    /// </summary>
    [JsonPropertyName("landingZ")]
    public float? LandingZ { get; set; }

    /// <summary>Converts to the contract type, resolving the aim angle to a direction.</summary>
    public Vec3 AimDirection()
    {
        float yaw = AimYaw * MathF.PI / 180f;
        float pitch = AimPitch * MathF.PI / 180f;
        float cosPitch = MathF.Cos(pitch);

        // Source convention: yaw 0 looks down +X, positive yaw turns left (+Y).
        return new Vec3(cosPitch * MathF.Cos(yaw), cosPitch * MathF.Sin(yaw), MathF.Sin(pitch));
    }
}

/// <summary>Plain x/y/z triple, so the JSON stays readable and diff-friendly.</summary>
public sealed class CalibrationVector
{
    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("z")]
    public float Z { get; set; }

    public Vec3 ToVec3() => new(X, Y, Z);

    public static CalibrationVector From(Vec3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
}

/// <summary>Top level calibration input file.</summary>
public sealed class CalibrationDataSet
{
    /// <summary>Free-form provenance note, e.g. which build and map the data came from.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("samples")]
    public List<CalibrationSample> Samples { get; set; } = new();
}
