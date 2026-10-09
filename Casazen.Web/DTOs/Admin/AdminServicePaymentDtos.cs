using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Admin;

/// <summary>
/// One supplier service payment as the platform admin sees it (<c>GET api/admin/supplier-payments</c>): gross, commission, net, what
/// was refunded and the commission that went back with it. Ids and names of the two orgs, never an email, a phone or an address.
/// Amounts are in cents.
/// </summary>
public sealed class AdminServicePaymentDto
{
    public Guid Id { get; set; }

    public Guid ServiceRequestId { get; set; }

    public Guid SupplierOrgId { get; set; }

    public string SupplierName { get; set; } = string.Empty;

    public Guid? PayerOrgId { get; set; }

    public string? PayerName { get; set; }

    /// <summary><c>Requested</c>, <c>Processing</c>, <c>Paid</c>, <c>Failed</c>, <c>Canceled</c>, <c>PartiallyRefunded</c>, <c>Refunded</c> or <c>NeedsReview</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary><c>Stripe</c> or <c>Offline</c>; null while it is not paid.</summary>
    public string? PaidVia { get; set; }

    public int AmountCents { get; set; }

    /// <summary>ISO currency in capitals (<c>EUR</c>).</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>The commission percentage the payment was created with (a snapshot).</summary>
    public decimal CommissionPercent { get; set; }

    /// <summary>CasaZen's commission in cents; 0 when none was charged.</summary>
    public int CommissionCents { get; set; }

    /// <summary>What the supplier gets before Stripe's own fees.</summary>
    public int NetCents { get; set; }

    public int RefundedCents { get; set; }

    /// <summary>The part of the commission that went back with the refunds, in proportion (rounded to the cent).</summary>
    public int CommissionRefundedCents { get; set; }

    public DateTime? RequestedAt { get; set; }

    /// <summary>When the payment became late (unpaid <c>LateAfterDays</c> after the request); null while on time.</summary>
    public DateTime? LateAt { get; set; }

    public DateTime? PaidAt { get; set; }

    /// <summary>
    /// Stripe's code of the last failed attempt, or, for a payment that needs a review, <c>review:</c> and what did not match
    /// (<c>account</c>, <c>amount</c>, <c>currency</c>, <c>fee</c>, <c>intent</c>). No personal data.
    /// </summary>
    public string? FailureCode { get; set; }

    public string? StripePaymentIntentId { get; set; }

    public string? ConnectedAccountId { get; set; }

    public DateTime CreatedAt { get; set; }

    internal static AdminServicePaymentDto From(AdminServicePaymentItem item) => new()
    {
        Id = item.Id,
        ServiceRequestId = item.ServiceRequestId,
        SupplierOrgId = item.SupplierOrgId,
        SupplierName = item.SupplierName,
        PayerOrgId = item.PayerOrgId,
        PayerName = item.PayerName,
        Status = item.Status.ToString(),
        PaidVia = item.PaidVia?.ToString(),
        AmountCents = item.AmountCents,
        Currency = item.Currency.ToUpperInvariant(),
        CommissionPercent = item.CommissionPercent,
        CommissionCents = item.ApplicationFeeCents,
        NetCents = item.NetCents,
        RefundedCents = item.RefundedCents,
        CommissionRefundedCents = item.CommissionRefundedCents,
        RequestedAt = item.RequestedAt,
        LateAt = item.LateAt,
        PaidAt = item.PaidAt,
        FailureCode = item.FailureCode,
        StripePaymentIntentId = item.StripePaymentIntentId,
        ConnectedAccountId = item.ConnectedAccountId,
        CreatedAt = item.CreatedAt,
    };
}

/// <summary>One refund of a service payment.</summary>
public sealed class AdminServicePaymentRefundDto
{
    public Guid Id { get; set; }

    /// <summary>1, 2, 3… within the payment: the <c>n</c> of the idempotency key.</summary>
    public int Sequence { get; set; }

    public int AmountCents { get; set; }

    /// <summary><c>Pending</c>, <c>Succeeded</c>, <c>Failed</c>, <c>Canceled</c> or <c>RequiresAction</c>. Only a succeeded refund counts.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary><c>Admin</c> or <c>Stripe</c> (made outside CasaZen).</summary>
    public string Origin { get; set; } = string.Empty;

    public string? StripeRefundId { get; set; }

    /// <summary>Stripe's reason when it failed or was canceled.</summary>
    public string? FailureCode { get; set; }

