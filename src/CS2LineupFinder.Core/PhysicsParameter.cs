// <copyright file="PhysicsParameter.cs" company="CS2LineupFinder">
// A single tunable plus its provenance, so the calibration report can cite it.
// </copyright>

namespace CS2LineupFinder.Core;

/// <summary>Provenance tier for a constant, mirroring the PHYSICS.md table.</summary>
public enum SourceTier
{
    /// <summary>Valve-authored source or leaked SDK headers.</summary>
    Valve = 0,

    /// <summary>CS:GO leaked headers / reverse-engineered builds.</summary>
    Leaked = 1,

    /// <summary>Community reverse-engineering write-ups.</summary>
    Community = 2,

    /// <summary>Fitted in-game against recorded landings by CalibrationRunner.</summary>
    Measured = 3,
}

/// <summary>A single tunable with its provenance, so the calibration report can cite it.</summary>
public sealed class PhysicsParameter
{
    public PhysicsParameter(string name, float value, float min, float max, SourceTier tier, string source)
    {
        Name = name;
        Value = value;
        Min = min;
        Max = max;
        Tier = tier;
        Source = source;
    }

    /// <summary>Stable identifier, also the JSON key used in calibration files.</summary>
    public string Name { get; }

    public float Value { get; set; }

    /// <summary>Lower bound CalibrationRunner will search.</summary>
    public float Min { get; }

    /// <summary>Upper bound CalibrationRunner will search.</summary>
    public float Max { get; }

    public SourceTier Tier { get; }

    /// <summary>Short citation, usually a URL or a measurement recipe.</summary>
    public string Source { get; }

    public PhysicsParameter Clone() => new(Name, Value, Min, Max, Tier, Source);
}
