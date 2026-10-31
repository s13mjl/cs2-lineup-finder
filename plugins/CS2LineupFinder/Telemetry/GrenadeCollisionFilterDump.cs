// <copyright file="GrenadeCollisionFilterDump.cs" company="CS2LineupFinder">
// Reads the live smoke projectile's own Source 2 collision-filter fields once
// at attach time, so the simulator's trace mask is copied from the engine, not
// reconstructed from documentation.
// </copyright>

using System;
using System.Globalization;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;

namespace CS2LineupFinder.Plugin.Telemetry;

/// <summary>Dumps m_nInteractsAs/With/Exclude of a live projectile.</summary>
public static class GrenadeCollisionFilterDump
{
    /// <summary>Formats the three filter fields of <paramref name="entity"/>.</summary>
    public static string Describe(CBaseEntity entity)
    {
        try
        {
            ulong Read(string prop)
            {
                var raw = Schema.GetSchemaValue<ulong>(
                    entity.Handle,
                    "CBaseEntity",
                    prop);
                return raw;
            }

            ulong interactsAs = Read("m_nInteractsAs");
            ulong interactsWith = Read("m_nInteractsWith");
            ulong interactsExclude = Read("m_nInteractsExclude");
            return string.Create(CultureInfo.InvariantCulture,
                $"grenade collision filter: m_nInteractsAs=0x{interactsAs:X} m_nInteractsWith=0x{interactsWith:X} m_nInteractsExclude=0x{interactsExclude:X}");
        }
        catch (Exception ex)
        {
            return "grenade collision filter dump failed: " + ex.Message;
        }
    }
}
