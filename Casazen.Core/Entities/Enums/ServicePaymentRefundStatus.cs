namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where one refund of a service payment is (<see cref="ServiceRequestPaymentRefund.Status"/>, SP-15b). Stored as an integer:
/// append only, never renumber. Only a <see cref="Succeeded"/> refund counts in <see cref="ServiceRequestPayment.RefundedCents"/>
/// and in the <c>PartiallyRefunded</c> / <c>Refunded</c> status of the payment: the money is never shown as given back before
/// Stripe says so.
/// </summary>
public enum ServicePaymentRefundStatus
{
    /// <summary>
    /// Written before Stripe is called, so its number is part of the idempotency key of the Stripe request; Stripe has not
    /// confirmed it yet. The amount is reserved: it cannot be refunded twice.
    /// </summary>
    Pending = 0,

    Succeeded = 1,

    Failed = 2,

    Canceled = 3,

    RequiresAction = 4,
}

public enum ServicePaymentRefundOrigin
{
    Admin = 0,

    Stripe = 1,

    /// <summary>
    /// Made by CasaZen itself on a payment held by the platform (SP-15c-a): the money given back because the request was refused,
    /// cancelled or lapsed after the charge, or the decision of a dispute (SP-15c-c). The cancellation of a block is not a refund and
    /// has no row.
    /// </summary>
    System = 2,
}
