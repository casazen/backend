namespace Casazen.Core.Entities.Enums;

/// <summary>Why the payer complains about a job (SP-15c-c). Stored as an integer: append only. The API names are the snake case ones of <c>ServiceDisputeRules</c>.</summary>
public enum ServiceDisputeReason
{
    /// <summary>The work was not done (also what CasaZen records when the supplier never marked it completed: the no-show).</summary>
    NotDone = 0,

    /// <summary>The work was left half done.</summary>
    Incomplete = 1,

    /// <summary>The work was done badly.</summary>
    PoorQuality = 2,

    /// <summary>The work is not what was agreed.</summary>
    NotAsDescribed = 3,

    /// <summary>Anything else, said in the payer's words.</summary>
    Other = 4,
}
