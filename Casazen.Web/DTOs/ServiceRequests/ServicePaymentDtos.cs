using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.ServiceRequests;

/// <summary>
/// Body of the public payment endpoints (SP-15a): the token of the payment link. It is sent in the body, never in the URL of the
/// API call (the URLs of the API end up in logs). A missing or too long token is a 400 <c>validation_error</c>; any other token
/// that does not open the payment gets the same 404 as a wrong payment id.
/// </summary>
public sealed class ServicePaymentTokenRequest
{
    [Required(ErrorMessage = ServicePaymentErrors.LinkInvalidMessageKey)]
    [MaxLength(ServicePaymentLimits.TokenMaxLength, ErrorMessage = ServicePaymentErrors.LinkInvalidMessageKey)]
    public string Token { get; set; } = string.Empty;
}

public sealed class PublicServicePaymentDto
{
    public Guid Id { get; set; }

    public string SupplierName { get; set; } = string.Empty;

    public string ServiceName { get; set; } = string.Empty;

    public string PropertyName { get; set; } = string.Empty;

    public DateTime? CompletedAt { get; set; }

    public int AmountCents { get; set; }

    public string Currency { get; set; } = string.Empty;

    public IEnumerable<ServiceRequestPriceLineDto> Lines { get; set; } = [];

    public string State { get; set; } = string.Empty;

    public bool LastAttemptFailed { get; set; }

    public DateTime? ValidUntil { get; set; }

    internal static PublicServicePaymentDto From(PublicServicePayment payment) => new()
    {
        Id = payment.Id,
        SupplierName = payment.SupplierName,
        ServiceName = payment.ServiceName,
        PropertyName = payment.PropertyName,
        CompletedAt = payment.CompletedAt,
        AmountCents = payment.AmountCents,
        Currency = payment.Currency,
        Lines = payment.Lines.Select(line => new ServiceRequestPriceLineDto
        {
            Kind = line.Kind,
            Label = line.Label,
            AmountCents = line.AmountCents,
        }).ToList(),
        State = payment.State.ToString(),
        LastAttemptFailed = payment.LastAttemptFailed,
        ValidUntil = payment.ValidUntil,
    };
}

/// <summary>
/// What Stripe.js needs to confirm the payment with the Payment Element on the supplier's connected account
/// (<c>POST api/public/service-payments/{id}/payment-session</c> and <c>POST api/service-requests/{id}/payment-session</c>). The
/// client secret is a credential: the answer is never cached.
/// </summary>
public sealed class ServicePaymentSessionDto
{
    public Guid PaymentId { get; set; }

    public string ClientSecret { get; set; } = string.Empty;

    public string PublishableKey { get; set; } = string.Empty;

    public string? StripeAccountId { get; set; }

    public int AmountCents { get; set; }

    public string Currency { get; set; } = string.Empty;

    internal static ServicePaymentSessionDto From(ServicePaymentSession session) => new()
    {
        PaymentId = session.PaymentId,
        ClientSecret = session.ClientSecret,
        PublishableKey = session.PublishableKey,
        StripeAccountId = session.StripeAccountId,
        AmountCents = session.AmountCents,
        Currency = session.Currency,
    };
}

public sealed class RecordOfflinePaymentRequest
{
    /// <summary>Why the payment was received outside CasaZen: at most 500 characters. Required for a request paid inside CasaZen; optional otherwise.</summary>
    [MaxLength(ServicePaymentLimits.OfflineNoteMaxLength, ErrorMessage = "ServicePaymentOfflineReasonTooLong")]
    public string? Reason { get; set; }
}

/// <summary>
/// The payment of a request as the supplier sees it (<c>POST api/supplier/requests/{id}/payment-request</c> and
/// <c>.../payment/offline</c>): the split "price gross, CasaZen commission, net", and where it stands. The commission is the
/// supplier's own figure: the payer never sees it.
/// </summary>
public sealed class ServicePaymentDto
{
    public Guid Id { get; set; }

    public Guid ServiceRequestId { get; set; }

    public string RequestStatus { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public int AmountCents { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal CommissionPercent { get; set; }

    public int CommissionCents { get; set; }

    public int NetCents { get; set; }

    public string? PaidVia { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime? RequestedAt { get; set; }

    public DateTime? LastSentAt { get; set; }

    public int SentCount { get; set; }

    public string? OfflineNote { get; set; }

    internal static ServicePaymentDto From(ServiceRequestPayment payment, ServiceRequestStatus requestStatus) => new()
    {
        Id = payment.Id,
        ServiceRequestId = payment.ServiceRequestId,
        RequestStatus = requestStatus.ToString(),
        Status = payment.Status.ToString(),
        AmountCents = payment.AmountCents,
        Currency = payment.Currency.ToUpperInvariant(),
        CommissionPercent = payment.CommissionPercent,
        CommissionCents = payment.ApplicationFeeCents,
        NetCents = payment.NetCents,
        PaidVia = payment.PaidVia?.ToString(),
        PaidAt = payment.PaidAt,
        RequestedAt = payment.RequestedAt,
        LastSentAt = payment.LastSentAt,
        SentCount = payment.SentCount,
        OfflineNote = payment.OfflineNote,
    };
}
