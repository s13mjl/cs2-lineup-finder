// <copyright file="PawnCollisionProbe.cs" company="CS2LineupFinder">
// Startup diagnostic: asks a live pawn which collision layers its hull reports,
// and sweeps masked rays at it, so the simulator's mask is judged against an
// entity the engine actually collides grenades with - measured, not assumed.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using CounterStrikeSharp.API;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Game;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2LineupFinder.Plugin.Diagnostics;

/// <summary>
/// On startup the practice server spawns bots before the replay runs, so at
/// least one <c>CCSPlayerPawn</c> is alive. This probe reads the pawn's own
/// <c>m_nInteracts*</c> schema fields (the engine's authoritative filter bits)
/// and then sweeps a masked sphere at the pawn's hull from a fixed offset,
/// reporting DidHit for each mask candidate. Lines go to the plugin log.
/// </summary>
public static class PawnCollisionProbe
{
    /// <summary>Runs the probe and returns one log line per finding.</summary>
    public static IReadOnlyList<string> Run(ITraceBackend backend)
    {
        var lines = new List<string>();
        try
        {
            CCSPlayerPawn? pawn = null;
            foreach (var slot in EnumerableRange(1, 13))
            {
                CCSPlayerPawn? candidate;
                try
                {
                    candidate = Utilities.GetEntityFromIndex<CCSPlayerPawn>(slot);
                }
                catch
                {
                    continue;
                }

                if (candidate is { IsValid: true, AbsOrigin: not null })
                {
                    pawn = candidate;
                    break;
                }
            }

            if (pawn is null)
            {
                lines.Add("pawn probe: no valid pawn found (no bots spawned yet)");
                return lines;
            }

            var origin = pawn.AbsOrigin!;
            ulong asMask = ReadSchema(pawn, "m_nInteractsAs");
            ulong withMask = ReadSchema(pawn, "m_nInteractsWith");
            ulong excludeMask = ReadSchema(pawn, "m_nInteractsExclude");
            ulong solidFlags = ReadSchema(pawn, "m_nSolidType");
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"pawn probe: pawn slot {pawn.Index} origin=({origin.X:0.##},{origin.Y:0.##},{origin.Z:0.##}) " +
                $"m_nInteractsAs=0x{asMask:X} m_nInteractsWith=0x{withMask:X} m_nInteractsExclude=0x{excludeMask:X} m_nSolidType=0x{solidFlags:X}"));

            // Sweep down onto the pawn's capsule from 16u above its origin with
            // a grenade-sized sphere; also sweep horizontally at origin height.
            void Report(string label, Vec3 start, Vec3 end, ulong with, ulong excl)
            {
                if (!backend.SupportsMaskedTrace)
                {
                    lines.Add($"pawn probe {label}: backend has no masked trace");
                    return;
                }

                var sample = backend.Trace(start, end, 2f, -1, with, excl);
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"pawn probe {label}: mask with=0x{with:X} excl=0x{excl:X} didHit={sample.DidHit} frac={sample.Fraction.ToString("0.####", CultureInfo.InvariantCulture)}"));
            }

            Vec3 V(float x, float y, float z) => new(x, y, z);
            var down = V(origin.X, origin.Y, origin.Z + 16f);
            var downEnd = V(origin.X, origin.Y, origin.Z - 8f);
            Report("down grenade-mask", down, downEnd, GrenadeCollisionMasks.InteractWith, GrenadeCollisionMasks.InteractExclude);
            Report("down with-only-solid", down, downEnd, 0x1UL, 0UL);
            Report("down with-clip-bits", down, downEnd, 0x30UL, 0UL);
            Report("down with-solid|clip", down, downEnd, 0x31UL, 0UL);
            Report("down with-clip|wg|sl", down, downEnd, 0x40004030UL, 0UL);
            Report("down default", down, downEnd, 0UL, 0UL);

            var side = V(origin.X - 24f, origin.Y, origin.Z);
            var sideEnd = V(origin.X + 24f, origin.Y, origin.Z);
            Report("side grenade-mask", side, sideEnd, GrenadeCollisionMasks.InteractWith, GrenadeCollisionMasks.InteractExclude);
            Report("side with-clip-bits", side, sideEnd, 0x30UL, 0UL);
            Report("side default", side, sideEnd, 0UL, 0UL);
        }
        catch (Exception ex)
        {
            lines.Add("pawn probe failed: " + ex.Message);
        }

        return lines;
    }

    private static IEnumerable<int> EnumerableRange(int start, int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return start + i;
        }
    }

    private static ulong ReadSchema(CBaseEntity entity, string prop)
    {
        try
        {
            return Schema.GetSchemaValue<ulong>(entity.Handle, "CBaseEntity", prop);
        }
        catch
        {
            return 0;
        }
    }
}
