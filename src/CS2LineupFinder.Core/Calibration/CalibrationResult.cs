// <copyright file="CalibrationResult.cs" company="CS2LineupFinder">
// Calibration output shapes.
// </copyright>

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CS2LineupFinder.Core.Calibration;

/// <summary>One parameter before and after fitting.</summary>
public sealed class ParameterFit
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("before")]
    public float Before { get; set; }

    [JsonPropertyName("after")]
    public float After { get; set; }

    [JsonPropertyName("min")]
    public float Min { get; set; }

    [JsonPropertyName("max")]
    public float Max { get; set; }

    /// <summary>"Valve", "Community", "Measured" and so on.</summary>
    [JsonPropertyName("tier")]
    public string Tier { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>True when the fit moved the value away from its sourced default.</summary>
    [JsonPropertyName("changed")]
    public bool Changed { get; set; }

    /// <summary>Improvement in root-mean-square error attributable to this parameter.</summary>
    [JsonPropertyName("rmseImprovement")]
    public float RmseImprovement { get; set; }
}

/// <summary>Per-sample residual, so a bad measurement can be spotted and dropped.</summary>
public sealed class SampleResidual
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("measuredX")]
    public float MeasuredX { get; set; }

    [JsonPropertyName("simulatedX")]
    public float SimulatedX { get; set; }

    [JsonPropertyName("measuredY")]
    public float MeasuredY { get; set; }

    [JsonPropertyName("simulatedY")]
    public float SimulatedY { get; set; }

    /// <summary>Planar error between measured and simulated landing, in units.</summary>
    [JsonPropertyName("error")]
    public float Error { get; set; }

    [JsonPropertyName("bounceCount")]
    public int BounceCount { get; set; }

    [JsonPropertyName("flightTime")]
    public float FlightTime { get; set; }
}

/// <summary>Full calibration output, also rendered into the PHYSICS.md record.</summary>
public sealed class CalibrationResult
{
    [JsonPropertyName("samples")]
    public int SampleCount { get; set; }

    /// <summary>Root-mean-square planar landing error, in world units.</summary>
    [JsonPropertyName("rmse")]
    public float Rmse { get; set; }

    /// <summary>Mean absolute landing error, in world units.</summary>
    [JsonPropertyName("meanAbsoluteError")]
    public float MeanAbsoluteError { get; set; }

    /// <summary>Worst single-sample error, in world units.</summary>
    [JsonPropertyName("maxError")]
    public float MaxError { get; set; }

    /// <summary>RMSE before fitting, for comparison.</summary>
    [JsonPropertyName("rmseBefore")]
    public float RmseBefore { get; set; }

    [JsonPropertyName("fits")]
    public List<ParameterFit> Fits { get; set; } = new();

    [JsonPropertyName("residuals")]
    public List<SampleResidual> Residuals { get; set; } = new();

    /// <summary>Provenance note carried through from the input file.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>Number of simulator evaluations performed.</summary>
    [JsonPropertyName("evaluations")]
    public int Evaluations { get; set; }

    /// <summary>
    /// A ready-to-paste markdown block for the PHYSICS.md calibration record.
    /// </summary>
    public string ToMarkdown()
    {
        System.Text.StringBuilder sb = new();
        sb.AppendLine($"- 样本数 / samples: **{SampleCount}**, 来源 / source: {Source}");
        sb.AppendLine($"- 校准前 RMSE / RMSE before: **{RmseBefore:0.###} u**, 校准后 / after: **{Rmse:0.###} u**");
        sb.AppendLine($"- 平均绝对误差 / MAE: **{MeanAbsoluteError:0.###} u**, 最大误差 / max: **{MaxError:0.###} u**");
        sb.AppendLine($"- 模拟调用次数 / evaluations: {Evaluations}");
        sb.AppendLine();
        sb.AppendLine("| 参数 / parameter | 校准前 / before | 校准后 / after | 搜索区间 / range | 来源 / tier | 变化 / changed |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");

        foreach (ParameterFit fit in Fits)
        {
            sb.AppendLine(
                $"| {fit.Name} | {fit.Before:0.###} | {fit.After:0.###} | " +
                $"[{fit.Min:0.###}, {fit.Max:0.###}] | {fit.Tier} | {(fit.Changed ? "是 / yes" : "否 / no")} |");
        }

        return sb.ToString();
    }
}