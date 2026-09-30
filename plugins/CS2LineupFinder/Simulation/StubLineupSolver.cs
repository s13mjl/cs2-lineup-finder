using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Simulation;

// TODO: replace with real Math (ILineupSolver from src/CS2LineupFinder.Math).
/// <summary>
/// Deterministic stand-in for the inverse solver owned by
/// <c>src/CS2LineupFinder.Math</c>. It sweeps a coarse grid of yaw and pitch
/// values, ordered by how plausible each angle looks, which is enough to keep the
/// whole plugin path (command to chat, beam and save) exercised before the real
/// solver lands.
/// </summary>
public sealed class StubLineupSolver : ILineupSolver
{
    private const float CoarseYawStep = 15f;

    private static readonly float[] PitchGrid = { 45f, 40f, 35f, 50f, 30f, 55f, 25f, 60f, 20f, 65f, 15f, 70f, 10f };

    /// <summary>
    /// Yaw steps walked before falling back to the coarse sweep. The real solver
    /// produces a sorted stream of candidates, so the closest to straight ahead
    /// comes first; the stub has to mimic that because the simulator stops at
    /// <see cref="LineupRequest.MaxCandidates"/>, and a coarse 15 degree grid alone
    /// would spend its whole budget on angles far away from the player's aim.
    /// </summary>
    private static readonly float[] FineYawOffsets = { 0f, -5f, 5f, -10f, 10f, -15f, 15f };

    /// <inheritdoc />
    public string Name => "stub-coarse-grid-v0";

    /// <inheritdoc />
    public async IAsyncEnumerable<AngleCandidate> EnumerateCandidatesAsync(
        LineupRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var emitted = 0;

        foreach (var offset in FineYawOffsets)
        {
            foreach (var pitch in PitchGrid)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new AngleCandidate
                {
                    Yaw = NormalizeYaw(offset),
                    Pitch = pitch,
                    HeuristicCost = pitch + MathF.Abs(offset),
                };

                if (++emitted >= request.MaxCandidates)
                {
                    yield break;
                }
            }

            await Task.Yield();
        }

        var yawStep = CoarseYawStep;
        for (var yaw = -180f; yaw < 180f; yaw += yawStep)
        {
            foreach (var pitch in PitchGrid)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new AngleCandidate
                {
                    Yaw = NormalizeYaw(yaw),
                    Pitch = pitch,
                    HeuristicCost = pitch + MathF.Abs(NormalizeYaw(yaw)),
                };

                if (++emitted >= request.MaxCandidates)
                {
                    yield break;
                }
            }

            // Hand the frame back so a large sweep cannot stall the server.
            await Task.Yield();
        }
    }

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
}
