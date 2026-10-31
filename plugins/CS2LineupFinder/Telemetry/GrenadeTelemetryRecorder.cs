// <copyright file="GrenadeTelemetryRecorder.cs" company="CS2LineupFinder">
// Records the real server-side grenade kinematics tick by tick, turning every
// previously guessed physics constant into a measurable quantity.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Telemetry;

/// <summary>
/// One recorded tick of a live projectile: server-authoritative position and
/// velocity read straight off the entity on the game thread.
/// </summary>
public readonly record struct TelemetryTick(float Time, Vec3 Position, Vec3 Velocity);

/// <summary>
/// Buffers every tick of one live smoke projectile and writes a CSV plus a JSON
/// summary when the grenade detonates. Bounce times are derived from velocity
/// discontinuities: a ground contact shows up as the vertical velocity reversing
/// or dying within one tick while horizontal speed carries on, which a total
/// speed ratio would miss on shallow throws. The summary fits the flight
/// gravity from the pre-contact vertical velocity series by least squares and
/// records the vertical rebound ratio of every contact (on a flat floor this is
/// exactly the restitution, expected 0.45 per the leaked source).
/// </summary>
public sealed class GrenadeTelemetryRecorder
{
    /// <summary>
    /// Vertical speed (u/s) below which a tick no longer counts as descending;
    /// pure sliding has no vertical motion to reverse.
    /// </summary>
    private const float DescendingVzThreshold = 1f;

    /// <summary>
    /// A one-tick vertical velocity gain larger than this fraction of the
    /// descending speed marks a ground contact: a rebound recovers at least the
    /// restitution share and a slide stops the descent, while free fall only
    /// changes vz by gravity/64 (~5 u/s, far under 50% of any real impact
    /// speed). Wall bounces leave vz alone and are deliberately not contacts.
    /// </summary>
    private const float ContactRecoveryFraction = 0.5f;

    private sealed record ContactSample(
        int TickIndex, float PreSpeed, float PostSpeed, float PreVz, float PostVz);

    private readonly List<TelemetryTick> _ticks = new(256);
    private readonly List<ContactSample> _contacts = new(8);
    private int _lastRecordedEngineTick = -1;
    private readonly string _directory;
    private readonly Action<string> _log;

    internal GrenadeTelemetryRecorder(string directory, Action<string> log)
    {
        _directory = directory;
        _log = log;
    }

    /// <summary>Number of ticks recorded so far for the live projectile.</summary>
    internal int Count => _ticks.Count;

    /// <summary>
    /// Appends one tick sample from the live entity. Samples whose engine tick
    /// equals the previous one are dropped: the 2026-10-15 captures showed the
    /// caller firing twice per engine tick, which stretched the time axis 2x
    /// and halved the least-squares gravity fit. The recorder-side guard keeps
    /// the analyser's time axis honest even if a future caller reintroduces a
    /// double trigger.
    /// </summary>
    internal void Record(float time, Vec3 position, Vec3 velocity, int engineTick = -1)
    {
        if (engineTick >= 0)
        {
            if (engineTick == _lastRecordedEngineTick)
            {
                return;
            }

            _lastRecordedEngineTick = engineTick;
        }

        _ticks.Add(new TelemetryTick(time, position, velocity));
    }

