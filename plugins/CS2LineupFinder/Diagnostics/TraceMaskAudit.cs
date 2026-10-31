// <copyright file="TraceMaskAudit.cs" company="CS2LineupFinder">
// Classifies every recorded engine move of a telemetry capture against two
// trace masks, using the CSV's own velocity discontinuities as the ground
// truth for where the engine really bounced.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Game;

namespace CS2LineupFinder.Plugin.Diagnostics;

/// <summary>
/// For every pair of adjacent telemetry rows this audit asks: did the engine
/// bounce here (velocity jumped beyond the gravity step)? Did the default
/// trace hit? Does the clip-excluded trace hit? The cross-tab counts where
/// each mask disagrees with the engine - the mask with zero free-flight false
/// hits and zero missed bounces is the one the simulator must use.
/// </summary>
public static class TraceMaskAudit
{
    /// <summary>Gravity velocity step per tick, measured: 320 u/s^2 / 64 Hz.</summary>
    private const float GravityStepZ = -5f;

    /// <summary>Speed error beyond which a tick is a contact discontinuity.</summary>
    private const float BounceThreshold = 2f;

    /// <summary>Layers the grenade must still collide with (measured enum).</summary>
    public const ulong InteractWith = GrenadeCollisionMasks.InteractWith;

    /// <summary>PlayerClip|NpcClip - the layers the engine grenade ignores.</summary>
    public const ulong ClipBits = GrenadeCollisionMasks.InteractExclude;

    /// <summary>Runs the audit for one CSV, one line per finding category.</summary>
    public static IReadOnlyList<string> Run(string csvPath, ITraceProbe probe)
    {
        var lines = new List<string>();
        if (probe is not ReflectiveTraceBackend)
        {
            return lines;
        }

        if (!LiveMapTelemetryReplay.TryLoadRows(csvPath, out var rows) || rows.Count < 2)
        {
            return lines;
        }

        int engineBounce = 0, quiet = 0;
        int bounceDefaultHit = 0, bounceMaskedHit = 0;
        int quietDefaultHit = 0, quietMaskedHit = 0;
        int bounceExclOnly = 0, quietExclOnly = 0;
        int bounceZeroWith = 0, quietZeroWith = 0;
        var bounceMissExamples = new List<string>();
        var quietFalseExamples = new List<string>();
        var falseHitContents = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 1; i < rows.Count; i++)
        {
            Vec3 deltaV = rows[i].Velocity - rows[i - 1].Velocity;
            Vec3 expected = new(0f, 0f, GravityStepZ);
            bool engineContact = (deltaV - expected).Length > BounceThreshold;

            Vec3 from = rows[i - 1].Position;
            Vec3 to = rows[i].Position;
            bool defaultHit = probe.RawHit(from, to, 2f);
            bool maskedHit = probe.MaskedHit(from, to, 2f, InteractWith, ClipBits);
            bool exclOnlyHit = probe.MaskedHit(from, to, 2f, 0UL, ClipBits);
            bool zeroWithHit = probe.MaskedHit(from, to, 2f, 0UL, 0UL);

            if (engineContact)
            {
                engineBounce++;
                if (defaultHit) bounceDefaultHit++;
                if (exclOnlyHit) bounceExclOnly++;
                if (zeroWithHit) bounceZeroWith++;
                if (maskedHit)
                {
                    bounceMaskedHit++;
                }
                else if (bounceMissExamples.Count < 4)
                {
                    bounceMissExamples.Add(string.Create(CultureInfo.InvariantCulture,
                        $"row {i}: engine bounced (dv {deltaV.X:0.#},{deltaV.Y:0.#},{deltaV.Z:0.#}) but masked sweep from ({from.X:0.##},{from.Y:0.##},{from.Z:0.##}) to ({to.X:0.##},{to.Y:0.##},{to.Z:0.##}) found nothing"));
                }
            }
            else
            {
                quiet++;
                if (defaultHit) quietDefaultHit++;
                if (exclOnlyHit) quietExclOnly++;
                if (zeroWithHit) quietZeroWith++;
                if (maskedHit)
                {
                    quietMaskedHit++;
                    if (quietFalseExamples.Count < 6)
                    {
                        quietFalseExamples.Add(string.Create(CultureInfo.InvariantCulture,
                            $"row {i}: no engine contact but masked sweep hit from ({from.X:0.##},{from.Y:0.##},{from.Z:0.##}) to ({to.X:0.##},{to.Y:0.##},{to.Z:0.##}) v({rows[i - 1].Velocity.X:0.#},{rows[i - 1].Velocity.Y:0.#},{rows[i - 1].Velocity.Z:0.#})"));
                    }
                }
                else if (defaultHit)
                {
                    // The mask removed this quiet-move hit. Classify the surface
                    // the engine trace reports, so "the excluded layers are all
                    // clip brushes" is proven per capture instead of assumed.
                    string contents = ExtractContents(probe.DescribeTrace(from, to, 2f));
                    if (contents.Length > 0)
                    {
                        falseHitContents[contents] = (falseHitContents.TryGetValue(contents, out int n) ? n : 0) + 1;
                    }
                }
            }
        }

        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"mask audit: {rows.Count - 1} moves, {engineBounce} engine bounces, {quiet} quiet; " +
            $"default hit on {bounceDefaultHit}/{engineBounce} bounces and {quietDefaultHit}/{quiet} quiet moves; " +
            $"masked hit on {bounceMaskedHit}/{engineBounce} bounces and {quietMaskedHit}/{quiet} quiet moves"));
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"mask semantics: excl-only hit on {bounceExclOnly}/{engineBounce} bounces and {quietExclOnly}/{quiet} quiet; " +
            $"zero-with hit on {bounceZeroWith}/{engineBounce} bounces and {quietZeroWith}/{quiet} quiet"));
        foreach (string e in bounceMissExamples)
        {
            lines.Add("mask audit MISS: " + e);
        }
        foreach (string e in quietFalseExamples)
        {
            lines.Add("mask audit FALSE: " + e);
        }
        foreach (var kv in falseHitContents)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"mask audit false-hit surface: {kv.Value}x {kv.Key}"));
        }
        return lines;
    }

    /// <summary>Pulls the Contents field out of a raw trace dump line.</summary>
    private static string ExtractContents(string rawDump)
    {
        const string marker = "Contents=";
        int start = rawDump.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        int end = rawDump.IndexOf(" TriangleIndex", start, StringComparison.Ordinal);
        return end > start ? rawDump[start..end] : rawDump[start..];
    }
}
