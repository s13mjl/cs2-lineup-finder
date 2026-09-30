using System;
using static System.Math;

namespace CS2LineupFinder.Math;

/// <summary>
/// Two dimensional Nelder-Mead simplex descent over the (yaw, elevation) plane.
/// </summary>
/// <remarks>
/// Deliberately internal: the refinement is an implementation detail of
/// <see cref="LineupSolver"/> and not something the physics or plugin layers should call.
/// The simplex is allowed to wander, so the caller's objective has to clamp elevation to a
/// physical window and hand back <see cref="double.PositiveInfinity"/> when the search
/// budget refuses an evaluation.
/// </remarks>
internal static class NelderMead2D
{
    private const double ReflectFactor = 1d;
    private const double ExpandFactor = 2d;
    private const double ContractFactor = 0.5d;
    private const double ShrinkFactor = 0.5d;

    /// <summary>How a descent terminated.</summary>
    internal readonly struct Outcome
    {
        internal Outcome(int iterations, bool convergedByTolerance, bool cutOffByBudget)
        {
            Iterations = iterations;
            ConvergedByTolerance = convergedByTolerance;
            CutOffByBudget = cutOffByBudget;
        }

        /// <summary>Simplex iterations completed.</summary>
        internal int Iterations { get; }

        /// <summary>True when the simplex shrank inside the requested tolerances.</summary>
        internal bool ConvergedByTolerance { get; }

        /// <summary>True when the evaluation budget or the clock stopped the descent early.</summary>
        internal bool CutOffByBudget { get; }
    }

    /// <summary>Minimises <paramref name="objective"/> starting from one vertex.</summary>
    /// <param name="objective">Cost of a (yaw, elevation) pair, lower is better.</param>
    /// <param name="yaw">Seed bearing in degrees.</param>
    /// <param name="elevation">Seed elevation in degrees.</param>
    /// <param name="yawStartStep">Initial simplex edge along yaw, degrees.</param>
    /// <param name="elevationStartStep">Initial simplex edge along elevation, degrees.</param>
    /// <param name="costTolerance">Landing spread in units below which the simplex counts as converged.</param>
    /// <param name="angleTolerance">Simplex size in degrees below which the simplex counts as converged.</param>
    /// <param name="maxIterations">Iteration ceiling for this descent.</param>
    /// <param name="budget">Shared evaluation and wall clock ceiling.</param>
    /// <returns>Termination information; the winning vertex is whatever the objective last recorded.</returns>
    internal static Outcome Minimize(
        Func<double, double, double> objective,
        double yaw,
        double elevation,
        double yawStartStep,
        double elevationStartStep,
        double costTolerance,
        double angleTolerance,
        int maxIterations,
        SearchBudget budget)
    {
        var vertices = new Vertex[3]
        {
            new(yaw, elevation, Evaluate(objective, yaw, elevation)),
            new(yaw + yawStartStep, elevation, Evaluate(objective, yaw + yawStartStep, elevation)),
            new(yaw, elevation + elevationStartStep, Evaluate(objective, yaw, elevation + elevationStartStep)),
        };

        var iterations = 0;
        while (iterations < maxIterations)
        {
            if (budget.IsExhausted)
            {
                return new Outcome(iterations, false, true);
            }

            SortAscending(vertices);
            iterations++;

            var spread = vertices[2].Cost - vertices[0].Cost;
            var size = SimplexSize(vertices);
            if (iterations > 1 && (spread < costTolerance || size < angleTolerance))
            {
                return new Outcome(iterations, true, false);
            }

            var best = vertices[0];
            var second = vertices[1];
            var worst = vertices[2];
            var centroidX = (best.X + second.X) * 0.5d;
            var centroidY = (best.Y + second.Y) * 0.5d;

            var reflectedX = centroidX + (ReflectFactor * (centroidX - worst.X));
            var reflectedY = centroidY + (ReflectFactor * (centroidY - worst.Y));
            var reflectedCost = Evaluate(objective, reflectedX, reflectedY);

            if (reflectedCost < best.Cost)
            {
                var expandedX = centroidX + (ExpandFactor * (centroidX - worst.X));
                var expandedY = centroidY + (ExpandFactor * (centroidY - worst.Y));
                var expandedCost = Evaluate(objective, expandedX, expandedY);
                vertices[2] = expandedCost < reflectedCost
                    ? new Vertex(expandedX, expandedY, expandedCost)
                    : new Vertex(reflectedX, reflectedY, reflectedCost);
                continue;
            }

            if (reflectedCost < second.Cost)
            {
                vertices[2] = new Vertex(reflectedX, reflectedY, reflectedCost);
                continue;
            }

            // The reflection bought nothing, so pull the worst vertex back toward the centroid.
            var contractTowardWorse = reflectedCost >= worst.Cost;
            var contractedX = contractTowardWorse
                ? centroidX + (ContractFactor * (worst.X - centroidX))
                : centroidX + (ContractFactor * (reflectedX - centroidX));
            var contractedY = contractTowardWorse
                ? centroidY + (ContractFactor * (worst.Y - centroidY))
                : centroidY + (ContractFactor * (reflectedY - centroidY));
            var contractedCost = Evaluate(objective, contractedX, contractedY);

            if (contractedCost < worst.Cost)
            {
                vertices[2] = new Vertex(contractedX, contractedY, contractedCost);
                continue;
            }

            // Shrink: fold the whole simplex onto the best vertex.
            vertices[1] = Shrink(best, second);
            vertices[2] = Shrink(best, worst);
            vertices[1] = vertices[1] with { Cost = Evaluate(objective, vertices[1].X, vertices[1].Y) };
            vertices[2] = vertices[2] with { Cost = Evaluate(objective, vertices[2].X, vertices[2].Y) };
        }

        return new Outcome(iterations, false, false);
    }

    private static Vertex Shrink(Vertex best, Vertex other) => new(
        best.X + (ShrinkFactor * (other.X - best.X)),
        best.Y + (ShrinkFactor * (other.Y - best.Y)),
        other.Cost);

    private static double Evaluate(Func<double, double, double> objective, double yaw, double elevation)
    {
        var cost = objective(yaw, elevation);
        return double.IsNaN(cost) ? double.PositiveInfinity : cost;
    }

    private static double SimplexSize(Vertex[] vertices)
    {
        var size = 0d;
        for (var i = 0; i < vertices.Length; i++)
        {
            for (var j = i + 1; j < vertices.Length; j++)
            {
                var dx = Abs(AngleMath.AngleDifference(vertices[i].X, vertices[j].X));
                var dy = Abs(vertices[i].Y - vertices[j].Y);
                size = Max(size, Max(dx, dy));
            }
        }

        return size;
    }

    private static void SortAscending(Vertex[] vertices)
    {
        if (vertices[0].Cost > vertices[1].Cost)
        {
            (vertices[0], vertices[1]) = (vertices[1], vertices[0]);
        }

        if (vertices[1].Cost > vertices[2].Cost)
        {
            (vertices[1], vertices[2]) = (vertices[2], vertices[1]);
        }

        if (vertices[0].Cost > vertices[1].Cost)
        {
            (vertices[0], vertices[1]) = (vertices[1], vertices[0]);
        }
    }

    private readonly record struct Vertex(double X, double Y, double Cost);
}
