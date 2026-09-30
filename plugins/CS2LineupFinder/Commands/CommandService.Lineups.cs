using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Persistence;

namespace CS2LineupFinder.Plugin.Commands;

/// <summary>
/// The half of the command layer that owns the save file: <c>css_lf_save</c>,
/// <c>css_lf_load</c> and <c>css_lf_list</c>.
/// </summary>
/// <remarks>
/// A saved line-up is the whole session plus the angles that were found, so loading
/// one puts the player's session back into the state it had when they saved it and
/// makes the stored angles the current result. That is what makes
/// <c>css_lf_save</c> then <c>css_lf_load</c> a round trip: everything a search
/// reads is restored, and the angles the player is shown are the saved ones.
/// </remarks>
public sealed partial class CommandService
{
    /// <summary>Stores the player's currently selected solution under a name.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="name">Line-up name typed by the player.</param>
    /// <returns>The path written, or <see langword="null"/> when nothing was saved.</returns>
    public string? SaveLineup(int playerSlot, string? name)
    {
        var session = _sessions.GetOrCreate(playerSlot);
        var safe = LineupRepository.Sanitize(name ?? string.Empty);
        if (safe.Length == 0)
        {
            Reply(playerSlot, Phrases.Id.BadName);
            return null;
        }

        var solution = session.SelectedSolution;
        if (solution is null)
        {
            Reply(playerSlot, Phrases.Id.NothingToSave);
            return null;
        }

        var record = LineupRecord.FromSolution(safe, MapFor(playerSlot), session, solution, _clock.UtcNow);

        try
        {
            var path = _lineups.Save(record);
            Reply(playerSlot, Phrases.Id.Saved, record.Name, path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _messages.LogWarning($"Failed to save line-up '{safe}': {ex.Message}");
            Reply(playerSlot, Phrases.Id.SaveFailed, ex.Message);
            return null;
        }
    }

    /// <summary>Restores a saved line-up into the player's session.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="name">Line-up name typed by the player.</param>
    /// <param name="mapName">Map to look under; the session's map is used when empty.</param>
    /// <returns>The restored record, or <see langword="null"/> when it was not found.</returns>
    public LineupRecord? LoadLineup(int playerSlot, string? name, string mapName)
    {
        var safe = LineupRepository.Sanitize(name ?? string.Empty);
        if (safe.Length == 0)
        {
            Reply(playerSlot, Phrases.Id.BadName);
            return null;
        }

        try
        {
            var map = string.IsNullOrWhiteSpace(mapName) ? MapFor(playerSlot) : mapName;
            var record = _lineups.Load(map, safe);
            if (record is null)
            {
                Reply(playerSlot, Phrases.Id.NotFound, safe);
                return null;
            }

            var session = _sessions.GetOrCreate(playerSlot);
            session.ThrowPoint = record.Origin.ToVec3();
            session.EyePoint = record.Origin.ToVec3() + new Vec3(0f, 0f, LineupRecord.DefaultEyeHeight);
            session.Zone = record.Zone.ToGroundZone();
            session.GrenadeType = record.GrenadeType;
            session.ThrowMode = record.ThrowMode;
            session.Button = record.Button;
            session.MarkingMode = true;

            var request = record.ToRequest(map);
            session.LastOutcome = request is null
                ? null
                : SolverOutcome.Success(
                    new[]
                    {
                        new LineupSolution
                        {
                            Yaw = record.Yaw,
                            Pitch = record.Pitch,
                            ReleasePosition = request.Origin.Eyes,
                            ImpactPosition = record.Zone.ToGroundZone().Center,
                            TargetDistance = 0f,
                            Note = "saved " + record.CreatedAt.UtcDateTime.ToString("u", System.Globalization.CultureInfo.InvariantCulture),
                        },
                    },
                    TimeSpan.Zero);
            session.SelectedResultIndex = 0;

            Reply(playerSlot, Phrases.Id.Loaded, record.Name, CommandParser.RoundAngle(record.Yaw), CommandParser.RoundAngle(record.Pitch));

            if (request is not null && request.TargetZone is not null)
            {
                Visuals.DrawZone(request.TargetZone, Config.BeamDurationSeconds);
                DrawResult(playerSlot, request, session.SelectedSolution!);
            }

            return record;
        }
        catch (JsonException ex)
        {
            _messages.LogWarning($"Line-up '{safe}' is not readable: {ex.Message}");
            Reply(playerSlot, Phrases.Id.LoadFailed, ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _messages.LogWarning($"Line-up '{safe}' could not be read: {ex.Message}");
            Reply(playerSlot, Phrases.Id.LoadFailed, ex.Message);
            return null;
        }
    }

    /// <summary>Lists the line-ups saved for a map.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="mapName">Map to list, or empty to use the player's current map.</param>
    /// <param name="limit">Maximum number of entries to print in chat.</param>
    /// <returns>The records that were found.</returns>
    public IReadOnlyList<LineupRecord> ListLineups(int playerSlot, string mapName, int limit = 20)
    {
        IReadOnlyList<LineupRecord> records;
        try
        {
            records = _lineups.List(mapName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _messages.LogWarning($"Line-up list for '{mapName}' failed: {ex.Message}");
            Reply(playerSlot, Phrases.Id.LoadFailed, ex.Message);
            return Array.Empty<LineupRecord>();
        }

        Reply(playerSlot, Phrases.Id.ListHeader, records.Count, mapName);

        var shown = Math.Min(records.Count, Math.Max(limit, 0));
        for (var i = 0; i < shown; i++)
        {
            var record = records[i];
            var detail = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{CommandUsage.Token(record.GrenadeType)} {CommandUsage.Token(record.ThrowMode)}/{CommandUsage.Token(record.Button)} yaw {CommandParser.RoundAngle(record.Yaw):0.0} pitch {CommandParser.RoundAngle(record.Pitch):0.0} by {record.Author}");
            Reply(playerSlot, Phrases.Id.ListEntry, record.Name, detail);
        }

        return records;
    }
}
