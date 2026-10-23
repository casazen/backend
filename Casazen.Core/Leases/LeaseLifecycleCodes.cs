namespace Casazen.Core.Leases;

/// <summary>
/// Stable codes of what the landlord does to a lease in force (LR-14): the notice of non-renewal, the early termination. A code is never
/// renamed: add a new one. The runbook <c>docs/runbooks/long-rent-aggregates.md</c> (§ 10) lists them.
/// </summary>
public static class LeaseLifecycleCodes
{
    /// <summary>
    /// Not an error: <c>noticeWarning</c> of the lease detail when the notice was given after the last day to give it. CasaZen records
    /// it and says so; it does not judge whether the notice is valid in law (decision LR-D29).
    /// </summary>
    public const string NoticePossiblyLate = "notice_possibly_late";
}
