using System;
using System.Threading;
using CS2LineupFinder.Contracts;
using static System.Math;

namespace CS2LineupFinder.Math.Tests;

/// <summary>
/// A throw that obeys the vacuum equations exactly, optionally stopped by one vertical wall.
/// The solver is tested against ground truth it can predict, so a failure here is a search
/// bug rather than a physics disagreement.
/// </summary>
internal sealed class VacuumThrowMock : IImpactEvaluator
{
    private readonly Vec3 _origin;
    private readonly double _speed;
    private readonly double _gravity;
    private readonly double _planeZ;

    internal VacuumThrowMock(Vec3 origin, double speed, double gravity, double planeZ)
    {
        _origin = origin;
        _speed = speed;
        _gravity = gravity;
        _planeZ = planeZ;
    }

    /// <summary>Horizontal distance of the blocking wall measured along the throw, or null for open ground.</summary>
    internal double? WallDistance { get; set; }

    /// <summary>Arcs that are still below this height when they reach the wall stop against it.</summary>
    internal double WallTopZ { get; set; }

    /// <summary>Artificial cost per evaluation, used to exercise the wall-clock budget.</summary>
    internal TimeSpan DelayPerEvaluation { get; set; }

    public int EvaluationCount { get; private set; }

    /// <summary>Elevation of the last accepted probe, so tests can assert which arc won.</summary>
    internal double LastElevation { get; private set; }

    public bool TryEvaluate(double yawDegrees, double elevationDegrees, out Vec3 impact)
    {
        EvaluationCount++;
        LastElevation = elevationDegrees;
        if (DelayPerEvaluation > TimeSpan.Zero)
        {
            Thread.Sleep(DelayPerEvaluation);
        }

        var radians = elevationDegrees * AngleMath.Deg2Rad;
        var vertical = _speed * Sin(radians);
        var horizontal = _speed * Cos(radians);
        var flightTime = Ballistics.TimeToPlane(elevationDegrees, _speed, _gravity, _planeZ - _origin.Z);
        if (double.IsNaN(flightTime))
        {
            impact = Vec3.Zero;
            return false;
        }

        var range = horizontal * flightTime;
        if (WallDistance is { } wall && range > wall)
        {
            // z(x) = z0 + x*tan(theta) - g*x^2 / (2*v^2*cos(theta)^2)
            var heightAtWall = _origin.Z
                + (wall * Tan(radians))
                - ((_gravity * wall * wall) / (2d * _speed * _speed * Cos(radians) * Cos(radians)));
            if (heightAtWall < WallTopZ)
            {
                impact = Offset(wall, yawDegrees, heightAtWall);
                return true;
            }
        }

        impact = Offset(range, yawDegrees, _planeZ);
        return true;
    }

    private Vec3 Offset(double distance, double yawDegrees, double z)
    {
        var radians = yawDegrees * AngleMath.Deg2Rad;
        return new Vec3(
            (float)(_origin.X + (Cos(radians) * distance)),
            (float)(_origin.Y + (Sin(radians) * distance)),
            (float)z);
    }
}

/// <summary>Fixed grenade profile, plus the stance lookup the contract expects.</summary>
internal sealed class StubVDataProvider : IVDataProvider
{
    internal StubVDataProvider(GrenadeProfile profile) => Profile = profile;

    internal GrenadeProfile Profile { get; }

    public GrenadeProfile? GetProfile(GrenadeType grenadeType) =>
        grenadeType == Profile.GrenadeType ? Profile : null;

    public StanceProfile? GetStanceProfile() => null;
}

/// <summary>An evaluator that never produces a usable landing, standing in for a sealed box.</summary>
internal sealed class AlwaysBlockedEvaluator : IImpactEvaluator
{
    internal AlwaysBlockedEvaluator(Vec3 reports, TimeSpan? delay = null)
    {
        Reports = reports;
        Delay = delay;
    }

    internal Vec3 Reports { get; }

    internal TimeSpan? Delay { get; }

    public int EvaluationCount { get; private set; }

    public bool TryEvaluate(double yawDegrees, double elevationDegrees, out Vec3 impact)
    {
        EvaluationCount++;
        if (Delay is { } delay)
        {
            Thread.Sleep(delay);
        }

        impact = Reports;
        return true;
    }
}
