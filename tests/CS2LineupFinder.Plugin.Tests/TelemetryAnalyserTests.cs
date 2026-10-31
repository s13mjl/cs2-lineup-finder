// <copyright file="TelemetryAnalyserTests.cs" company="CS2LineupFinder">
// Feeds the telemetry analyser synthetic trajectories with analytically known
// gravity, restitution and shallow-bounce geometry, so the in-game readouts can
// be trusted before any real throw is measured.
// </copyright>

using System;
using System.IO;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Telemetry;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Plugin.Tests;

public sealed class TelemetryAnalyserTests
{
    private readonly ITestOutputHelper _output;

    public TelemetryAnalyserTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static GrenadeTelemetryRecorder Recorder(string dir)
        => new(dir, text => { });

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cs2lf-telemetry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Builds tick data for pure vertical free fall from rest height z0.</summary>
    private static (float[] Times, Vec3[] Positions, Vec3[] Velocities) VerticalFall(float g, int ticks)
    {
        var times = new float[ticks];
        var positions = new Vec3[ticks];
        var velocities = new Vec3[ticks];
        const float dt = 1f / 64f;
        float v = 0f;
        float z = 1000f;
        for (int i = 0; i < ticks; i++)
        {
            times[i] = i * dt;
            positions[i] = new Vec3(0f, 0f, z);
            velocities[i] = new Vec3(0f, 0f, v);
            v -= g * dt;
            z += v * dt;
        }

        return (times, positions, velocities);
    }

    [Fact]
    public void VerticalFall_FitRecoversGravity()
    {
        string dir = TempDir();
        GrenadeTelemetryRecorder recorder = Recorder(dir);
        const float g = 320f;
        var (times, positions, velocities) = VerticalFall(g, 40);
        for (int i = 0; i < times.Length; i++)
        {
            recorder.Record(times[i], positions[i], velocities[i]);
        }

        string? file = recorder.Flush(1, positions[^1]);
        Assert.NotNull(file);
        string json = File.ReadAllText(Path.Combine(dir, "telemetry-001.json"));
        _output.WriteLine(json);
        Assert.Contains("\"FittedGravity\": 3", json);
    }

    [Fact]
    public void ExistingCapture_IsNeverOverwritten_NextFreeIndexIsUsed()
    {
        // The plugin's index restarts with the process. Writing telemetry-001
        // blindly overwrote the 2026-10-15 23:54 palace capture with the
        // 2026-10-18 throw; the recorder must pick the first free index.
        string dir = TempDir();
        string first = Path.Combine(dir, "telemetry-001.csv");
        File.WriteAllText(first, "old capture");

        GrenadeTelemetryRecorder recorder = Recorder(dir);
        var (times, positions, velocities) = VerticalFall(320f, 40);
        for (int i = 0; i < times.Length; i++)
        {
            recorder.Record(times[i], positions[i], velocities[i]);
        }

        string? file = recorder.Flush(1, positions[^1]);

        Assert.NotNull(file);
        Assert.Equal("telemetry-002.csv", Path.GetFileName(file));
        Assert.Equal("old capture", File.ReadAllText(first));
        Assert.True(File.Exists(Path.Combine(dir, "telemetry-002.json")));
    }

