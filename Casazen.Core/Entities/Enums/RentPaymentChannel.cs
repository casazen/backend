namespace Casazen.Core.Entities.Enums;

public enum RentPaymentChannel
{
    /// <summary>Paid online by the tenant on the landlord's Stripe connected account (confirmed by the webhook).</summary>
    Stripe = 0,

    Offline = 1,
}
