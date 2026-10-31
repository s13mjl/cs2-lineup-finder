// <copyright file="AimBiasSweepDiagnostic.cs" company="CS2LineupFinder">
// Diagnostic-only sweep: replays the recorded CS2 landings across a spread of
// level-aim up-bias values. No assertion - the printed table is the output.
// </copyright>

using System;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Core.Tests;

public class AimBiasSweepDiagnostic
{
    private readonly ITestOutputHelper _output;

    public AimBiasSweepDiagnostic(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed record Sample(
        string Name, Vec3 Origin, float EyeHeight, float Yaw, float Pitch,
        Vec3 ActualImpact);

    private static readonly Sample[] Samples =
    {
        new("1009-1 palace     ", new Vec3(183.49892f, -1477.1948f, -175.96875f), 64f, -139.2f, 11.7f,
            new Vec3(-822.4844f, -2358.0156f, -173.96875f)),
        new("1009-2 mid window ", new Vec3(502.93915f, -109.203995f, -160.003f), 64f, -148.8f, 18.9f,
            new Vec3(-686.0562f, -884.6193f, -253.96875f)),
        new("1009-3 ramp       ", new Vec3(-91.31061f, -332.0716f, -159.96875f), 64f, -166.5f, 15.1f,
            new Vec3(-1191.1335f, -630.13257f, -165.96875f)),
        new("1013 palace shallow", new Vec3(38.9705f, -2036.9613f, -39.96875f), 64f, -161.2f, -6.9f,
            new Vec3(-1089.178f, -2421.01f, -165.96875f)),
        new("1014 palace steep ", new Vec3(90.239044f, -2006.9675f, -39.96875f), 64f, -158.8f, -12.1f,
            new Vec3(-825.45056f, -2358.9136f, -173.96875f)),
    };

    [Fact]
    public void PrintBiasSweep()
    {
        float[] biases = { 0f, 2f, 4f, 5f, 6f, 8f, 10f, 12f };
        _output.WriteLine("");
        _output.WriteLine("bias |   1009-1   1009-2   1009-3   1013-sh   1014-st |  RMSE    MAE");
        _output.WriteLine("-----+----------------------------------------------------+-------------");
        foreach (float bias in biases)
        {
            PhysicsParameters p = new();
            p.AimBiasDegrees.Value = bias;
            double sumSq = 0;
            var cells = new string[Samples.Length];
            for (var i = 0; i < Samples.Length; i++)
            {
                Sample s = Samples[i];
                Vec3 aim = GrenadeSimulator.DirectionFromAnglesForTest(s.Yaw, s.Pitch);
                Vec3 unbiased = aim.Normalized();
                Vec3 velocity = ThrowVelocityCalculator.Compute(aim, Vec3.Zero, ThrowButton.Primary, GrenadeType.Smoke, 1f, p);
                Vec3 release = s.Origin + (unbiased * p.ReleaseOffset.Value) + new Vec3(0f, 0f, s.EyeHeight);
                GrenadeSimulator simulator = new(p);
                TrajectoryResult r = simulator.Simulate(
                    new ThrowParams(release, velocity, GrenadeType.Smoke, 1f, 64),
                    MockWorldGeometry.Empty().AddInfiniteFloor(s.ActualImpact.Z));
                float e = (r.Impact.Position - s.ActualImpact).Length;
                sumSq += (double)e * e;
                cells[i] = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0,8:0.#}", e);
            }

            double rmse = System.Math.Sqrt(sumSq / Samples.Length);
            double mae = 0;
            for (var i = 0; i < Samples.Length; i++)
            {
                Sample s = Samples[i];
                Vec3 aim = GrenadeSimulator.DirectionFromAnglesForTest(s.Yaw, s.Pitch);
                Vec3 unbiased = aim.Normalized();
                Vec3 velocity = ThrowVelocityCalculator.Compute(aim, Vec3.Zero, ThrowButton.Primary, GrenadeType.Smoke, 1f, p);
                Vec3 release = s.Origin + (unbiased * p.ReleaseOffset.Value) + new Vec3(0f, 0f, s.EyeHeight);
                GrenadeSimulator simulator = new(p);
                TrajectoryResult r = simulator.Simulate(
                    new ThrowParams(release, velocity, GrenadeType.Smoke, 1f, 64),
                    MockWorldGeometry.Empty().AddInfiniteFloor(s.ActualImpact.Z));
                mae += (r.Impact.Position - s.ActualImpact).Length;
            }
            mae /= Samples.Length;
            _output.WriteLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0,4:0.#} | {1} {2} {3} {4} {5} | {6,7:0.#} {7,6:0.#}",
                bias, cells[0], cells[1], cells[2], cells[3], cells[4], rmse, mae));
        }

        _output.WriteLine("");
        _output.WriteLine("Caveats: infinite-plane replay (real map geometry differs); throw-velocity");
        _output.WriteLine("tier assumed 675; samples were recorded pre-tickrate-fix, so the recorded");
        _output.WriteLine("aim is the raw view angle and is replayed through ApplyAimBias exactly as");
        _output.WriteLine("the engine remap would. Read the row shape, not single-cell wins.");
    }
}
