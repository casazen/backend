using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.Leases;

/// <summary>
/// The letters CasaZen prepares for the landlord of a lease in force (LR-OPS-1). CasaZen only prepares the draft; the person signs and
/// sends it, and nothing is ever sent to the tenant from here. The names are written in the history of the lease and in the variables
/// of the environment (<c>LeaseLetters__Variants__{Kind}__…</c>): a name is never renamed, a new kind is added.
/// </summary>
public enum LeaseLetterKind
{
    Disdetta = 0,

    PresaAttoRecesso = 1,

    Rinnovo = 2,

    ConfermaEsigenza = 3,

    /// <summary>
    /// The landlord's request to the tenant to update the rent by the variation of the FOI index (LR-OPS-1c). The numbers are worked out by
    /// <see cref="LeaseIstatRules"/>; the contract has to give the faculty, and the letter is never prepared under the cedolare secca.
    /// </summary>
    AggiornamentoIstat = 4,

    /// <summary>
    /// The communication to the tenant, by registered mail, that the landlord has chosen the cedolare secca, with the renunciation of every
    /// update of the rent for as long as the option lasts (D.Lgs. 23/2011, art. 3).
    /// </summary>
    OpzioneCedolare = 5,

    Riconsegna = 6,

    RestituzioneCauzione = 7,
}

public enum LeaseLetterMeans
{
    RegisteredMail = 0,
    Pec = 1,
    ByHand = 2,
    Other = 3,
}

/// <summary>
/// The reasons for which the landlord may deny the renewal at the first expiry (art. 3, comma 1, L. 431/1998, lettere a-g): a closed list,
/// and the law asks the letter to state one of them (comma 2: "a pena di nullità"). The reasons the interface offers are exactly these.
/// </summary>
public enum LeaseDisdettaReason
{
    OwnUse = 0,

    LegalEntityUse = 1,

    TenantOtherHome = 2,

    DamagedBuilding = 3,

    Renovation = 4,

    TenantAbsent = 5,

    Sale = 6,
}

public enum TenantWithdrawalReason
{
    /// <summary>For serious reasons, which the law allows at any time with the notice of the rules (art. 3, comma 6, L. 431/1998).</summary>
    GraveMotivi = 0,

    ContractualFaculty = 1,
}

public static class LeaseLetterKinds
{
    public static string Slug(LeaseLetterKind kind) => kind switch
    {
        LeaseLetterKind.Disdetta => "disdetta",
        LeaseLetterKind.PresaAttoRecesso => "presa-atto-recesso",
        LeaseLetterKind.Rinnovo => "rinnovo",
        LeaseLetterKind.ConfermaEsigenza => "conferma-esigenza",
        LeaseLetterKind.AggiornamentoIstat => "aggiornamento-istat",
        LeaseLetterKind.OpzioneCedolare => "opzione-cedolare",
        LeaseLetterKind.Riconsegna => "riconsegna",
        LeaseLetterKind.RestituzioneCauzione => "restituzione-cauzione",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown letter"),
    };

