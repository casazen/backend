using Casazen.Core.Entities;
using Casazen.Core.TouristTax;

namespace Casazen.Web.DTOs;

public class ConnectedAccountPublishableContext
{
    public string PublishableKey { get; set; } = string.Empty;
    public string StripeAccountId { get; set; } = string.Empty;
}

public class DirectBookingResponse
{
    public Guid BookingId { get; set; }
    public string ClientSecret { get; set; } = string.Empty;
    public string? SetupIntentClientSecret { get; set; }
    public ConnectedAccountPublishableContext ConnectedAccountPublishableContext { get; set; } = null!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public decimal TouristTaxAmount { get; set; }
    public decimal BasePrice { get; set; }
    public DateTime FreeRefundDeadline { get; set; }
    public PaymentOption PaymentOption { get; set; } = PaymentOption.Immediate;

    /// <summary>
    /// Whether <see cref="TouristTaxAmount"/> was calculated. <c>RateUnavailable</c> / <c>CategoryRequired</c>: the
    /// tax is not included in <see cref="Amount"/> (0), it is not known to CasaZen.
    /// </summary>
    public TouristTaxQuoteStatus TouristTaxStatus { get; set; } = TouristTaxQuoteStatus.Calculated;

    /// <summary>
    /// "Pay at the property" only (BK-06, D5): the booking is a request, not confirmed. The guest must confirm the email
    /// (link sent by email) by this instant, then the host accepts or declines.
    /// </summary>
    public DateTime? EmailConfirmationExpiresAt { get; set; }

    /// <summary>
    /// Token of the outcome page (BK-07): <c>/book/{orgSlug}/booking/{bookingId}?token=…</c> shows the real state of the
    /// booking and lets the guest pay the same hold again. Given only here; the database keeps its hash.
    /// </summary>
    public string CheckoutToken { get; set; } = string.Empty;
}
