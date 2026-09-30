using System;

using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Commands;

/// <summary>
/// Shared vocabulary for the command layer: tokens players type and the text the
/// plugin answers with. Keeping the parsing here means the chat and console entry
/// points cannot drift apart, and it makes the tokens unit testable without a
/// running server.
/// </summary>
public static class CommandUsage
{
    /// <summary>Prefix shared by every chat alias, so <c>!lf_menu</c> and <c>css_lf_menu</c> are the same command.</summary>
    public const string ChatPrefix = "!lf_";

    /// <summary>Infers the command name a chat message maps to, e.g. <c>!lf_zone r 200</c> gives <c>lf_zone</c>.</summary>
    /// <param name="message">Raw chat message including the trigger character.</param>
    /// <returns>The command name without the trigger character, or an empty string.</returns>
    public static string CommandNameFromChat(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var trimmed = message.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        // CSSharp strips the public ('!') or silent ('/') trigger before the command
        // is dispatched, but a message read straight from an event still carries it.
        var start = trimmed[0] is '!' or '/' or '.' ? 1 : 0;
        var name = trimmed[start..];
        var space = name.IndexOf(' ');
        return space < 0 ? name : name[..space];
    }

    /// <summary>Parses a grenade family token.</summary>
    /// <param name="token">One of <c>smoke</c>, <c>flash</c>, <c>molotov</c>, <c>he</c>.</param>
    /// <param name="result">The parsed family.</param>
    /// <returns><see langword="true"/> when the token was recognized.</returns>
    public static bool TryParseGrenadeType(string? token, out GrenadeType result)
    {
        switch (token?.Trim().ToLowerInvariant())
        {
            case "smoke":
                result = GrenadeType.Smoke;
                return true;
            case "flash":
            case "flashbang":
                result = GrenadeType.Flash;
                return true;
            case "molotov":
            case "molo":
            case "fire":
                result = GrenadeType.Molotov;
                return true;
            case "he":
            case "frag":
                result = GrenadeType.He;
                return true;
            default:
                result = default;
                return false;
        }
    }

    /// <summary>Parses a stance token.</summary>
    /// <param name="token">One of <c>stand</c>, <c>crouch</c>, <c>jump</c>.</param>
    /// <param name="result">The parsed stance.</param>
    /// <returns><see langword="true"/> when the token was recognized.</returns>
    public static bool TryParseThrowMode(string? token, out ThrowMode result)
    {
        switch (token?.Trim().ToLowerInvariant())
        {
            case "stand":
            case "standing":
                result = ThrowMode.Stand;
                return true;
            case "crouch":
            case "crouching":
                result = ThrowMode.Crouch;
                return true;
            case "jump":
            case "jumping":
                result = ThrowMode.Jump;
                return true;
            default:
                result = default;
                return false;
        }
    }

    /// <summary>Parses a mouse button token.</summary>
    /// <param name="token">One of <c>primary</c>, <c>secondary</c>, <c>both</c>.</param>
    /// <param name="result">The parsed button.</param>
    /// <returns><see langword="true"/> when the token was recognized.</returns>
    public static bool TryParseThrowButton(string? token, out ThrowButton result)
    {
        switch (token?.Trim().ToLowerInvariant())
        {
            case "primary":
            case "left":
            case "lmb":
                result = ThrowButton.Primary;
                return true;
            case "secondary":
            case "right":
            case "rmb":
                result = ThrowButton.Secondary;
                return true;
            case "both":
            case "middle":
            case "mmb":
                result = ThrowButton.Both;
                return true;
            default:
                result = default;
                return false;
        }
    }

    /// <summary>Renders the usage text a player sees after a bad argument, e.g. <c>!lf_zone circle &lt;radius&gt;</c>.</summary>
    /// <param name="consoleName">Console command name including the <c>css_</c> prefix.</param>
    /// <returns>The usage line, with the chat alias so it can be pasted into chat.</returns>
    public static string UsageFor(string consoleName)
    {
        var spec = CommandSpec.Find(consoleName);
        return spec is null ? string.Empty : CommandSpec.ChatPrefix + spec.Value.Usage;
    }

    /// <summary>Chat token for a grenade family, the inverse of <see cref="TryParseGrenadeType"/>.</summary>
    /// <param name="type">Family to format.</param>
    /// <returns>The token players type.</returns>
    public static string Token(GrenadeType type) => type switch
    {
        GrenadeType.Smoke => "smoke",
        GrenadeType.Flash => "flash",
        GrenadeType.Molotov => "molotov",
        _ => "he",
    };

    /// <summary>Chat token for a stance, the inverse of <see cref="TryParseThrowMode"/>.</summary>
    /// <param name="mode">Stance to format.</param>
    /// <returns>The token players type.</returns>
    public static string Token(ThrowMode mode) => mode switch
    {
        ThrowMode.Crouch => "crouch",
        ThrowMode.Jump => "jump",
        _ => "stand",
    };

    /// <summary>Chat token for a mouse button, the inverse of <see cref="TryParseThrowButton"/>.</summary>
    /// <param name="button">Button to format.</param>
    /// <returns>The token players type.</returns>
    public static string Token(ThrowButton button) => button switch
    {
        ThrowButton.Secondary => "secondary",
        ThrowButton.Both => "both",
        _ => "primary",
    };
}