    public static bool TryParseSlug(string? slug, out LeaseLetterKind kind)
    {
        foreach (var candidate in Enum.GetValues<LeaseLetterKind>())
        {
            if (string.Equals(Slug(candidate), slug, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}

/// <summary>What the landlord can do about the notice of a lease today (<see cref="LeaseOperationRules.Disdetta"/>).</summary>
/// <param name="Available">The lease is in force, renews by itself, is not ended and its period is not over: a letter makes sense.</param>
/// <param name="FirstPeriod">The period the lease is in is the first one: the law asks the landlord for one of the reasons.</param>
/// <param name="PeriodEnd">The last day of the period the lease is in (the day the lease ends if the notice is given).</param>
/// <param name="LastDayToGiveNotice">The last day by which the notice is on time (the notice months before the end); <c>null</c> for a type that does not renew.</param>
/// <param name="Late">Today is after that day: CasaZen still prepares the letter and says it may come too late, it does not judge.</param>
public sealed record LeaseDisdettaFacts(bool Available, bool FirstPeriod, DateOnly PeriodEnd, DateOnly? LastDayToGiveNotice, bool Late);

/// <summary>What the landlord can do about a withdrawal of the tenant today (<see cref="LeaseOperationRules.Withdrawal"/>).</summary>
/// <param name="Available">The lease is in force, has begun, is not ended and its period is not over: a withdrawal can be recorded.</param>
/// <param name="MinReceivedOn">The first day the withdrawal can have been received (the start of the lease).</param>
/// <param name="MaxReceivedOn">The last day (today).</param>
public sealed record LeaseWithdrawalFacts(bool Available, DateOnly MinReceivedOn, DateOnly MaxReceivedOn);

/// <summary>What the landlord can do about the renewal of a lease today (<see cref="LeaseOperationRules.Renewal"/>).</summary>
/// <param name="Available">The lease is in force, renews by itself, has no notice, is not ended, its period is not over and the stretch before the end has begun: a renewal can be recorded.</param>
/// <param name="OpensOn">The first day a renewal can be recorded: the last day to give notice, the day the stretch before the end begins. Recording a renewal moves the end forward, so it closes the window until the next period's.</param>
/// <param name="StartsOn">The first day of the new period (the day after the period the lease is in).</param>
/// <param name="StandardEnd">The last day of the standard renewal (the months of the type, <see cref="LongRentRules"/>).</param>
/// <param name="StandardMonths">The months of the standard renewal; 0 for a type that does not renew.</param>
/// <param name="LatestAgreedEnd">The furthest day an extension agreed by the parties can end on (a sanity bound, <see cref="LeaseOperationRules.MaxAgreedExtensionYears"/>).</param>
public sealed record LeaseRenewalFacts(bool Available, DateOnly OpensOn, DateOnly StartsOn, DateOnly StandardEnd, int StandardMonths, DateOnly LatestAgreedEnd);

/// <summary>What the landlord can do about the confirmation of the need of a transitory lease today (<see cref="LeaseOperationRules.TransitoryConfirmation"/>).</summary>
/// <param name="Applies">A transitory lease in force whose period is not over: the need has to be confirmed before it ends.</param>
/// <param name="Available">The need is the landlord's, so he prepares the letter; when it is the tenant's the tenant confirms.</param>
/// <param name="Holder">Whose need it is, as the contract states it; <c>null</c> when the lease does not say.</param>
/// <param name="ConfirmBy">The day before which the confirmation has to be sent: the last day of the lease. The law's lead time, if it gives one, is to be confirmed (LR-D29).</param>
public sealed record LeaseTransitoryConfirmationFacts(bool Applies, bool Available, PartyRole? Holder, DateOnly ConfirmBy);

/// <summary>What the landlord can do about the communication of the cedolare secca to the tenant today (<see cref="LeaseOperationRules.CedolareNotice"/>).</summary>
/// <param name="Available">The lease is in force, not ended, under the cedolare secca and its period is not over: the communication can be prepared.</param>
/// <param name="MinEffectiveOn">The first day the option can be said to apply from (the start of the lease).</param>
/// <param name="MaxEffectiveOn">The last day (the end of the period the lease is in).</param>
public sealed record LeaseCedolareNoticeFacts(bool Available, DateOnly MinEffectiveOn, DateOnly MaxEffectiveOn);

public sealed record LeaseHandoverFacts(bool Available, DateOnly MinOn, DateOnly MaxOn, DateOnly SuggestedOn);

/// <summary>What the landlord can do about the return of the security deposit today (<see cref="LeaseOperationRules.Deposit"/>).</summary>
/// <param name="Available">The lease is ending (as for the handover) and has a deposit: the statement of the return can be worked out.</param>
/// <param name="Amount">The deposit of the lease, in euros; 0 when it has none.</param>
/// <param name="PaidOn">The day the deposit is taken to have been paid unless the landlord says another: the start of the lease.</param>
/// <param name="MinPaidOn">The earliest day it can have been paid: a year before the start of the lease.</param>
/// <param name="SuggestedReturnOn">The day the window suggests: the last day of the lease, or today when the lease is over.</param>
/// <param name="MaxReturnOn">The last day a return can be dated: a few years after the end of the lease (a sanity bound).</param>
public sealed record LeaseDepositFacts(
    bool Available, decimal Amount, DateOnly PaidOn, DateOnly MinPaidOn, DateOnly SuggestedReturnOn, DateOnly MaxReturnOn);

public static class LeaseOperationRules
{
    public const int MaxContractualNoticeMonths = 12;

    public const int MaxAgreedExtensionYears = 20;

    public static bool IsFirstPeriod(LeaseContract lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return RomeCalendar.DateInRome(lease.CurrentTermEndDate) == RomeCalendar.DateInRome(lease.EndDate);
    }

    public static LeaseDisdettaFacts Disdetta(LeaseContract lease, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var periodEnd = RomeCalendar.DateInRome(lease.CurrentTermEndDate);
        var lastDay = LongRentDeadlineRules.NoticeDate(lease.ContractType, periodEnd);
        var tenantAlreadyGaveNotice = lease.NoticeOn is not null && lease.NoticeFrom == LeaseNoticeParty.Tenant;
        var available = LeaseRenewal.IsInForce(lease.Status)
                        && lastDay is not null
                        && lease.TerminatedOn is null
                        && !tenantAlreadyGaveNotice
                        && periodEnd >= today;

        return new LeaseDisdettaFacts(available, IsFirstPeriod(lease), periodEnd, lastDay, lastDay is { } last && today > last);
    }

    public static LeaseWithdrawalFacts Withdrawal(LeaseContract lease, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var start = RomeCalendar.DateInRome(lease.StartDate);
        var periodEnd = RomeCalendar.DateInRome(lease.CurrentTermEndDate);
        var available = LeaseRenewal.IsInForce(lease.Status) && lease.TerminatedOn is null && start <= today && periodEnd >= today;
        return new LeaseWithdrawalFacts(available, start, today);
    }

    /// <summary>
    /// The renewal the landlord can record now: the standard one (the months of the type, from the day after the period) or an extension to a
    /// day the parties agreed on. Only in the last stretch before the end — from the last day to give notice, when the renewal is the thing the
    /// parties decide — so that recording one (which moves the end forward) closes the window until the next period's. Not while a notice is
    /// recorded (it has to be taken back first) nor once the period is over (the hourly job renews it).
    /// </summary>
    public static LeaseRenewalFacts Renewal(LeaseContract lease, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var periodEnd = RomeCalendar.DateInRome(lease.CurrentTermEndDate);
        var months = LongRentRules.RenewalMonths(lease.ContractType);
        var standardEnd = LeaseRenewal.NextEnd(lease.CurrentTermEndDate, lease.ContractType) is { } next ? RomeCalendar.DateInRome(next) : periodEnd;
        var opensOn = LongRentDeadlineRules.NoticeDate(lease.ContractType, periodEnd) ?? periodEnd;
        var available = LeaseRenewal.IsInForce(lease.Status)
                        && months is not null
                        && lease.TerminatedOn is null
                        && lease.NoticeOn is null
                        && periodEnd >= today
                        && today >= opensOn;

        return new LeaseRenewalFacts(available, opensOn, periodEnd.AddDays(1), standardEnd, months ?? 0, periodEnd.AddYears(MaxAgreedExtensionYears));
    }

    public static LeaseTransitoryConfirmationFacts TransitoryConfirmation(LeaseContract lease, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var periodEnd = RomeCalendar.DateInRome(lease.CurrentTermEndDate);
        var applies = lease.ContractType == LeaseContractType.Transitorio
                      && LeaseRenewal.IsInForce(lease.Status)
                      && lease.TerminatedOn is null
                      && periodEnd >= today;

        return new LeaseTransitoryConfirmationFacts(applies, applies && lease.TransitoryParty == PartyRole.Landlord, lease.TransitoryParty, periodEnd);
    }

    /// <summary>
    /// Whether the communication of the cedolare secca to the tenant (with the renunciation of the updates of the rent) can be prepared: a lease
    /// in force that is under the cedolare secca. The contracts made in CasaZen already say it in a clause; the letter is for the others and for
    /// an option chosen later.
    /// </summary>
    public static LeaseCedolareNoticeFacts CedolareNotice(LeaseContract lease, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var start = RomeCalendar.DateInRome(lease.StartDate);
        var periodEnd = RomeCalendar.DateInRome(lease.CurrentTermEndDate);
        var available = LeaseRenewal.IsInForce(lease.Status)
                        && lease.TerminatedOn is null
                        && LeaseIstatRules.UpdatesWaived(lease)
                        && periodEnd >= today;
        return new LeaseCedolareNoticeFacts(available, start, periodEnd);
    }

    public static bool IsEnding(LeaseContract lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return lease.TerminatedOn is not null || lease.NoticeOn is not null || !LeaseRenewal.Renews(lease.ContractType);
    }

    public static LeaseHandoverFacts Handover(LeaseContract lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var start = RomeCalendar.DateInRome(lease.StartDate);
        var end = RomeCalendar.DateInRome(lease.CurrentTermEndDate);
        var available = LeaseRenewal.IsInForce(lease.Status) && IsEnding(lease);
        return new LeaseHandoverFacts(available, start, end.AddYears(1), end < start ? start : end);
    }

    public static LeaseDepositFacts Deposit(LeaseContract lease, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var start = RomeCalendar.DateInRome(lease.StartDate);
        var end = RomeCalendar.DateInRome(lease.CurrentTermEndDate);
        return new LeaseDepositFacts(
            LeaseDepositRules.Applies(lease),
            lease.SecurityDeposit ?? 0m,
            start,
            start.AddYears(-1),
            end > today ? end : today,
            end.AddYears(LeaseDepositRules.MaxYearsAfterEnd));
    }

    public static int WithdrawalNoticeMonths(TenantWithdrawalReason reason, int? contractualMonths) =>
        reason == TenantWithdrawalReason.ContractualFaculty && contractualMonths is { } months
            ? months
            : LongRentDeadlineRules.NoticeMonthsBeforeEnd;

    public static DateOnly WithdrawalEffectiveOn(DateOnly receivedOn, int noticeMonths) => receivedOn.AddMonths(noticeMonths);
}
