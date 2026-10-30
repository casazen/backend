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
    /// <summary>The landlord's notice to the tenant that the lease will not renew (the denial of renewal at the first expiry, the renunciation after).</summary>
    Disdetta = 0,

    /// <summary>The landlord's acknowledgement of the tenant's withdrawal (recesso).</summary>
    PresaAttoRecesso = 1,

    /// <summary>
    /// The agreement of the parties to renew or to extend the lease (LR-OPS-1): the standard renewal at the expiry, or an extension to a
    /// day they agreed on. Both parties sign it; it is not sent.
    /// </summary>
    Rinnovo = 2,

    /// <summary>
    /// The confirmation, sent before the expiry, that the need that justified a transitory lease goes on (D.M. 16/01/2017); the landlord
    /// prepares it when the need is his.
    /// </summary>
    ConfermaEsigenza = 3,
}

/// <summary>How the person sent a letter (<c>Segna come inviata</c>): written in the history of the lease with the day, nothing else.</summary>
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
    /// <summary>Lettera a): the landlord wants the home for his own use, or for his spouse, parents, children or relatives up to the second degree.</summary>
    OwnUse = 0,

    /// <summary>Lettera b): the landlord is a company, an entity or a body with public or social aims and wants the home for its activities, offering another suitable one.</summary>
    LegalEntityUse = 1,

    /// <summary>Lettera c): the tenant has the full availability of another free and suitable home in the same comune.</summary>
    TenantOtherHome = 2,

    /// <summary>Lettera d): the home is in a seriously damaged building that must be rebuilt or made stable.</summary>
    DamagedBuilding = 3,

    /// <summary>Lettera e): the building is to be renovated as a whole, demolished or transformed, or raised by a floor.</summary>
    Renovation = 4,

    /// <summary>Lettera f): the tenant does not live in the home continuously, without a good reason.</summary>
    TenantAbsent = 5,

    /// <summary>Lettera g): the landlord wants to sell the home to a third party and owns no other home.</summary>
    Sale = 6,
}

/// <summary>Why the tenant withdraws from the lease before its end (recesso), as the landlord records it.</summary>
public enum TenantWithdrawalReason
{
    /// <summary>For serious reasons, which the law allows at any time with the notice of the rules (art. 3, comma 6, L. 431/1998).</summary>
    GraveMotivi = 0,

    /// <summary>By the faculty the contract itself gives, with the notice the contract states.</summary>
    ContractualFaculty = 1,
}

/// <summary>The address names of the letters (<c>/letters/{slug}</c>); never renamed.</summary>
public static class LeaseLetterKinds
{
    public static string Slug(LeaseLetterKind kind) => kind switch
    {
        LeaseLetterKind.Disdetta => "disdetta",
        LeaseLetterKind.PresaAttoRecesso => "presa-atto-recesso",
        LeaseLetterKind.Rinnovo => "rinnovo",
        LeaseLetterKind.ConfermaEsigenza => "conferma-esigenza",
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

/// <summary>
/// The rules of the operations of a lease in force (LR-OPS-1), pure: when a notice is on time, what a withdrawal ends the lease on, which
/// operation makes sense now. The months and days are the figures of <see cref="LongRentRules"/> and <see cref="LongRentDeadlineRules"/>,
/// the product values a lawyer has still to confirm (decision LR-D29): none is copied here or in the interface.
/// </summary>
public static class LeaseOperationRules
{
    /// <summary>The longest notice, in months, a landlord can type for a withdrawal by contractual faculty: a sanity bound, not a figure of law.</summary>
    public const int MaxContractualNoticeMonths = 12;

    /// <summary>How far past the end of the period an extension agreed by the parties can go, in years: a sanity bound against a typo, not a figure of law.</summary>
    public const int MaxAgreedExtensionYears = 20;

    /// <summary>The period the lease is in is the first one it has: it has not renewed by itself yet (<see cref="LeaseContract.EndDate"/> does not move).</summary>
    public static bool IsFirstPeriod(LeaseContract lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return RomeCalendar.DateInRome(lease.CurrentTermEndDate) == RomeCalendar.DateInRome(lease.EndDate);
    }

    /// <summary>The notice of non-renewal that the landlord can still prepare today, and by when it is on time.</summary>
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

    /// <summary>Whether a withdrawal of the tenant can be recorded now, and the days it can have been received on.</summary>
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

    /// <summary>The confirmation of the need of a transitory lease: whether it applies, whose it is and the day before which it has to be sent.</summary>
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

    /// <summary>The notice, in months, of a withdrawal: the one of the rules for serious reasons, the one typed (or the rules') for the contractual faculty.</summary>
    public static int WithdrawalNoticeMonths(TenantWithdrawalReason reason, int? contractualMonths) =>
        reason == TenantWithdrawalReason.ContractualFaculty && contractualMonths is { } months
            ? months
            : LongRentDeadlineRules.NoticeMonthsBeforeEnd;

    /// <summary>The last day of the lease when a withdrawal received on <paramref name="receivedOn"/> takes effect (the day the notice runs out).</summary>
    public static DateOnly WithdrawalEffectiveOn(DateOnly receivedOn, int noticeMonths) => receivedOn.AddMonths(noticeMonths);
}
