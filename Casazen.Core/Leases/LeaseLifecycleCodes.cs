namespace Casazen.Core.Leases;

/// <summary>
/// Stable codes of what the landlord does to a lease in force (LR-14): the notice of non-renewal, the early termination. Each error is
/// answered with its status and the localized text of the resource key written next to it; the client translates the code. A code is
/// never renamed: add a new one. The runbook <c>docs/runbooks/long-rent-aggregates.md</c> (§ 10) lists them with their statuses.
/// </summary>
public static class LeaseLifecycleCodes
{
    public const string NotInForce = "lease_not_in_force";

    public const string TypeHasNoRenewal = "lease_type_has_no_renewal";

    public const string NoticeAlreadyRecorded = "lease_notice_already_recorded";

    public const string AlreadyTerminated = "lease_already_terminated";

    public const string NoticeDateInvalid = "lease_notice_date_invalid";

    public const string TermAlreadyEnded = "lease_term_already_ended";

    public const string TerminationDateInvalid = "lease_termination_date_invalid";

    public const string Busy = "lease_busy";

    public const string WithdrawalDateInvalid = "lease_withdrawal_date_invalid";

    public const string WithdrawalNoticeInvalid = "lease_withdrawal_notice_invalid";

    public const string LetterDateInvalid = "lease_letter_date_invalid";

    public const string DisdettaReasonRequired = "lease_disdetta_reason_required";

    public const string RenewalEndInvalid = "lease_renewal_end_invalid";

    public const string RenewalNotYet = "lease_renewal_not_yet";

    public const string RenewalPeriodOver = "lease_renewal_period_over";

    public const string RenewalBlockedByNotice = "lease_renewal_blocked_by_notice";

    /// <summary>422: the lease is under the cedolare secca, which suspends every update of the rent for as long as the option lasts, so nothing is calculated (LR-OPS-1c).</summary>
    public const string IstatWaived = "lease_istat_waived";

    public const string IstatNoIncrease = "lease_istat_no_increase";

    public const string IstatPercentInvalid = "lease_istat_percent_invalid";

    public const string IstatNoRent = "lease_istat_no_rent";

    public const string LetterEffectDateInvalid = "lease_letter_effect_date_invalid";

    public const string DepositNone = "lease_deposit_none";

    public const string DepositDatesInvalid = "lease_deposit_dates_invalid";

    public const string DepositRateInvalid = "lease_deposit_rate_invalid";

    public const string DepositRateMissing = "lease_deposit_rate_missing";

    public const string DepositInterestPaidInvalid = "lease_deposit_interest_paid_invalid";

    public const string DepositDeductionInvalid = "lease_deposit_deduction_invalid";

    public const string DepositDeductionsExceed = "lease_deposit_deductions_exceed";

    public const string DepositReturnDateInvalid = "lease_deposit_return_date_invalid";

    public const string LetterNotApplicable = "lease_letter_not_applicable";

    public const string LetterUnavailable = "lease_letter_unavailable";

    public const string NoticePossiblyLate = "notice_possibly_late";
}
