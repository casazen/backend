using Casazen.Core.Entities;

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
}
