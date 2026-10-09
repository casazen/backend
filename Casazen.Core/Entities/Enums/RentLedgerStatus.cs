namespace Casazen.Core.Entities.Enums;

/// <summary>
/// State of one rent installment (LT-06). Stored as an integer: never renumber.
/// <list type="bullet">
/// <item><see cref="Scheduled"/>: to be collected (not paid yet; overdue is computed on read from the due date).</item>
/// <item><see cref="Processing"/>: Stripe reported the payment in flight (e.g. SEPA Debit); the webhook settles it.</item>
/// <item><see cref="Paid"/>: Stripe reported <c>succeeded</c>, or the landlord declared an offline payment.</item>
/// <item><see cref="Failed"/>: the last online payment failed; the tenant can pay again from the link.</item>
/// <item><see cref="Cancelled"/>: the schedule was disabled before the installment was paid.</item>
/// </list>
/// </summary>
public enum RentLedgerStatus
{
    Scheduled = 0,
    Processing = 1,
    Paid = 2,
    Failed = 3,
    Cancelled = 4,
}