    [Fact]
    public void SameTickDuplicateRecord_IsDropped_TimeAxisStaysHonest()
    {
        // Regression for the 2026-10-15 server incident: OnTick fired twice per
        // engine tick, so every recorded state appeared twice in the CSV. The
        // duplicated time axis halved the least-squares gravity fit to 160.
        // The recorder now drops same-tick duplicates at the source.
        string dir = TempDir();
        GrenadeTelemetryRecorder recorder = Recorder(dir);
        const float g = 320f;
        const float dt = 1f / 64f;
        float v = 0f;
        float z = 1000f;
        int engineTick = 1000;
        for (int i = 0; i < 20; i++)
        {
            var position = new Vec3(0f, 0f, z);
            var velocity = new Vec3(0f, 0f, v);
            // Real tick followed by a duplicate invocation with the same state.
            recorder.Record(i * dt, position, velocity, engineTick);
            recorder.Record(i * dt, position, velocity, engineTick);
            engineTick++;
            v -= g * dt;
            z += v * dt;
        }

        string? file = recorder.Flush(9, new Vec3(0f, 0f, z));
        Assert.NotNull(file);
        string json = File.ReadAllText(Path.Combine(dir, "telemetry-009.json"));
        _output.WriteLine(json);

        // 40 recorded calls collapse to 20 states, and the fitted gravity is
        // the true 320, not the doubled-timeline 160.
        Assert.Contains("\"TickCount\": 20", json);
        Assert.DoesNotContain("\"FittedGravity\": 16", json);
        Assert.Contains("\"FittedGravity\": 3", json);
    }

    [Fact]
    public void ShallowBounce_TotalSpeedRatioNearOne_StillDetected()
    {
        // 675 u/s at pitch -5 degrees: vz about -59 u/s, horizontal about 672 u/s.
        // After a 0.45 vertical rebound the total speed ratio is ~0.997, which
        // the old detector (ratio <= 0.75) missed entirely.
        string dir = TempDir();
        GrenadeTelemetryRecorder recorder = Recorder(dir);
        const float dt = 1f / 64f;
        const float g = 320f;
        var speed = new Vec3(669.7f, 0f, -58.8f);
        var pos = new Vec3(0f, 0f, 60f);
        int contactIndex = -1;
        for (int i = 0; i < 45; i++)
        {
            recorder.Record(i * dt, pos, speed);
            if (contactIndex < 0 && pos.Z + (speed.Z * dt) <= 0f)
            {
                // Ground contact this tick: reflect vz with restitution 0.45.
                speed = new Vec3(speed.X, speed.Y, -speed.Z * 0.45f);
                contactIndex = i;
            }

            pos += speed * dt;
            speed -= new Vec3(0f, 0f, g * dt);
        }

        Assert.True(contactIndex >= 0, "synthetic trajectory never touched the floor");
        string? file = recorder.Flush(2, pos);
        Assert.NotNull(file);
        string json = File.ReadAllText(Path.Combine(dir, "telemetry-002.json"));
        _output.WriteLine(json);

        // The rebound ratio must be recovered (0.45) even though the total
        // speed barely changes across the contact.
        Assert.Contains("\"VerticalReboundRatio\": 0.4", json);
        Assert.Contains("\"BounceCount\": 1", json);

        // And the gravity fit must stay in the pre-contact free-flight segment.
        Assert.Contains("\"FittedGravity\": 3", json);
    }

    [Fact]
    public void RollingStop_ContactDetectedWithFlatTail()
    {
        // Grenade thrown level, lands, then slides at constant speed: the
        // contact is the vz reversal to zero, the roll is the flat tail.
        string dir = TempDir();
        GrenadeTelemetryRecorder recorder = Recorder(dir);
        const float dt = 1f / 64f;
        const float g = 320f;
        var speed = new Vec3(400f, 0f, -20f);
        var pos = new Vec3(0f, 0f, 40f);
        bool grounded = false;
        for (int i = 0; i < 30; i++)
        {
            recorder.Record(i * dt, pos, speed);
            if (pos.Z + (speed.Z * dt) <= 0f)
            {
                speed = new Vec3(speed.X, speed.Y, 0f);
                grounded = true;
            }

            pos += speed * dt;
            if (!grounded)
            {
                speed -= new Vec3(0f, 0f, g * dt);
            }
        }

        string? file = recorder.Flush(3, pos);
        Assert.NotNull(file);
        string json = File.ReadAllText(Path.Combine(dir, "telemetry-003.json"));
        _output.WriteLine(json);
        Assert.Contains("\"BounceCount\": 1", json);
        Assert.Contains("\"RollEndSpeed\": 4", json);
    }
}
