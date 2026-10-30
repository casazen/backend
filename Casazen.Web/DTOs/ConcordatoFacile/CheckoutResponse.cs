namespace Casazen.Web.Dtos.ConcordatoFacile;

/// <summary>
/// Response from creating a ConcordatoFacile checkout session (ADR-005).
/// Contains the Stripe session details needed to redirect the user to the payment page.
/// </summary>
public class CheckoutResponse
{
    /// <summary>Stripe Checkout Session ID for reference and tracking</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Stripe Checkout Session URL to redirect the user to</summary>
    public string SessionUrl { get; set; } = string.Empty;
}
