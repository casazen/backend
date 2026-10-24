namespace Casazen.Core.Entities.Enums;

/// <summary>Status of a ConcordatoFacile one-time purchase (ADR-004).</summary>
public enum ConcordatoFacilePurchaseStatus
{
    /// <summary>Checkout session created, payment not yet completed.</summary>
    Pending = 0,

    /// <summary>Payment succeeded, purchase completed.</summary>
    Completed = 1,

    /// <summary>Payment failed or checkout session expired.</summary>
    Failed = 2,

    /// <summary>Payment was refunded.</summary>
    Refunded = 3,
}