    /// <summary>The admin's note.</summary>
    public string? Reason { get; set; }

    /// <summary>The part of the commission that went back with this refund, in cents; null until it succeeds.</summary>
    public int? CommissionRefundedCents { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    internal static AdminServicePaymentRefundDto From(ServiceRequestPaymentRefund refund) => new()
    {
        Id = refund.Id,
        Sequence = refund.Sequence,
        AmountCents = refund.AmountCents,
        Status = refund.Status.ToString(),
        Origin = refund.Origin.ToString(),
        StripeRefundId = refund.StripeRefundId,
        FailureCode = refund.FailureCode,
        Reason = refund.Reason,
        CommissionRefundedCents = refund.ApplicationFeeRefundedCents,
        CreatedAt = refund.CreatedAt,
        CompletedAt = refund.CompletedAt,
    };
}

/// <summary>A payment with its refunds (<c>GET api/admin/service-payments/{id}</c>).</summary>
public sealed class AdminServicePaymentDetailDto
{
    public AdminServicePaymentDto Payment { get; set; } = new();

    public IEnumerable<AdminServicePaymentRefundDto> Refunds { get; set; } = [];

    internal static AdminServicePaymentDetailDto From(AdminServicePaymentDetail detail) => new()
    {
        Payment = AdminServicePaymentDto.From(detail.Payment),
        Refunds = detail.Refunds.Select(AdminServicePaymentRefundDto.From).ToList(),
    };
}

/// <summary>Body of <c>POST api/admin/service-payments/{id}/refund</c> (SP-15b).</summary>
public sealed class RefundServicePaymentRequest
{
    /// <summary>The amount to give back, in cents; everything still refundable when left out.</summary>
    [Range(1, ServiceRequestLimits.MaxAmountCents, ErrorMessage = ServicePaymentErrors.RefundAmountInvalidMessageKey)]
    public int? AmountCents { get; set; }

    /// <summary>Why: an internal note of at most 500 characters. It is never sent to Stripe.</summary>
    [MaxLength(ServicePaymentLimits.OfflineNoteMaxLength, ErrorMessage = ServicePaymentErrors.RefundReasonTooLongMessageKey)]
    public string? Reason { get; set; }
}

/// <summary>The commission applied to a supplier (<c>GET/PUT api/admin/suppliers/{orgId}/commission</c>).</summary>
public sealed class SupplierCommissionDto
{
    public Guid SupplierOrgId { get; set; }

    /// <summary>The platform's percentage (<c>SupplierPayments:CommissionPercent</c>).</summary>
    public decimal PlatformPercent { get; set; }

    /// <summary>The supplier's own percentage; null when it has none.</summary>
    public decimal? OverridePercent { get; set; }

    /// <summary>When the override stops; null = it has no end.</summary>
    public DateTime? OverrideUntil { get; set; }

    /// <summary>The override is in force now.</summary>
    public bool OverrideActive { get; set; }

    /// <summary>What a payment created now is charged.</summary>
    public decimal EffectivePercent { get; set; }

    internal static SupplierCommissionDto From(SupplierCommissionSetting setting) => new()
    {
        SupplierOrgId = setting.SupplierOrgId,
        PlatformPercent = setting.PlatformPercent,
        OverridePercent = setting.OverridePercent,
        OverrideUntil = setting.OverrideUntil,
        OverrideActive = setting.OverrideActive,
        EffectivePercent = setting.EffectivePercent,
    };
}

/// <summary>Body of <c>PUT api/admin/suppliers/{orgId}/commission</c> (SP-15b).</summary>
public sealed class SetSupplierCommissionRequest
{
    /// <summary>
    /// The supplier's own commission, a percentage from 0 to the highest the configuration allows with two decimals at most (0 is a
    /// free period). Null removes the override: the platform's percentage applies again.
    /// </summary>
    public decimal? Percent { get; set; }

    /// <summary>When the override stops (UTC), in the future; left out = no end. Only with a percentage.</summary>
    public DateTime? Until { get; set; }

    /// <summary>Why: required, at most 500 characters. Recorded in the audit trail with the admin and the old and new value.</summary>
    [Required(ErrorMessage = ServicePaymentErrors.CommissionReasonMessageKey)]
    [MaxLength(ServicePaymentLimits.OfflineNoteMaxLength, ErrorMessage = ServicePaymentErrors.CommissionReasonMessageKey)]
    public string Reason { get; set; } = string.Empty;
}
