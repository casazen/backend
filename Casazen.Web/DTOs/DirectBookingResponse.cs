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
}
