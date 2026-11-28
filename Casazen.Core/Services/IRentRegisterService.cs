using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public enum RentRegisterStatus
{
    All = 0,
    Paid = 1,
    Pending = 2,
    Overdue = 3,
}

public sealed record RentRegisterQuery(DateOnly Month, RentRegisterStatus Status = RentRegisterStatus.All, int Page = 1, int PageSize = RentRegisterQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public DateOnly FirstDay => new(Month.Year, Month.Month, 1);

    public DateOnly LastDay => FirstDay.AddMonths(1).AddDays(-1);
}

public sealed record RentCounter(int Count, decimal Amount);

public sealed record RentRegisterCounters(RentCounter Expected, RentCounter Collected, RentCounter Pending, RentCounter Overdue);

/// <summary>
/// One installment of the register. The tenant is the first tenant of the lease, by name only (D29), and the names are null
/// once that tenant was anonymized (<c>TenantAnonymized</c>, LT-12). <c>CanRemind</c> is the answer to "can I send a reminder
/// now": the installment is not paid nor in flight, a tenant has an email address, and the interval since the last reminder
/// (<c>RentBilling:ReminderIntervalHours</c>) has passed. <c>CanWhatsAppRemind</c> applies the same status and interval rule,
/// but requires a tenant phone number instead of an email address.
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
    bool CanRemind,
    decimal RentAmount,
    decimal CondominiumAdvanceAmount,
    decimal CondominiumSettlementAmount,
    decimal OtherAmount,
    decimal AmountPaid,
    decimal AmountOutstanding,
    bool HasTenantPhone = false,
    bool CanWhatsAppRemind = false);

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

    Task<IReadOnlyList<RentInstallmentRef>> FindInstallmentsAsync(
        IReadOnlyCollection<Guid> installmentIds, CancellationToken cancellationToken = default);
}

public static class RentRegisterErrorCodes
{
    public const string ReminderTooSoon = "rent_reminder_too_soon";

    public const string ReminderNotSent = "rent_reminder_not_sent";

    public const string ReminderNoteInvalid = "rent_reminder_note_invalid";

    public const string MonthInvalid = "rent_register_month_invalid";

    public const string StatusUnknown = "rent_register_status_unknown";

    public const string BatchInvalid = "rent_reminder_batch_invalid";
}
