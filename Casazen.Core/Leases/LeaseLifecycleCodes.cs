namespace Casazen.Core.Leases;

/// <summary>
/// Stable codes of what the landlord does to a lease in force (LR-14): the notice of non-renewal, the early termination. Each error is
/// answered with its status and the localized text of the resource key written next to it; the client translates the code. A code is
/// never renamed: add a new one. The runbook <c>docs/runbooks/long-rent-aggregates.md</c> (§ 10) lists them with their statuses.
/// </summary>
public static class LeaseLifecycleCodes
{
    /// <summary>409: the lease is not signed by every party yet (or was rejected), so there is no period to give notice of or to end.</summary>
    public const string NotInForce = "lease_not_in_force";

    /// <summary>422: a transitory lease ends by itself and has no renewal, so there is no notice to record.</summary>
    public const string TypeHasNoRenewal = "lease_type_has_no_renewal";

    /// <summary>409: a notice is already recorded; take it back first to record another.</summary>
    public const string NoticeAlreadyRecorded = "lease_notice_already_recorded";

    /// <summary>409: the lease was already ended (early, or recorded as concluded when the renewal was introduced); that cannot be undone.</summary>
    public const string AlreadyTerminated = "lease_already_terminated";

    /// <summary>422: the day of the notice is before the start of the lease or after today.</summary>
    public const string NoticeDateInvalid = "lease_notice_date_invalid";

    /// <summary>409: the period of the lease is over, so a notice can no longer be taken back (the lease has ended, or is about to renew).</summary>
    public const string TermAlreadyEnded = "lease_term_already_ended";

    /// <summary>422: the last day of an early termination is before the start of the lease or after the end of the current period.</summary>
    public const string TerminationDateInvalid = "lease_termination_date_invalid";

    /// <summary>409: somebody else is changing the lease right now (the row is locked for more than a few seconds); nothing was written, try again.</summary>
    public const string Busy = "lease_busy";

    /// <summary>422: the day a withdrawal of the tenant was received is before the start of the lease or after today (LR-OPS-1).</summary>
    public const string WithdrawalDateInvalid = "lease_withdrawal_date_invalid";

    /// <summary>422: the notice of a withdrawal by contractual faculty is not a whole number of months between 1 and <see cref="LeaseOperationRules.MaxContractualNoticeMonths"/> (LR-OPS-1).</summary>
    public const string WithdrawalNoticeInvalid = "lease_withdrawal_notice_invalid";

    /// <summary>422: the day a letter was sent is before the start of the lease or after today (LR-OPS-1).</summary>
    public const string LetterDateInvalid = "lease_letter_date_invalid";

    /// <summary>422: the notice at the first expiry must state one of the reasons of the law, and none was chosen (LR-OPS-1).</summary>
    public const string DisdettaReasonRequired = "lease_disdetta_reason_required";

    /// <summary>422: the text of the letter cannot be used (its file is missing or broken): a problem of the deployment, never of the person (LR-OPS-1).</summary>
    public const string LetterUnavailable = "lease_letter_unavailable";

    /// <summary>
    /// Not an error: <c>noticeWarning</c> of the lease detail when the notice was given after the last day to give it. CasaZen records
    /// it and says so; it does not judge whether the notice is valid in law (decision LR-D29).
    /// </summary>
    public const string NoticePossiblyLate = "notice_possibly_late";
}
