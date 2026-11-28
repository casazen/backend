namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the payment of a service request is (<see cref="ServiceRequestPayment.Status"/>). Stored as an integer and serialized
/// by name: append only, never renumber. SP-15a creates a payment as <see cref="Requested"/>, drops it as
/// <see cref="Canceled"/> and records an offline one as <see cref="Paid"/>; the other states are reached by the Stripe webhook
/// and by the refunds of SP-15b.
/// </summary>
public enum ServicePaymentStatus
{
    Requested = 0,

    Processing = 1,

    Paid = 2,

    Failed = 3,

    Canceled = 4,

    PartiallyRefunded = 5,

    Refunded = 6,

    /// <summary>
    /// Stripe reported a payment that does not match what CasaZen asked for (another account, amount, currency or fee): never
    /// shown as paid, an admin decides (SP-15b).
    /// </summary>
    NeedsReview = 7,

    Authorized = 8,

    Held = 9,

    Released = 10,

    Disputed = 11,

    /// <summary>
    /// The transfer to the supplier was refused by Stripe: the money is still held, the release job tries again with a growing delay
    /// (Stripe does not retry a failed transfer by itself). Visible to the admin, never two transfers for one payment.
    /// </summary>
    TransferFailed = 12,
}
