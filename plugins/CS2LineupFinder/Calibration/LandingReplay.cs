// <copyright file="LandingReplay.cs" company="CS2LineupFinder">
// Reads recorded calibration landings so they can be replayed on the live map.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Calibration;

/// <summary>Vector shape used by the landing JSON files.</summary>
public sealed class Vec3Dto
{
    /// <summary>X component.</summary>
    public double X { get; set; }

    /// <summary>Y component.</summary>
    public double Y { get; set; }

    /// <summary>Z component.</summary>
    public double Z { get; set; }

    /// <summary>Converts to the contract vector.</summary>
    public Vec3 ToVec3() => new((float)X, (float)Y, (float)Z);
}

/// <summary>One recorded throw, shaped exactly like the day files.</summary>
public sealed class LandingSampleDto
{
    /// <summary>Serial number of the throw.</summary>
    public int Index { get; set; }

    /// <summary>Map the throw happened on.</summary>
    public string Map { get; set; } = string.Empty;

    /// <summary>Throw origin: feet position.</summary>
    public Vec3Dto Origin { get; set; } = new();

    /// <summary>Eye height above the origin at release.</summary>
    public float EyeHeight { get; set; }

    /// <summary>Aim yaw the plugin suggested (contract convention).</summary>
    public float AimYaw { get; set; }

    /// <summary>Aim pitch the plugin suggested (contract convention, positive up).</summary>
    public float AimPitch { get; set; }

    /// <summary>Grenade family of the recorded throw.</summary>
    public string GrenadeType { get; set; } = "Smoke";

    /// <summary>Where the simulation predicted the grenade would come to rest.</summary>
    public Vec3Dto PredictedImpact { get; set; } = new();

    /// <summary>Exact release position, when the recording plugin captured it.</summary>
    public Vec3Dto? ReleasePosition { get; set; }

    /// <summary>The projectile's own position one tick after release (new files only).</summary>
    public Vec3Dto? LaunchPosition { get; set; }

    /// <summary>The projectile's actual launch velocity (new files only).</summary>
    public Vec3Dto? LaunchVelocity { get; set; }

    /// <summary>Prediction re-simulated from the captured launch state (new files only).</summary>
    public Vec3Dto? ReplayImpact { get; set; }

    /// <summary>Planar error of that replay against reality (new files only).</summary>
    public float? ReplayError { get; set; }

    /// <summary>Environment.TickCount64 (ms) at which the suggestion was recorded; used to pair the capture.</summary>
    public long SuggestedTickMs { get; set; }

    /// <summary>Where the game actually detonated the grenade.</summary>
    public Vec3Dto ActualImpact { get; set; } = new();

    /// <summary>Planar distance between prediction and reality.</summary>
    public float Error { get; set; }

    /// <summary>When the throw was recorded (UTC).</summary>
    public string RecordedAt { get; set; } = string.Empty;
}

/// <summary>Root object of a day file.</summary>
public sealed class LandingFileDto
{
    /// <summary>All samples recorded that day.</summary>
    public List<LandingSampleDto> Samples { get; set; } = new();
}

/// <summary>Loads landing day files from the calibration directory.</summary>
public static class LandingReplayFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Reads every <c>landings-*.json</c> in the directory. A broken file is skipped.</summary>
    public static List<LandingSampleDto> LoadAll(string directory)
    {
        var samples = new List<LandingSampleDto>();
        if (!Directory.Exists(directory))
        {
            return samples;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "landings-*.json"))
        {
            try
            {
                var document = JsonSerializer.Deserialize<LandingFileDto>(File.ReadAllText(path), Options);
                if (document?.Samples is not null)
                {
                    samples.AddRange(document.Samples);
                }
            }
            catch (Exception)
            {
                // A broken day file must never take the command down.
            }
        }

        return samples;
    }
}
