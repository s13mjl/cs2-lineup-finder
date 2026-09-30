using System;
using CS2LineupFinder.Contracts;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace CS2LineupFinder.Plugin.Game;

/// <summary>Looks up the controllers and pawns a command is about to act on.</summary>
/// <remarks>
/// Every lookup is defensive: a player can disconnect between the command being
/// typed and it being handled, and CSSharp hands back an object with a stale handle
/// rather than null. Callers therefore always check, so no command can trip over a
/// half removed player.
/// </remarks>
public static class EnginePlayers
{
    /// <summary>Returns the controller for a slot.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <returns>The controller, or <see langword="null"/> when the slot is empty.</returns>
    public static CCSPlayerController? Controller(int playerSlot)
    {
        if (playerSlot < 0)
        {
            return null;
        }

        try
        {
            var controller = Utilities.GetPlayerFromSlot(playerSlot);
            return controller is { IsValid: true } && controller.Connected == PlayerConnectedState.Connected
                ? controller
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Returns the live pawn for a slot.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <returns>The pawn, or <see langword="null"/> when the player is dead or gone.</returns>
    public static CCSPlayerPawn? Pawn(int playerSlot)
    {
        try
        {
            var pawn = Controller(playerSlot)?.PlayerPawn.Value;
            return pawn is { IsValid: true } ? pawn : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Returns the display name a saved line-up should credit.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <returns>The player name, or an empty string when the slot is empty.</returns>
    public static string Name(int playerSlot)
    {
        try
        {
            return Controller(playerSlot)?.PlayerName ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
