namespace CS2LineupFinder.Contracts;

/// <summary>Grenade family handled by the line-up finder.</summary>
public enum GrenadeType
{
    /// <summary>Smoke grenade (<c>smoke</c>).</summary>
    Smoke = 0,

    /// <summary>Flashbang (<c>flash</c>).</summary>
    Flash = 1,

    /// <summary>Molotov / incendiary (<c>molotov</c>).</summary>
    Molotov = 2,

    /// <summary>High explosive grenade (<c>he</c>).</summary>
    He = 3,
}

/// <summary>Body stance the throw is executed from.</summary>
public enum ThrowMode
{
    /// <summary>Standing throw, highest release point, lowest penalty on accuracy.</summary>
    Stand = 0,

    /// <summary>Crouched throw, lowers the release point by the crouch delta.</summary>
    Crouch = 1,

    /// <summary>Jump throw, releases at the apex with an upward velocity component.</summary>
    Jump = 2,
}

/// <summary>Mouse button combination used to release the grenade.</summary>
public enum ThrowButton
{
    /// <summary>Left click, full strength throw.</summary>
    Primary = 0,

    /// <summary>Right click, underhand throw.</summary>
    Secondary = 1,

    /// <summary>Both buttons, medium strength "jump throw" / mid throw.</summary>
    Both = 2,
}

/// <summary>Terminal state of a solve request.</summary>
public enum SolverStatus
{
    /// <summary>At least one line-up was found, see <see cref="SolverOutcome.Results"/>.</summary>
    Success = 0,

    /// <summary>The search space was exhausted without a line-up landing inside the zone.</summary>
    NoSolution = 1,

    /// <summary>The configured time budget expired before the search finished.</summary>
    Timeout = 2,

    /// <summary>The caller cancelled the request.</summary>
    Cancelled = 3,

    /// <summary>The request is internally inconsistent, see <see cref="SolverOutcome.Message"/>.</summary>
    InvalidRequest = 4,

    /// <summary>The simulator has no implementation yet (stub build).</summary>
    NotImplemented = 5,

    /// <summary>An unexpected failure occurred, see <see cref="SolverOutcome.Message"/>.</summary>
    Failed = 6,
}
