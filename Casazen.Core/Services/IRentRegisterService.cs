using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Which installments of the month the rent register lists (LR-01, B1). The three states are the ones a landlord reads at
/// a glance and are derived from the ledger (<see cref="Leases.RentInstallmentRules"/>): <see cref="Paid"/> is
/// <see cref="RentLedgerStatus.Paid"/>; <see cref="Overdue"/> is not paid and past its due date; <see cref="Pending"/> is the
/// rest that is still expected (not yet due, or a payment in flight). A cancelled installment is not listed in any of them.
/// </summary>
public enum RentRegisterStatus
{
    All = 0,
    Paid = 1,
    Pending = 2,
    Overdue = 3,
}

/// <summary>
/// The register of a month: the installments whose <b>due date</b> falls in <see cref="Month"/> (any day of it), narrowed by
/// <see cref="Status"/>, <see cref="PageSize"/> at a time. Ordered by due date, then by id, so a page never repeats or skips a row.
/// </summary>
public sealed record RentRegisterQuery(DateOnly Month, RentRegisterStatus Status = RentRegisterStatus.All, int Page = 1, int PageSize = RentRegisterQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>First day of the month asked for.</summary>
    public DateOnly FirstDay => new(Month.Year, Month.Month, 1);

    /// <summary>Last day of the month asked for.</summary>
    public DateOnly LastDay => FirstDay.AddMonths(1).AddDays(-1);
}

/// <summary>A number of installments and what they add up to.</summary>
public sealed record RentCounter(int Count, decimal Amount);

/// <summary>
/// The numbers of the month, whatever the status filter and the page (they do not change when the list is narrowed):
/// <c>Expected</c> is everything due in the month and not cancelled, split into <c>Collected</c> (paid), <c>Overdue</c> (past
/// due and not paid) and <c>Pending</c> (still to come, or a payment in flight).
/// </summary>
public sealed record RentRegisterCounters(RentCounter Expected, RentCounter Collected, RentCounter Pending, RentCounter Overdue);

/// <summary>
/// One installment of the register. The tenant is the first tenant of the lease, by name only (D29), and the names are null
/// once that tenant was anonymized (<c>TenantAnonymized</c>, LT-12). <c>CanRemind</c> is the answer to "can I send a reminder
/// now": the installment is not paid nor in flight, a tenant has an email address, and the interval since the last reminder
/// (<c>RentBilling:ReminderIntervalHours</c>) has passed.
/// </summary>
public sealed record RentRegisterRow(
    Guid Id,
    Guid LeaseId,
    Guid PropertyId,
    string PropertyName,
    string? TenantFirstName,
    string? TenantLastName,
    bool TenantAnonymized,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly DueDate,
    decimal Amount,
    string Currency,
    RentLedgerStatus Status,
    bool IsOverdue,
    int? DaysOverdue,
    RentPaymentChannel? PaidVia,
    DateOnly? PaidOn,
    DateTime? PaymentRequestedAt,
    DateTime? LastReminderAt,
    int ReminderCount,
    bool CanRemind);

/// <summary>
/// A page of the register. <c>OnlinePaymentsAvailable</c>: the org accepts rent online (Stripe Connect), so a reminder carries
/// the payment link; otherwise it is only a reminder. <c>ReminderIntervalHours</c>: the least time between two reminders of the
/// same installment.
/// </summary>
public sealed record RentRegisterPage(
    string Month,
    RentRegisterCounters Counters,
    IReadOnlyList<RentRegisterRow> Items,
    int Total,
    int Page,
    int PageSize,
    bool OnlinePaymentsAvailable,
    int ReminderIntervalHours);

/// <summary>The lease and the property an installment belongs to: what the caller authorizes before it changes the installment.</summary>
public sealed record RentInstallmentRef(Guid InstallmentId, Guid LeaseId, Guid PropertyId);

/// <summary>
/// The rent register of the long-term area (LR-01, B1): the installments of every lease the caller reaches, month by month,
/// with the numbers of the month. Read side only; the reminders change the installments through
/// <see cref="IRentBillingService.SendReminderAsync"/>, under the lock of the lease.
/// </summary>
public interface IRentRegisterService
{
    /// <summary>
    /// A page of the register for <paramref name="scope"/> (its org, and its properties when the scope is restricted: TN-3).
    /// Three statements at most whatever the size of the page: the numbers of the month, the page with its total, and the
    /// org's Stripe state.
    /// </summary>
    Task<RentRegisterPage> GetRegisterAsync(HostScope scope, RentRegisterQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The lease and property of each installment that exists in the caller's org (tenant filter); an id that does not exist,
    /// or belongs to another org, is simply missing from the answer. No ownership check: the caller authorizes each property.
    /// </summary>
    Task<IReadOnlyList<RentInstallmentRef>> FindInstallmentsAsync(
        IReadOnlyCollection<Guid> installmentIds, CancellationToken cancellationToken = default);
}

/// <summary>Stable codes of the rent register and of its reminders (LR-01). Answered as 400, 404, 409 or 422.</summary>
public static class RentRegisterErrorCodes
{
    /// <summary>422: the installment was reminded less than <c>RentBilling:ReminderIntervalHours</c> ago.</summary>
    public const string ReminderTooSoon = "rent_reminder_too_soon";

    /// <summary>422: no reminder could be queued (the email service is not available); nothing was recorded.</summary>
    public const string ReminderNotSent = "rent_reminder_not_sent";

    /// <summary>400: the note of the reminder is longer than <see cref="RentCharges.MaxReminderNoteLength"/> characters.</summary>
    public const string ReminderNoteInvalid = "rent_reminder_note_invalid";

    /// <summary>400: the register was asked for a month that is not <c>yyyy-MM</c>.</summary>
    public const string MonthInvalid = "rent_register_month_invalid";

    /// <summary>400: the register was asked for a status that is not <c>all</c>, <c>paid</c>, <c>pending</c> or <c>overdue</c>.</summary>
    public const string StatusUnknown = "rent_register_status_unknown";

    /// <summary>400: a bulk reminder with no installment, with more than <see cref="RentCharges.MaxBulkReminders"/>, or with an empty id.</summary>
    public const string BatchInvalid = "rent_reminder_batch_invalid";
}
