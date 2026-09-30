// <copyright file="CalibrationRunner.cs" company="CS2LineupFinder">
// Fits the physics parameters against measured in-game landings.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Core.Calibration;

/// <summary>
/// Fits <see cref="PhysicsParameters"/> against a set of measured throws by
/// minimising the root-mean-square error between simulated and measured landing
/// points.
///
/// Method: coordinate descent with a shrinking step. The parameters are close to
/// correct already (they come from Valve source or from measured references), so
/// a local method is the right tool: a global search over six coupled parameters
/// would need tens of thousands of simulations and would happily converge on a
/// different, equally wrong, combination. Each parameter is swept independently
/// over a golden-section-style refinement, the best value is kept, and the step
/// halves each round until it falls below the resolution floor.
///
/// The world is rebuilt from scratch for every trial so no state leaks between
/// evaluations.
/// </summary>
public sealed class CalibrationRunner
{
    /// <summary>
    /// Shared options. The string enum converter is what lets a sample file say
    /// "HeGrenade" and "FullThrow" instead of magic integers.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>Builds the options used for both reading samples and writing reports.</summary>
    public static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Parameter names this run is allowed to move. Defaults to the standard mask.</summary>
    public IReadOnlyList<string> SearchableParameters { get; init; } = PhysicsParameters.SearchableNames;

    /// <summary>Rounds of coordinate descent. Four is enough for a 1e-3 resolution.</summary>
    public int MaxRounds { get; init; } = 6;

    /// <summary>Relative width of the initial sweep around the current value.</summary>
    public float InitialStepFraction { get; init; } = 0.5f;

    /// <summary>Stops refining once the step falls below this, in parameter units.</summary>
    public float MinimumStep { get; init; } = 0.001f;

    /// <summary>Loads a calibration data set from disk.</summary>
    public static CalibrationDataSet Load(string path)
    {
        string json = File.ReadAllText(path);
        return Deserialize(json);
    }

    /// <summary>Parses a calibration data set from a JSON string.</summary>
    public static CalibrationDataSet Deserialize(string json)
    {
        CalibrationDataSet? data = JsonSerializer.Deserialize<CalibrationDataSet>(json, JsonOptions);
        return data ?? throw new InvalidDataException("Calibration JSON did not contain a data set.");
    }

    /// <summary>Fits the parameters and returns the tuned set.</summary>
    public CalibrationResult Run(CalibrationDataSet data, PhysicsParameters baseline)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(baseline);

        if (data.Samples.Count == 0)
        {
            throw new InvalidDataException("Calibration data set contains no samples.");
        }

        int evaluations = 0;
        PhysicsParameters current = baseline.Clone();

        float rmseBefore = Score(current, data.Samples, ref evaluations);

        foreach (string name in SearchableParameters)
        {
            PhysicsParameter parameter = current[name];
            float startValue = parameter.Value;
            float bestValue = startValue;
            float bestScore = rmseBefore;

            // The effective window always contains the starting value. Without
            // this, a baseline that sits outside [Min, Max] (a badly wrong
            // default, say) can never be recovered: every candidate clamps back
            // to the window and the fit is pinned against the edge.
            float low = MathF.Min(parameter.Min, startValue);
            float high = MathF.Max(parameter.Max, startValue);

            // Full search window, then progressively tighter around the best.
            float span = MathF.Max(high - low, 1e-6f);
            float step = span * InitialStepFraction;

            for (int round = 0; round < MaxRounds && step > MinimumStep; round++)
            {
                bool improvedThisRound = false;

                // Try both directions from the current best.
                foreach (float direction in new[] { -1f, 1f })
                {
                    float candidate = Math.Clamp(bestValue + (direction * step), low, high);

                    if (MathF.Abs(candidate - bestValue) < 1e-9f)
                    {
                        continue;
                    }

                    parameter.Value = candidate;
                    float score = Score(current, data.Samples, ref evaluations);

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestValue = candidate;
                        improvedThisRound = true;
                    }
                }

                // Keep the winner, then shrink the step.
                parameter.Value = bestValue;
                step *= 0.5f;

                if (!improvedThisRound && step < span * 0.01f)
                {
                    break;
                }
            }

