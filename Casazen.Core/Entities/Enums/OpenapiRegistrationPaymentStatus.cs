namespace Casazen.Core.Entities.Enums;

/// <summary>Status of the Openapi filing-fee checkout. The values match a one-time Stripe Checkout, as the concordato purchase does.</summary>
public enum OpenapiRegistrationPaymentStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
}
