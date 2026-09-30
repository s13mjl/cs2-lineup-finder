namespace CS2LineupFinder.Contracts;

/// <summary>Describes where a grenade is released from.</summary>
public sealed record ThrowOrigin
{
    /// <summary>World position of the player's feet.</summary>
    public required Vec3 Feet { get; init; }

    /// <summary>World position of the player's eyes before the throw animation.</summary>
    public required Vec3 Eyes { get; init; }

    /// <summary>Velocity the player carries into the throw, used by jump throws.</summary>
    public Vec3 Velocity { get; init; } = Vec3.Zero;
}

/// <summary>Everything the simulator needs to search for a line-up.</summary>
public sealed record LineupRequest
{
    /// <summary>Map the request is evaluated on, e.g. <c>de_mirage</c>.</summary>
    public required string MapName { get; init; }

    /// <summary>Grenade family being solved for.</summary>
    public required GrenadeType GrenadeType { get; init; }

    /// <summary>Stance the throw is executed from.</summary>
    public required ThrowMode ThrowMode { get; init; }

    /// <summary>Mouse button combination that releases the grenade.</summary>
    public required ThrowButton Button { get; init; }

    /// <summary>Release position / stance of the thrower.</summary>
    public required ThrowOrigin Origin { get; init; }

    /// <summary>Area the grenade has to land inside.</summary>
    public required GroundZone TargetZone { get; init; }

    /// <summary>Tuning knobs for the integrator.</summary>
    public SimulationEnvironment Environment { get; init; } = new();

    /// <summary>Upper bound on how many distinct throws the solver may evaluate.</summary>
    public int MaxCandidates { get; init; } = 64;
}

/// <summary>A single suggested throw returned by the solver.</summary>
public sealed record LineupSolution
{
    /// <summary>Suggested view angles, in degrees. Yaw is normalized to -180..180.</summary>
    public required float Yaw { get; init; }

    /// <summary>
    /// Suggested pitch in degrees, positive values look up, matching the
    /// contracts README and <c>GrenadeSimulator.DirectionFromAngles</c>.
    /// The engine's own angle convention is the mirror image; the plugin
    /// converts at the display boundary only, so solvers must not
    /// pre-negate anything.
    /// </summary>
    public required float Pitch { get; init; }

    /// <summary>Position the grenade was released from.</summary>
    public required Vec3 ReleasePosition { get; init; }

    /// <summary>Where the simulated grenade came to rest / detonated.</summary>
    public required Vec3 ImpactPosition { get; init; }

    /// <summary>Distance from the target zone centre in units, smaller is better.</summary>
    public required float TargetDistance { get; init; }

    /// <summary>Number of simulation steps consumed, useful for profiling.</summary>
    public int Steps { get; init; }

    /// <summary>Human readable explanation of the throw, e.g. <c>jumpthrow +w</c>.</summary>
    public string? Note { get; init; }
}

/// <summary>Outcome of <see cref="ITrajectorySimulator.SolveLineupAsync"/>.</summary>
public sealed record SolverOutcome
{
    /// <summary>Terminal status of the request.</summary>
    public required SolverStatus Status { get; init; }

    /// <summary>Solutions ordered best-first. Empty unless <see cref="Status"/> is <see cref="SolverStatus.Success"/>.</summary>
    public IReadOnlyList<LineupSolution> Results { get; init; } = Array.Empty<LineupSolution>();

    /// <summary>Wall clock time the solver spent on the request.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Diagnostic text for non-success statuses.</summary>
    public string? Message { get; init; }

    /// <summary>Convenience factory for a successful outcome.</summary>
    /// <param name="results">Solutions ordered best-first.</param>
    /// <param name="elapsed">Wall clock time spent solving.</param>
    /// <returns>A <see cref="SolverStatus.Success"/> outcome.</returns>
    public static SolverOutcome Success(IReadOnlyList<LineupSolution> results, TimeSpan elapsed) =>
        new() { Status = SolverStatus.Success, Results = results, Elapsed = elapsed };

    /// <summary>Convenience factory for a failure outcome.</summary>
    /// <param name="status">Non-success status.</param>
    /// <param name="message">Diagnostic text.</param>
    /// <param name="elapsed">Wall clock time spent solving.</param>
    /// <returns>The populated outcome.</returns>
    public static SolverOutcome Failure(SolverStatus status, string message, TimeSpan elapsed = default) =>
        new() { Status = status, Message = message, Elapsed = elapsed };
}

/// <summary>Physics profile of a grenade, mirroring the weapon VData fields the simulation needs.</summary>
public sealed record GrenadeProfile
{
    /// <summary>Item definition name, e.g. <c>weapon_smokegrenade</c>.</summary>
    public required string ItemName { get; init; }

    /// <summary>Grenade family this profile describes.</summary>
    public required GrenadeType GrenadeType { get; init; }

    /// <summary>Release speed in units per second for a primary (full strength) throw.</summary>
    public required float PrimaryThrowVelocity { get; init; }

    /// <summary>Release speed in units per second for a secondary (underhand) throw.</summary>
    public required float SecondaryThrowVelocity { get; init; }

    /// <summary>Release speed in units per second when both buttons are held.</summary>
    public required float BothButtonsThrowVelocity { get; init; }

    /// <summary>Gravity scale applied to the projectile, typically 0.4 for grenades.</summary>
    public float GravityScale { get; init; } = 1f;

    /// <summary>Restitution used when the projectile bounces off world geometry.</summary>
    public float Elasticity { get; init; } = 0.45f;

    /// <summary>Seconds between release and detonation, 0 for impact detonation.</summary>
    public float FuseTime { get; init; }

    /// <summary>Whether the grenade detonates on first impact rather than resting.</summary>
    public bool DetonatesOnImpact { get; init; }

    /// <summary>Projectile radius used for hull traces.</summary>
    public float CollisionRadius { get; init; } = 2f;
}

/// <summary>Stance dependent body measurements used to derive the release point.</summary>
public sealed record StanceProfile
{
    /// <summary>Eye height above the feet while standing.</summary>
    public required float StandingEyeHeight { get; init; }

    /// <summary>Eye height above the feet while crouched.</summary>
    public required float CrouchedEyeHeight { get; init; }

    /// <summary>Upward velocity added by a jump throw, in units per second.</summary>
    public float JumpVelocity { get; init; }

    /// <summary>Offset applied to the release point along the throw direction.</summary>
    public float ReleaseForwardOffset { get; init; }

    /// <summary>Offset applied to the release point on the Z axis.</summary>
    public float ReleaseUpOffset { get; init; }
}