            parameter.Value = bestValue;
        }

        float rmseAfter = Score(current, data.Samples, ref evaluations);
        List<SampleResidual> residuals = Evaluate(current, data.Samples, ref evaluations);

        CalibrationResult result = new()
        {
            SampleCount = data.Samples.Count,
            Source = data.Source,
            Rmse = rmseAfter,
            RmseBefore = rmseBefore,
            MeanAbsoluteError = Mean(residuals.Select(r => r.Error)),
            MaxError = residuals.Count == 0 ? 0f : Max(residuals.Select(r => r.Error)),
            Residuals = residuals,
            Evaluations = evaluations,
        };

        foreach (string name in SearchableParameters)
        {
            PhysicsParameter after = current[name];
            PhysicsParameter before = baseline[name];

            result.Fits.Add(new ParameterFit
            {
                Name = name,
                Before = before.Value,
                After = after.Value,
                Min = after.Min,
                Max = after.Max,
                Tier = after.Tier.ToString(),
                Source = after.Source,
                Changed = MathF.Abs(after.Value - before.Value) > MinimumStep,
            });
        }

        return result;
    }

    /// <summary>
    /// Root-mean-square planar landing error over every sample. A trial that
    /// throws is treated as a large constant penalty rather than skipped, so a
    /// parameter set that stops the grenade from landing at all cannot win.
    /// </summary>
    private static float Score(PhysicsParameters parameters, List<CalibrationSample> samples, ref int evaluations)
    {
        List<SampleResidual> residuals = Evaluate(parameters, samples, ref evaluations);
        double sum = 0d;

        foreach (SampleResidual residual in residuals)
        {
            sum += (double)residual.Error * residual.Error;
        }

        return (float)Math.Sqrt(sum / Math.Max(1, residuals.Count));
    }

    /// <summary>Simulates every sample and reports its landing residual.</summary>
    private static List<SampleResidual> Evaluate(PhysicsParameters parameters, List<CalibrationSample> samples, ref int evaluations)
    {
        List<SampleResidual> residuals = new(samples.Count);

        foreach (CalibrationSample sample in samples)
        {
            ThrowParams input = BuildThrowParams(sample, parameters);
            CalibrationWorld world = BuildWorld(sample);

            GrenadeSimulator simulator = new(parameters);
            TrajectoryResult result = simulator.Simulate(input, world);
            evaluations++;

            float dx = result.Impact.Position.X - sample.LandingX;
            float dy = result.Impact.Position.Y - sample.LandingY;

            residuals.Add(new SampleResidual
            {
                Name = sample.Name,
                MeasuredX = sample.LandingX,
                MeasuredY = sample.LandingY,
                SimulatedX = result.Impact.Position.X,
                SimulatedY = result.Impact.Position.Y,
                Error = new Vec3(dx, dy, 0f).Length,
                BounceCount = result.BounceCount,
                FlightTime = result.FlightTime,
            });
        }

        return residuals;
    }

    /// <summary>
    /// Turns a measured sample into simulator input, resolving the launch
    /// velocity from the aim angle through the same path the plugin shell uses.
    /// </summary>
    private static ThrowParams BuildThrowParams(CalibrationSample sample, PhysicsParameters parameters)
    {
        Vec3 aim = sample.AimDirection();
        Vec3 launch = ThrowVelocityCalculator.Compute(
            aim,
            sample.PlayerVelocity.ToVec3(),
            sample.ThrowButton,
            sample.GrenadeType,
            sample.ThrowStrength,
            parameters);

        return new ThrowParams(
            sample.Origin.ToVec3(),
            launch,
            sample.GrenadeType,
            sample.ThrowStrength,
            sample.GameTickRate);
    }

    /// <summary>
    /// Builds the collision world for a sample. Samples with a known floor height
    /// get an infinite flat floor, which is what lets open-field throws be fitted
    /// without shipping any map geometry. Samples without one run against an
    /// empty world, so only the airborne arc is fitted.
    /// </summary>
    private static CalibrationWorld BuildWorld(CalibrationSample sample)
    {
        if (sample.FloorHeight is null)
        {
            return CalibrationWorld.Empty();
        }

        return CalibrationWorld.WithFloor(sample.FloorHeight.Value);
    }

    private static float Mean(IEnumerable<float> values)
    {
        float total = 0f;
        int count = 0;

        foreach (float value in values)
        {
            total += value;
            count++;
        }

        return count == 0 ? 0f : total / count;
    }

    private static float Max(IEnumerable<float> values)
    {
        float best = float.MinValue;

        foreach (float value in values)
        {
            if (value > best)
            {
                best = value;
            }
        }

        return best;
    }
}
