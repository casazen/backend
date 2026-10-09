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

    /// <summary>Stripe confirmed the refund (the answer of the call, or a webhook).</summary>
    Succeeded = 1,

    /// <summary>Stripe refused it, or it failed afterwards. Nothing was given back; the amount is free again.</summary>
    Failed = 2,

    /// <summary>Stripe canceled it. Nothing was given back; the amount is free again.</summary>
    Canceled = 3,

    /// <summary>Stripe needs an action of the payer's bank before it completes. The amount stays reserved.</summary>
    RequiresAction = 4,
}

/// <summary>Who started a refund of a service payment (<see cref="ServiceRequestPaymentRefund.Origin"/>). Stored as an integer: append only.</summary>
public enum ServicePaymentRefundOrigin
{
    /// <summary>A platform admin, from <c>POST api/admin/service-payments/{id}/refund</c>.</summary>
    Admin = 0,

    /// <summary>Made outside CasaZen (Stripe Dashboard or API) and learnt from a webhook: recorded so the payment reflects it.</summary>
    Stripe = 1,
}
