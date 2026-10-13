namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the payment of a service request is (<see cref="ServiceRequestPayment.Status"/>). Stored as an integer and serialized
/// by name: append only, never renumber. SP-15a creates a payment as <see cref="Requested"/>, drops it as
/// <see cref="Canceled"/> and records an offline one as <see cref="Paid"/>; the other states are reached by the Stripe webhook
/// and by the refunds of SP-15b.
/// </summary>
public enum ServicePaymentStatus
{
    /// <summary>Created, waiting for the payer. Nothing was collected; a PaymentIntent exists only after the payer opened the payment session.</summary>
    Requested = 0,

    /// <summary>Stripe is processing the payment (e.g. a SEPA debit): not paid yet, not payable again.</summary>
    Processing = 1,

    /// <summary>Stripe confirmed the payment, or the supplier recorded it as received outside CasaZen. The request is <c>Pagato</c>.</summary>
    Paid = 2,

    /// <summary>The last attempt failed; the payer can try again with the same link.</summary>
    Failed = 3,

    /// <summary>
    /// Dropped before it was paid (replaced by an offline record, withdrawn). The only state that frees the request for a new
    /// payment: the unique index of "one live payment per request" ignores it.
    /// </summary>
    Canceled = 4,

    /// <summary>Paid, then partly refunded (SP-15b).</summary>
    PartiallyRefunded = 5,

    /// <summary>Paid, then refunded in full (SP-15b).</summary>
    Refunded = 6,

    /// <summary>
    /// Stripe reported a payment that does not match what CasaZen asked for (another account, amount, currency or fee): never
    /// shown as paid, an admin decides (SP-15b).
    /// </summary>
    NeedsReview = 7,
}
