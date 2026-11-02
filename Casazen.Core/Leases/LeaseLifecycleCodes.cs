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

    /// <summary>422: the last day of an extension the parties agreed on is not after the end of the period the lease is in, or is too far (LR-OPS-1).</summary>
    public const string RenewalEndInvalid = "lease_renewal_end_invalid";

    /// <summary>422: the stretch before the end of the period has not begun, so a renewal cannot be recorded yet (LR-OPS-1).</summary>
    public const string RenewalNotYet = "lease_renewal_not_yet";

    /// <summary>409: the period of the lease is over, so a renewal cannot be recorded any more: the lease renews by itself and the hourly job writes it (LR-OPS-1).</summary>
    public const string RenewalPeriodOver = "lease_renewal_period_over";

    /// <summary>409: a notice is recorded, so the lease cannot be renewed until the notice is taken back (LR-OPS-1).</summary>
    public const string RenewalBlockedByNotice = "lease_renewal_blocked_by_notice";

    /// <summary>422: the lease is under the cedolare secca, which suspends every update of the rent for as long as the option lasts, so nothing is calculated (LR-OPS-1c).</summary>
    public const string IstatWaived = "lease_istat_waived";

    /// <summary>422: the variation of the index is zero or negative (or the increase rounds to nothing): the rent does not go up, there is nothing to ask for (LR-OPS-1c).</summary>
    public const string IstatNoIncrease = "lease_istat_no_increase";

    /// <summary>422: a percentage typed is not above zero, is above its maximum or has more than two decimals (LR-OPS-1c).</summary>
    public const string IstatPercentInvalid = "lease_istat_percent_invalid";

    /// <summary>422: the lease has no rent to update (LR-OPS-1c).</summary>
    public const string IstatNoRent = "lease_istat_no_rent";

    /// <summary>422: the day a letter takes effect from is outside the days it can have (LR-OPS-1c).</summary>
    public const string LetterEffectDateInvalid = "lease_letter_effect_date_invalid";

    /// <summary>422: the lease has no security deposit, so there is nothing to return (LR-OPS-1c).</summary>
    public const string DepositNone = "lease_deposit_none";

    /// <summary>422: the deposit was not held for at least a day, or it was paid too long before the lease or is returned too long after it (LR-OPS-1c).</summary>
    public const string DepositDatesInvalid = "lease_deposit_dates_invalid";

    /// <summary>422: a legal interest rate is not a percentage between zero and the bound with two decimals at most, or a year has two (LR-OPS-1c).</summary>
    public const string DepositRateInvalid = "lease_deposit_rate_invalid";

    /// <summary>422: a year the deposit was held in has no legal interest rate; the years that miss are in the message (LR-OPS-1c).</summary>
    public const string DepositRateMissing = "lease_deposit_rate_missing";

    /// <summary>422: the interest already paid is not a sum of euros with two decimals at most, or is more than the interest accrued (LR-OPS-1c).</summary>
    public const string DepositInterestPaidInvalid = "lease_deposit_interest_paid_invalid";

    /// <summary>422: a sum kept from the deposit has no reason, a reason that is too long, or a sum that is not above zero, or there are too many (LR-OPS-1c).</summary>
    public const string DepositDeductionInvalid = "lease_deposit_deduction_invalid";

    /// <summary>422: the sums kept are more than the deposit and the interest due: what is left is a debt of the tenant, not a return (LR-OPS-1c).</summary>
    public const string DepositDeductionsExceed = "lease_deposit_deductions_exceed";

    /// <summary>422: the day the deposit was returned is before the start of the lease or after today: what is recorded has already happened (LR-OPS-1c).</summary>
    public const string DepositReturnDateInvalid = "lease_deposit_return_date_invalid";

    /// <summary>422: this letter does not apply to this lease (not a transitory lease, the need is the tenant's, a type that does not renew…) (LR-OPS-1).</summary>
    public const string LetterNotApplicable = "lease_letter_not_applicable";

    /// <summary>422: the text of the letter cannot be used (its file is missing or broken): a problem of the deployment, never of the person (LR-OPS-1).</summary>
    public const string LetterUnavailable = "lease_letter_unavailable";

    /// <summary>
    /// Not an error: <c>noticeWarning</c> of the lease detail when the notice was given after the last day to give it. CasaZen records
    /// it and says so; it does not judge whether the notice is valid in law (decision LR-D29).
    /// </summary>
    public const string NoticePossiblyLate = "notice_possibly_late";
}
