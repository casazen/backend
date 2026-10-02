namespace Casazen.Core.Entities.Enums;

/// <summary>How a paid rent installment was collected (LT-06). Stored as an integer: never renumber.</summary>
public enum RentPaymentChannel
{
    /// <summary>Paid online by the tenant on the landlord's Stripe connected account (confirmed by the webhook).</summary>
    Stripe = 0,

    /// <summary>Declared paid by the landlord (bank transfer, cash…): no money went through CasaZen.</summary>
    Offline = 1,
}
