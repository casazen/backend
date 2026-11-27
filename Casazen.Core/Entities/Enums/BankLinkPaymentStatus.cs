namespace Casazen.Core.Entities.Enums;

/// <summary>
/// State of a debit started from a linked Financial Connections account. <see cref="Succeeded"/> is written only when
/// Stripe reports <c>payment_intent.succeeded</c> (webhook, or the rent job reading a payment that stayed in flight).
/// </summary>
public enum BankLinkPaymentStatus
{
    Processing = 0,
    Succeeded = 1,
    Failed = 2,
    Canceled = 3,
}
