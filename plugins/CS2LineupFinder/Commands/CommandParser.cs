using System;
using System.Collections.Generic;
using System.Globalization;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Commands;

/// <summary>
/// Command grammar that does not touch the engine: parsing <c>css_lf_zone</c>
/// arguments, formatting angles for chat and rounding a solution to the precision
/// the brief asks for. Kept separate so it can be unit tested directly.
/// </summary>
public static class CommandParser
{
    /// <summary>Precision used when showing view angles to players, in degrees.</summary>
    public const int AngleDecimals = 1;

    /// <summary>Parses the arguments of <c>css_lf_zone</c>.</summary>
    /// <param name="arguments">Tokens after the command name.</param>
    /// <param name="defaultRadius">Radius used when the player omits one.</param>
    /// <param name="zone">The parsed zone shape, without a centre.</param>
    /// <param name="error">Localized error text when parsing failed.</param>
    /// <returns><see langword="true"/> when the arguments describe a zone.</returns>
    public static bool TryParseZone(
        IReadOnlyList<string> arguments,
        float defaultRadius,
        out GroundZone? zone,
        out string? error)
    {
        zone = null;
        error = null;

        if (arguments.Count == 0)
        {
            error = "zone circle <radius> | zone rect <width> <height>";
            return false;
        }

        var shape = arguments[0].Trim().ToLowerInvariant();
        switch (shape)
        {
            case "circle":
            case "c":
            case "r":
            case "radius":
            {
                var radius = arguments.Count >= 2 ? ParsePositive(arguments[1]) : defaultRadius;
                if (radius is null)
                {
                    return false;
                }

                zone = new GroundZone { Type = GroundZoneType.Circle, Center = Vec3.Zero, Radius = radius.Value };
                return true;
            }

            case "rect":
            case "rectangle":
            {
                if (arguments.Count < 2)
                {
                    error = "zone rect <width> <height>";
                    return false;
                }

                var width = ParsePositive(arguments[1]);
                var height = arguments.Count >= 3 ? ParsePositive(arguments[2]) : width;
                if (width is null || height is null)
                {
                    return false;
                }

                zone = new GroundZone
                {
                    Type = GroundZoneType.Rectangle,
                    Center = Vec3.Zero,
                    Width = width.Value,
                    Height = height.Value,
                };
                return true;
            }

            default:
                error = shape;
                return false;
        }
    }

    /// <summary>Parses a strictly positive float using the invariant culture.</summary>
    /// <param name="token">Text to parse.</param>
    /// <returns>The value, or <see langword="null"/> when it is absent, negative or zero.</returns>
    public static float? ParsePositive(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (!float.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value) ? value : null;
    }

    /// <summary>Rounds an angle to the precision shown to players.</summary>
    /// <param name="degrees">Raw angle.</param>
    /// <returns>The angle rounded to one decimal place.</returns>
    public static float RoundAngle(float degrees) => MathF.Round(degrees, AngleDecimals, MidpointRounding.AwayFromZero);

    /// <summary>Formats an angle in degrees, e.g. <c>yaw -127.5°</c>.</summary>
    /// <param name="label">Label placed before the value.</param>
    /// <param name="degrees">Angle to format.</param>
    /// <returns>The formatted angle.</returns>
    public static string FormatAngle(string label, float degrees) => string.Create(
        CultureInfo.InvariantCulture,
        $"{label} {RoundAngle(degrees):0.0}");

    /// <summary>Formats a distance in world units.</summary>
    /// <param name="units">Distance in units.</param>
    /// <returns>The formatted distance.</returns>
    public static string FormatUnits(float units) => string.Create(CultureInfo.InvariantCulture, $"{units:0.0}u");

    /// <summary>Normalizes a yaw into the -180..180 range the contract uses.</summary>
    /// <param name="yaw">Raw yaw in degrees.</param>
    /// <returns>The normalized yaw.</returns>
    public static float NormalizeYaw(float yaw)
    {
        var wrapped = yaw % 360f;
        if (wrapped > 180f)
        {
            wrapped -= 360f;
        }
        else if (wrapped < -180f)
        {
            wrapped += 360f;
        }

        return wrapped;
    }

    /// <summary>Builds the unit view direction a yaw/pitch pair looks along.</summary>
    /// <param name="yawDegrees">View yaw in degrees.</param>
    /// <param name="pitchDegrees">View pitch in degrees, positive looks up.</param>
    /// <returns>The unit direction.</returns>
    public static Vec3 DirectionFromAngles(float yawDegrees, float pitchDegrees)
    {
        var yaw = yawDegrees * MathF.PI / 180f;
        var pitch = pitchDegrees * MathF.PI / 180f;
        var cosPitch = MathF.Cos(pitch);

        return new Vec3(
            cosPitch * MathF.Cos(yaw),
            cosPitch * MathF.Sin(yaw),
            MathF.Sin(pitch)).Normalized();
    }

    /// <summary>
    /// Builds the view direction from the angles a player reads off the scoreboard,
    /// where yaw is counter-clockwise from east and positive pitch looks down.
    /// </summary>
    /// <param name="yawDegrees">Player yaw in degrees.</param>
    /// <param name="pitchDegrees">Player pitch in degrees, positive looks down.</param>
    /// <returns>The unit direction, in the contract's positive-up convention.</returns>
    public static Vec3 DirectionFromPlayerAngles(float yawDegrees, float pitchDegrees) =>
        DirectionFromAngles(yawDegrees, -pitchDegrees);

    /// <summary>Converts a contract pitch into the player convention the engine uses.</summary>
    /// <param name="pitchDegrees">Contract pitch, positive looks up.</param>
    /// <returns>Player pitch, positive looks down.</returns>
    public static float ToPlayerPitch(float pitchDegrees) => -pitchDegrees;

    /// <summary>Converts a player pitch into the contract convention.</summary>
    /// <param name="pitchDegrees">Player pitch, positive looks down.</param>
    /// <returns>Contract pitch, positive looks up.</returns>
    public static float FromPlayerPitch(float pitchDegrees) => -pitchDegrees;

    /// <summary>Renders a world point for chat output.</summary>
    /// <param name="point">Point to render.</param>
    /// <returns>The formatted point.</returns>
    public static string FormatPoint(Vec3 point) => string.Create(
        CultureInfo.InvariantCulture,
        $"({point.X:0}, {point.Y:0}, {point.Z:0})");
}
