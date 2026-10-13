namespace Casazen.Core.Entities.Enums;

/// <summary>How a service request payment was collected (<see cref="ServiceRequestPayment.PaidVia"/>). Stored as an integer: append only.</summary>
public enum ServicePaymentChannel
{
    /// <summary>Paid online by the payer, direct charge on the supplier's Stripe account (confirmed by the webhook).</summary>
    Stripe = 0,

    /// <summary>
    /// Recorded by the supplier as received outside CasaZen: no money went through CasaZen and no commission was charged (D5).
    /// </summary>
    Offline = 1,
}
