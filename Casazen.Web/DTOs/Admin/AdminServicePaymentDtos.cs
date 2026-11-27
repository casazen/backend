using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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

    public string Status { get; set; } = string.Empty;

    public string? PaidVia { get; set; }

    public int AmountCents { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal CommissionPercent { get; set; }

    public int CommissionCents { get; set; }

    public int NetCents { get; set; }

    public int RefundedCents { get; set; }

    public int CommissionRefundedCents { get; set; }

    public DateTime? RequestedAt { get; set; }

    public DateTime? LateAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public string? FailureCode { get; set; }

    public string? StripePaymentIntentId { get; set; }

    public string? ConnectedAccountId { get; set; }

    public DateTime CreatedAt { get; set; }

    public string FundsModel { get; set; } = nameof(ServiceFundsModel.DirectCharge);

    public DateTime? CapturedAt { get; set; }

    public DateTime? ReleaseAt { get; set; }

    public DateTime? ReleasedAt { get; set; }

    public string? StripeTransferId { get; set; }

    public int TransferAttempts { get; set; }

    public string? TransferFailureCode { get; set; }

    public DateTime? TransferNextAttemptAt { get; set; }

    public string? CardDisputeId { get; set; }

    public string? CardDisputeStatus { get; set; }

    public DateTime? CardDisputeAt { get; set; }

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
        FundsModel = item.FundsModel.ToString(),
        CapturedAt = item.CapturedAt,
        ReleaseAt = item.ReleaseAt,
        ReleasedAt = item.ReleasedAt,
        StripeTransferId = item.StripeTransferId,
        TransferAttempts = item.TransferAttempts,
        TransferFailureCode = item.TransferFailureCode,
        TransferNextAttemptAt = item.TransferNextAttemptAt,
        CardDisputeId = item.CardDisputeId,
        CardDisputeStatus = item.CardDisputeStatus,
        CardDisputeAt = item.CardDisputeAt,
    };
}

public sealed class AdminServicePaymentRefundDto
{
    public Guid Id { get; set; }

    /// <summary>1, 2, 3… within the payment: the <c>n</c> of the idempotency key.</summary>
    public int Sequence { get; set; }

    public int AmountCents { get; set; }

    public string Status { get; set; } = string.Empty;

    /// <summary><c>Admin</c>, <c>Stripe</c> (made outside CasaZen) or <c>System</c> (CasaZen gave the money back because the request closed, SP-15c).</summary>
    public string Origin { get; set; } = string.Empty;

    public string? StripeRefundId { get; set; }

    public string? FailureCode { get; set; }

    public string? Reason { get; set; }

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

public sealed class RefundServicePaymentRequest
{
    [Range(1, ServiceRequestLimits.MaxAmountCents, ErrorMessage = ServicePaymentErrors.RefundAmountInvalidMessageKey)]
    public int? AmountCents { get; set; }

    /// <summary>Why: an internal note of at most 500 characters. It is never sent to Stripe.</summary>
    [MaxLength(ServicePaymentLimits.OfflineNoteMaxLength, ErrorMessage = ServicePaymentErrors.RefundReasonTooLongMessageKey)]
    public string? Reason { get; set; }

    /// <summary>
    /// SP-15c-b, rule R4 — for the money CasaZen holds, before it is released: the supplier was at fault, so the payer gets back what the supplier
    /// would have been paid (the price minus the commission), CasaZen keeps its commission and the supplier gets nothing. <c>amountCents</c> may
    /// ask for less but never for more than that. Left out or false (rule R5, cancelling is always free): everything still refundable goes back,
    /// the commission included. Not for a direct charge (422).
    /// </summary>
    public bool KeepCommission { get; set; }
}

public sealed class SupplierCommissionDto
{
    public Guid SupplierOrgId { get; set; }

    public decimal PlatformPercent { get; set; }

    public decimal? OverridePercent { get; set; }

    public DateTime? OverrideUntil { get; set; }

    public bool OverrideActive { get; set; }

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

public sealed class SetSupplierCommissionRequest
{
    public decimal? Percent { get; set; }

    public DateTime? Until { get; set; }

    [Required(ErrorMessage = ServicePaymentErrors.CommissionReasonMessageKey)]
    [MaxLength(ServicePaymentLimits.OfflineNoteMaxLength, ErrorMessage = ServicePaymentErrors.CommissionReasonMessageKey)]
    public string Reason { get; set; } = string.Empty;
}