    /// <summary>
    /// Writes the recording to disk and returns the derived summary, or null
    /// when the capture is too short to be useful (fewer than 8 ticks).
    /// </summary>
    internal string? Flush(int index, Vec3 endPosition)
    {
        if (_ticks.Count < 8)
        {
            _log($"telemetry #{index}: capture too short ({_ticks.Count} ticks), discarded");
            return null;
        }

        try
        {
            Directory.CreateDirectory(_directory);

            // The plugin's counter restarts at zero on every load, so writing
            // telemetry-{index} directly silently overwrote the previous
            // session's captures (the 2026-10-15 23:54 palace flight was lost
            // to the 2026-10-18 capture that reused telemetry-001). Pick the
            // first free index instead: existing files are evidence and are
            // never replaced.
            int actualIndex = NextFreeIndex(index);
            string csvPath = Path.Combine(_directory, $"telemetry-{actualIndex:D3}.csv");
            var csv = new StringBuilder(_ticks.Count * 96);
            csv.AppendLine("time,px,py,pz,vx,vy,vz,speed");
            foreach (var t in _ticks)
            {
                csv.Append(t.Time.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
                csv.Append(t.Position.X.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
                csv.Append(t.Position.Y.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
                csv.Append(t.Position.Z.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
                csv.Append(t.Velocity.X.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
                csv.Append(t.Velocity.Y.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
                csv.Append(t.Velocity.Z.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
                csv.Append(t.Velocity.Length.ToString("0.0000", CultureInfo.InvariantCulture)).Append('\n');
            }

            File.WriteAllText(csvPath, csv.ToString());
            var summary = Analyse(actualIndex, Path.GetFileName(csvPath), endPosition);
            string json = System.Text.Json.JsonSerializer.Serialize(
                summary,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(_directory, $"telemetry-{actualIndex:D3}.json"), json);
            _log(
                $"telemetry #{actualIndex}: {summary.TickCount} ticks, launch {summary.LaunchSpeed:0.#} u/s, " +
                $"impact {summary.ImpactSpeed:0.#} -> {summary.PostImpactSpeed:0.#} u/s at t={summary.ImpactTime:0.000}s, " +
                $"rebound ratio {summary.VerticalReboundRatio?.ToString("0.00") ?? "n/a"}, bounces {summary.BounceCount}, " +
                $"fitted gravity {summary.FittedGravity:0.#} u/s^2, roll-end {summary.RollEndSpeed:0.#} u/s, last tick {summary.FlightTime:0.000}s");
            return summary.File;
        }
        catch (Exception ex)
        {
            _log($"telemetry write failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the first telemetry index whose CSV and JSON do not exist yet.
    /// Existing captures are never overwritten: the recorder's index restarts
    /// with the plugin, while the files are the project's physics evidence.
    /// </summary>
    private int NextFreeIndex(int requested)
    {
        int index = Math.Max(1, requested);
        while (File.Exists(Path.Combine(_directory, $"telemetry-{index:D3}.csv"))
            || File.Exists(Path.Combine(_directory, $"telemetry-{index:D3}.json")))
        {
            index++;
        }

        return index;
    }

    /// <summary>Derives every measured constant the CSV supports.</summary>
    private TelemetrySummary Analyse(int index, string file, Vec3 endPosition)
    {
        DetectContacts();
        int firstContact = _contacts.Count > 0 ? _contacts[0].TickIndex : _ticks.Count - 1;
        float fittedGravity = FitGravity(firstContact);
        ContactSample? first = _contacts.Count > 0 ? _contacts[0] : null;
        float? reboundRatio = first is null
            ? null
            : (-first.PreVz) > 0f ? first.PostVz / -first.PreVz : 0f;

        // ImpactTime reports the timestamp of the first post-contact tick, while
        // FlightTime reports the last pre-contact tick. Indexing the contact tick
        // itself (+1) runs off the end of the array when the capture ends on the
        // contact, which produced "impact after last tick" inconsistencies.
        int impactTick = first is null ? 0 : Math.Min(first.TickIndex + 1, _ticks.Count - 1);
        var summary = new TelemetrySummary
        {
            Index = index,
            File = file,
            TickCount = _ticks.Count,
            Launch = _ticks[0].Position,
            LaunchSpeed = _ticks[0].Velocity.Length,
            ImpactTime = _ticks[impactTick].Time,
            ImpactSpeed = first?.PreSpeed ?? 0f,
            PostImpactSpeed = first?.PostSpeed ?? 0f,
            VerticalReboundRatio = reboundRatio,
            BounceCount = _contacts.Count,
            FittedGravity = fittedGravity,
            End = endPosition,
            RollEndSpeed = _ticks[^1].Velocity.Length,
            FlightTime = _ticks[firstContact].Time,
        };
        return summary;
    }

    /// <summary>
    /// Finds every ground contact: the vertical velocity gains more than half
    /// of its descending magnitude within one tick. A rebound recovers at least
    /// the restitution share, a slide ends the descent, and free fall alone
    /// changes vz by only gravity/64 per tick (~5 u/s), far under the fraction
    /// for any real impact speed.
    /// </summary>
    private void DetectContacts()
    {
        _contacts.Clear();
        for (var i = 1; i < _ticks.Count; i++)
        {
            float preVz = _ticks[i - 1].Velocity.Z;
            float postVz = _ticks[i].Velocity.Z;
            if (preVz >= -DescendingVzThreshold)
            {
                continue;
            }

            if ((postVz - preVz) > (ContactRecoveryFraction * -preVz))
            {
                _contacts.Add(new ContactSample(
                    i - 1,
                    _ticks[i - 1].Velocity.Length,
                    _ticks[i].Velocity.Length,
                    preVz,
                    postVz));
            }
        }
    }

    /// <summary>
    /// Least-squares slope of vz over t up to <paramref name="lastFreeTick"/>,
    /// which under constant acceleration is the negative gravity. Restricting
    /// to the free-flight segment keeps bounces out of the fit.
    /// </summary>
    private float FitGravity(int lastFreeTick)
    {
        if (lastFreeTick < 4)
        {
            return 0f;
        }

        double sumT = 0, sumV = 0, sumTT = 0, sumTV = 0;
        var n = lastFreeTick + 1;
        for (var i = 0; i <= lastFreeTick; i++)
        {
            double t = _ticks[i].Time;
            double vz = _ticks[i].Velocity.Z;
            sumT += t;
            sumV += vz;
            sumTT += t * t;
            sumTV += t * vz;
        }

        double denominator = (n * sumTT) - (sumT * sumT);
        if (Math.Abs(denominator) < 1e-9)
        {
            return 0f;
        }

        double slope = ((n * sumTV) - (sumT * sumV)) / denominator;
        return (float)(-slope);
    }
}

/// <summary>Derived single-throw summary written next to the raw CSV.</summary>
public sealed class TelemetrySummary
{
    /// <summary>Sample index, matches the CSV file name.</summary>
    public int Index { get; init; }

    /// <summary>CSV file name the summary belongs to.</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>Tick samples recorded.</summary>
    public int TickCount { get; init; }

    /// <summary>First recorded position (release).</summary>
    public Vec3 Launch { get; init; }

    /// <summary>Speed on the first recorded tick, u/s.</summary>
    public float LaunchSpeed { get; init; }

    /// <summary>Speed on the last free-flight tick before the first bounce, u/s.</summary>
    public float ImpactSpeed { get; init; }

    /// <summary>Speed on the first tick after the first bounce, u/s.</summary>
    public float PostImpactSpeed { get; init; }

    /// <summary>
    /// Vertical rebound ratio at the first ground contact (vz_after / -vz_before).
    /// On a flat floor this equals the restitution (leaked source: 0.45); on a
    /// slope it is a lower-bound style readout and should be judged against the
    /// map geometry.
    /// </summary>
    public float? VerticalReboundRatio { get; init; }

    /// <summary>Number of ground contacts detected before detonation.</summary>
    public int BounceCount { get; init; }

    /// <summary>Speed on the final recorded tick (rolling/stop regime).</summary>
    public float RollEndSpeed { get; init; }

    /// <summary>Time of the first bounce since capture start, seconds.</summary>
    public float ImpactTime { get; init; }

    /// <summary>Least-squares gravity fit over the free-flight segment, u/s^2.</summary>
    public float FittedGravity { get; init; }

    /// <summary>Time of the last recorded tick, seconds.</summary>
    public float FlightTime { get; init; }

    /// <summary>End position: detonation point, or last seen position.</summary>
    public Vec3 End { get; init; }
}
