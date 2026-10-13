using Casazen.Core.Authorization;
using Casazen.Core.Leases;

namespace Casazen.Core.Services;

/// <summary>
/// What the agenda is asked for: the days from <see cref="From"/> to <see cref="To"/> (both included, Europe/Rome calendar
/// days) and, optionally, one type of deadline. A deadline that is already past and still open (a registration, a Questura
/// communication or a rent installment not done) is listed whenever the window contains today, flagged overdue.
/// </summary>
public sealed record LongRentDeadlinesQuery(DateOnly From, DateOnly To, LongRentDeadlineType? Type = null);

/// <summary>
/// One deadline of the agenda. <c>Date</c> is the Rome calendar day; <c>DaysFromToday</c> is negative when it is past
/// (<c>IsOverdue</c>). The tenant is the first tenant of the lease, by name only (D29), null once anonymized. For
/// <see cref="LongRentDeadlineType.Rent"/> the installment and its amount are given.
/// </summary>
public sealed record LongRentDeadline(
    LongRentDeadlineType Type,
    DateOnly Date,
    int DaysFromToday,
    bool IsOverdue,
    Guid LeaseId,
    Guid PropertyId,
    string PropertyName,
    string? TenantFirstName,
    string? TenantLastName,
    Guid? InstallmentId,
    decimal? Amount);

/// <summary>The deadlines of a window, in date order. <c>Truncated</c>: more installments were due than the answer carries.</summary>
public sealed record LongRentDeadlines(DateOnly From, DateOnly To, IReadOnlyList<LongRentDeadline> Items, bool Truncated);

/// <summary>The leases of the area counted by where they stand (the views of the lease list, plus what waits for the landlord).</summary>
/// <param name="Active">Registered and not ended (includes <paramref name="Expiring"/>).</param>
/// <param name="Expiring">Registered, ending within six months.</param>
/// <param name="InPreparation">Not registered yet: <paramref name="ToSign"/> plus <paramref name="ToRegister"/>.</param>
/// <param name="ToSign">Draft or in signature.</param>
/// <param name="ToRegister">Signed, the registration still to be done (or in progress).</param>
/// <param name="Ended">Registered and ended, or rejected.</param>
public sealed record LongRentLeaseCounters(int Active, int Expiring, int InPreparation, int ToSign, int ToRegister, int Ended);

/// <summary>What waits for the landlord, in the order of urgency.</summary>
public enum LongRentChecklistKind
{
    /// <summary>Rent installments past due and not paid. <c>Date</c>: the oldest due date; <c>LeaseId</c>: its lease; <c>Amount</c>: all of them.</summary>
    RentOverdue = 0,

    /// <summary>Leases signed and not registered. <c>Date</c>: the nearest registration deadline; <c>LeaseId</c>: its lease.</summary>
    RliRegistration = 1,

    /// <summary>Questura communications of extra-EU tenants not declared. <c>Date</c>: the nearest deadline; <c>LeaseId</c>: its lease.</summary>
    Questura = 2,

    /// <summary>Leases not signed yet. <c>Date</c>: the nearest start date; <c>LeaseId</c>: its lease.</summary>
    LeaseToSign = 3,
}

/// <summary>
/// One line of the checklist: <c>Count</c> things of this kind wait; <c>Date</c> and <c>LeaseId</c> point at the most pressing
/// one. <c>IsOverdue</c>: that date has passed. <c>Amount</c> only for <see cref="LongRentChecklistKind.RentOverdue"/>.
/// </summary>
public sealed record LongRentChecklistItem(
    LongRentChecklistKind Kind,
    int Count,
    DateOnly? Date,
    Guid? LeaseId,
    decimal? Amount,
    bool IsOverdue);

/// <summary>
/// The overview of the long-term area (LR-01, B2): the numbers of the area, the rent of the current month, the checklist and the
/// next deadline. <c>NextDeadline</c> is the first one that is not past, within <see cref="LongRentDeadlineRules.DefaultWindowDays"/> days.
/// </summary>
public sealed record LongRentOverview(
    DateOnly Today,
    LongRentLeaseCounters Leases,
    string RentMonth,
    RentRegisterCounters Rents,
    IReadOnlyList<LongRentChecklistItem> Checklist,
    LongRentDeadline? NextDeadline);

/// <summary>
/// The agenda and the overview of the long-term area (LR-01, B2): read side only, for the leases the caller reaches
/// (<see cref="HostScope"/>, TN-3), computed on today's date in Europe/Rome.
/// </summary>
public interface ILongRentAgendaService
{
    /// <summary>
    /// The deadlines of the window, in date order: registration (RLI), Questura communication, end of the contract, last day of
    /// notice (six months before the end of a 4+4 or 3+2 contract) and the rent installments not paid. Only what is still to be
    /// done: a registration already made, a communication already declared or an installment already paid are not listed.
    /// </summary>
    Task<LongRentDeadlines> GetDeadlinesAsync(HostScope scope, LongRentDeadlinesQuery query, CancellationToken cancellationToken = default);

    Task<LongRentOverview> GetOverviewAsync(HostScope scope, CancellationToken cancellationToken = default);
}

/// <summary>Stable codes of the agenda (LR-01, B2): answered as 400.</summary>
public static class LongRentAgendaErrorCodes
{
    /// <summary>The window is not made of two valid dates, <c>to</c> is before <c>from</c>, or it is longer than <see cref="LongRentDeadlineRules.MaxWindowDays"/> days.</summary>
    public const string RangeInvalid = "long_rent_deadlines_range_invalid";

    /// <summary>The type is not one of <see cref="LongRentDeadlineType"/>.</summary>
    public const string TypeUnknown = "long_rent_deadlines_type_unknown";
}
