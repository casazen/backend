namespace Casazen.Web.DTOs.Verification;

/// <summary>
/// The figures of the identity verifications of a period (<c>GET /api/verification/metrics</c>, E03/F04/US02/T03, NF-51 monitoring) for the
/// bookings the caller reaches.
/// </summary>
public sealed class VerificationMetricsDto
{
    /// <summary>First day of the period (Europe/Rome).</summary>
    public DateOnly From { get; set; }

    /// <summary>Last day of the period (Europe/Rome), included.</summary>
    public DateOnly To { get; set; }

    /// <summary>Automatic outcomes: passed.</summary>
    public int Ok { get; set; }

    /// <summary>Automatic outcomes: sent to a person (between the thresholds, MRZ not consistent, document unreadable, technical problem).</summary>
    public int Review { get; set; }

    /// <summary>Automatic outcomes: not passed (a new attempt, or the operator after the last one).</summary>
    public int NotOk { get; set; }

    /// <summary>Decisions of the operators: approved.</summary>
    public int OperatorApproved { get; set; }

    /// <summary>Decisions of the operators: rejected.</summary>
    public int OperatorRejected { get; set; }

    /// <summary>Every decision of an operator.</summary>
    public int OperatorDecisions { get; set; }

    /// <summary>Operator decisions / (automatic ok + operator decisions): the share of the concluded verifications an operator concluded, 0 to 1.</summary>
    public double OperatorShare { get; set; }
}
