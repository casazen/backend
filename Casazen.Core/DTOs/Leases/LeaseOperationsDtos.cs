using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;

namespace Casazen.Core.DTOs.Leases;

public sealed record LeaseOperationsDto(
    LeaseDisdettaOperationDto Disdetta,
    LeaseWithdrawalOperationDto Withdrawal,
    IReadOnlyList<LeaseLetterStateDto> Letters,
    LeaseRenewalOperationDto Renewal,
    LeaseTransitoryConfirmationDto TransitoryConfirmation,
    LeaseIstatOperationDto Istat,
    LeaseCedolareNoticeDto CedolareNotice,
    LeaseHandoverOperationDto Handover,
    LeaseDepositOperationDto Deposit);

public sealed record LeaseLetterStateDto(LeaseLetterKind Kind, bool Approved, LeaseLetterSentDto? Sent);

public sealed record LeaseLetterSentDto(DateOnly On, LeaseLetterMeans Means);

public sealed record LeaseDisdettaOperationDto(
    bool Available,
    bool FirstPeriod,
    DateOnly PeriodEnd,
    DateOnly? LastDayToGiveNotice,
    bool Late,
    IReadOnlyList<LeaseDisdettaReason> Reasons);

/// <summary>
/// The withdrawal of the tenant. <c>Available</c>: it can be recorded now, with a day received between <c>MinReceivedOn</c> and
/// <c>MaxReceivedOn</c>. <c>MaxContractualNoticeMonths</c> bounds the notice the landlord can type when the contract gave the faculty.
/// <c>Recorded</c> is the withdrawal already recorded, if any.
/// </summary>
public sealed record LeaseWithdrawalOperationDto(
    bool Available,
    DateOnly MinReceivedOn,
    DateOnly MaxReceivedOn,
    int MaxContractualNoticeMonths,
    LeaseWithdrawalRecordDto? Recorded);

public sealed record LeaseWithdrawalRecordDto(DateOnly ReceivedOn, TenantWithdrawalReason Reason, DateOnly EffectiveOn, int NoticeMonths);

public sealed record LeaseRenewalOperationDto(
    bool Available,
    DateOnly OpensOn,
    DateOnly StartsOn,
    DateOnly StandardEnd,
    int StandardMonths,
    DateOnly LatestAgreedEnd,
    LeasePeriodConflictDto? Conflict,
    LeaseRenewalRecordDto? Recorded);

public sealed record LeasePeriodConflictDto(Guid LeaseId, DateOnly From, DateOnly To);

/// <summary>A renewal that was recorded: whether it was agreed on a day (otherwise the standard one) and the period it added.</summary>
public sealed record LeaseRenewalRecordDto(bool Agreed, DateOnly From, DateOnly To);

public sealed record LeaseTransitoryConfirmationDto(bool Applies, bool Available, PartyRole? Holder, DateOnly ConfirmBy);

/// <summary>
/// The ISTAT update of the rent. <c>Available</c>: the lease is in force, has a rent and is not under the cedolare secca (which suspends every
/// update), so a calculation and a letter make sense. <c>CurrentRent</c> is the monthly rent today; <c>DefaultSharePercent</c> the share the
/// window suggests (the contract states the real one, to be confirmed) and <c>MaxSharePercent</c> the most a share can be; the update can start
/// from a day between <c>MinEffectiveOn</c> (never in the past) and <c>MaxEffectiveOn</c> (the end of the period).
/// </summary>
public sealed record LeaseIstatOperationDto(
    bool Available,
    decimal CurrentRent,
    decimal DefaultSharePercent,
    decimal MaxSharePercent,
    DateOnly MinEffectiveOn,
    DateOnly MaxEffectiveOn);

/// <summary>
/// The communication of the cedolare secca to the tenant. <c>Available</c>: the lease is in force and under the cedolare secca; the option can be
/// said to apply from a day between <c>MinEffectiveOn</c> and <c>MaxEffectiveOn</c>.
/// </summary>
public sealed record LeaseCedolareNoticeDto(bool Available, DateOnly MinEffectiveOn, DateOnly MaxEffectiveOn);

public sealed record LeaseHandoverOperationDto(bool Available, DateOnly MinOn, DateOnly MaxOn, DateOnly SuggestedOn);

/// <summary>
/// The return of the security deposit. <c>Available</c>: the lease is ending and has a deposit, so the statement can be worked out. <c>Amount</c> is
/// the deposit; it is taken to have been paid on <c>PaidOn</c> (the start of the lease) unless the landlord says another day from <c>MinPaidOn</c>;
/// the return can be dated up to <c>MaxReturnOn</c> and the window suggests <c>SuggestedReturnOn</c>; at most <c>MaxDeductions</c> sums can be kept.
/// <c>Recorded</c> is the return the landlord recorded last, if any.
/// </summary>
public sealed record LeaseDepositOperationDto(
    bool Available,
    decimal Amount,
    DateOnly PaidOn,
    DateOnly MinPaidOn,
    DateOnly SuggestedReturnOn,
    DateOnly MaxReturnOn,
    int MaxDeductions,
    LeaseDepositRecordDto? Recorded);

public sealed record LeaseDepositRecordDto(DateOnly ReturnedOn, decimal Deposit, decimal InterestDue, decimal Deductions, decimal Returned);

public sealed record LeaseDepositYearDto(int Year, decimal RatePercent, int Days, int DaysInYear, decimal Interest);

public sealed record LeaseDepositDeductionDto(string Reason, decimal Amount);

public sealed record LeaseDepositCalculationDto(
    decimal Deposit,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<LeaseDepositYearDto> Years,
    decimal Interest,
    decimal InterestPaid,
    decimal InterestDue,
    IReadOnlyList<LeaseDepositDeductionDto> Deductions,
    decimal DeductionsTotal,
    decimal Returned);

/// <summary>
/// The ISTAT update worked out by the server (<c>POST api/leases/{id}/istat/calculate</c>): the rent today, the variation and the share typed,
/// what they make in percent of the rent, the increase and the new monthly rent (both to the cent). Nothing is written.
/// </summary>
public sealed record LeaseIstatCalculationDto(
    decimal CurrentRent, decimal FoiPercent, decimal SharePercent, decimal AppliedPercent, decimal Increase, decimal NewRent);
