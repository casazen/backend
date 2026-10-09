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

/// <summary>
/// The payment page of a link (<c>POST api/public/service-payments/{id}</c>): who asks for what, and where the payment stands. No
/// commission, no address and no contact of anyone: the link is the only access check (the names are the supplier's business name, the service and the payer's own property).
/// </summary>
public sealed class PublicServicePaymentDto
{
    public Guid Id { get; set; }

    /// <summary>The supplier that is paid.</summary>
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>The service: the name it had in the supplier's catalog, else its category.</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>The payer's own property the work was done at.</summary>
    public string PropertyName { get; set; } = string.Empty;

    public DateTime? CompletedAt { get; set; }

    /// <summary>What the payer pays, in cents.</summary>
    public int AmountCents { get; set; }

    /// <summary>ISO currency in capitals (<c>EUR</c>).</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>The lines that add up to <see cref="AmountCents"/>: the agreed price and the extras.</summary>
    public IEnumerable<ServiceRequestPriceLineDto> Lines { get; set; } = [];

    /// <summary><c>Payable</c>, <c>Processing</c>, <c>Paid</c> or <c>Unavailable</c>.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The last online attempt failed: the payer can try again.</summary>
    public bool LastAttemptFailed { get; set; }

    /// <summary>When the link stops working; only while the payment is <c>Payable</c>.</summary>
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

    /// <summary>The supplier's connected account (<c>acct_…</c>): Stripe.js loads the PaymentIntent from it.</summary>
    public string StripeAccountId { get; set; } = string.Empty;

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

/// <summary>
/// Body of <c>POST api/supplier/requests/{id}/payment/offline</c> (SP-15a, decision D5). For a request paid inside CasaZen the
/// reason is required: it is the trace of the exception, and the host is shown it.
/// </summary>
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

    /// <summary>The status of the request after the call (<c>Completato</c>, <c>Pagato</c>…).</summary>
    public string RequestStatus { get; set; } = string.Empty;

    /// <summary><c>Requested</c>, <c>Processing</c>, <c>Paid</c>, <c>Failed</c>, <c>Canceled</c>, <c>PartiallyRefunded</c>, <c>Refunded</c> or <c>NeedsReview</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The price the payer pays, in cents (gross).</summary>
    public int AmountCents { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary>The commission percentage in force when the payment was created.</summary>
    public decimal CommissionPercent { get; set; }

    /// <summary>The commission CasaZen keeps, in cents; 0 when none is charged (an offline payment never has one).</summary>
    public int CommissionCents { get; set; }

    /// <summary>What the supplier receives before Stripe's own fees, in cents.</summary>
    public int NetCents { get; set; }

    /// <summary><c>Stripe</c> or <c>Offline</c>; null until it is paid.</summary>
    public string? PaidVia { get; set; }

    public DateTime? PaidAt { get; set; }

    /// <summary>When the payment was first asked of the payer; null while it is pending (nothing was sent).</summary>
    public DateTime? RequestedAt { get; set; }

    /// <summary>When the payer was last sent a link (the supplier can ask again a day later).</summary>
    public DateTime? LastSentAt { get; set; }

    /// <summary>Emails with a link sent so far (the request and its reminders).</summary>
    public int SentCount { get; set; }

    /// <summary>The reason the supplier gave when it recorded the payment as received outside CasaZen.</summary>
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
