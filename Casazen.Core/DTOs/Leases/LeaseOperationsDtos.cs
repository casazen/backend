using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;

namespace Casazen.Core.DTOs.Leases;

// API contract of the operations of a lease in force (LR-OPS-1, GET api/leases/{id}/operations): what the landlord can do about the end of
// the lease today, computed on the server. A client shows it and does not work it out: no month or day of law is written in the
// interface (the rules are GET api/long-rent/rules), and no name or address of a person is in these types.

/// <summary>
/// The operations of one lease. <c>Letters</c> has one entry per letter kind CasaZen can prepare, with whether its text is approved (a
/// draft that is not approved is printed with a watermark) and when the landlord marked it as sent.
/// </summary>
public sealed record LeaseOperationsDto(
    LeaseDisdettaOperationDto Disdetta,
    LeaseWithdrawalOperationDto Withdrawal,
    IReadOnlyList<LeaseLetterStateDto> Letters,
    LeaseRenewalOperationDto Renewal,
    LeaseTransitoryConfirmationDto TransitoryConfirmation);

/// <summary>The state of a letter kind: its text is approved or still a draft, and the last time the landlord marked it as sent.</summary>
public sealed record LeaseLetterStateDto(LeaseLetterKind Kind, bool Approved, LeaseLetterSentDto? Sent);

/// <summary>The day the landlord marked a letter as sent and how.</summary>
public sealed record LeaseLetterSentDto(DateOnly On, LeaseLetterMeans Means);

/// <summary>
/// The notice of non-renewal. <c>Available</c>: a letter can be prepared. <c>PeriodEnd</c> is the day the lease ends if the notice is given;
/// <c>LastDayToGiveNotice</c> the last day by which it is on time (<c>null</c> for a type that does not renew) and <c>Late</c> says today is
/// after it. <c>FirstPeriod</c>: the lease is in its first period, so the letter must state one of <c>Reasons</c> (the closed list of the law);
/// later periods have none.
/// </summary>
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

/// <summary>A withdrawal that was recorded: the day it was received, why, the last day of the lease and the months of notice used.</summary>
public sealed record LeaseWithdrawalRecordDto(DateOnly ReceivedOn, TenantWithdrawalReason Reason, DateOnly EffectiveOn, int NoticeMonths);

/// <summary>
/// The renewal of the lease. <c>Available</c>: it can be recorded now (from <c>OpensOn</c>, the last day to give notice, to the end of the period;
/// recording one moves the end forward and closes the window until the next period's). The standard renewal starts on <c>StartsOn</c> and ends on
/// <c>StandardEnd</c> (<c>StandardMonths</c> months); an extension the parties agreed on can end on any later day up to <c>LatestAgreedEnd</c>.
/// <c>Conflict</c> is the lease of the same property that already covers some of the days of the standard renewal (it would be refused with
/// 409 <c>lease_period_overlaps</c>), <c>Recorded</c> the renewal recorded for the period the lease is in now (null once the lease has moved on).
/// </summary>
public sealed record LeaseRenewalOperationDto(
    bool Available,
    DateOnly OpensOn,
    DateOnly StartsOn,
    DateOnly StandardEnd,
    int StandardMonths,
    DateOnly LatestAgreedEnd,
    LeasePeriodConflictDto? Conflict,
    LeaseRenewalRecordDto? Recorded);

/// <summary>The lease that is in the way of a period: its id (not a person) and the first and last day it covers.</summary>
public sealed record LeasePeriodConflictDto(Guid LeaseId, DateOnly From, DateOnly To);

/// <summary>A renewal that was recorded: whether it was agreed on a day (otherwise the standard one) and the period it added.</summary>
public sealed record LeaseRenewalRecordDto(bool Agreed, DateOnly From, DateOnly To);

/// <summary>
/// The confirmation of the need of a transitory lease. <c>Applies</c>: it is a transitory lease in force and the need has to be confirmed in
/// writing before the end (<c>ConfirmBy</c>, its last day). <c>Available</c>: the need is the landlord's, so CasaZen prepares the letter;
/// <c>Holder</c> says whose it is (<c>Landlord</c> or <c>Tenant</c>, or none when the contract does not say).
/// </summary>
public sealed record LeaseTransitoryConfirmationDto(bool Applies, bool Available, PartyRole? Holder, DateOnly ConfirmBy);
