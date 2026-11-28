using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Admin;

public class AdminServiceDisputeDto
{
    public Guid Id { get; set; }

    public Guid ServiceRequestId { get; set; }

    public Guid PaymentId { get; set; }

    public Guid SupplierOrgId { get; set; }

    public string SupplierName { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string OpenedBy { get; set; } = string.Empty;

    public bool NoShow { get; set; }

    public DateTime OpenedAt { get; set; }

    public DateTime SupplierReplyDueAt { get; set; }

    public DateTime? SupplierRepliedAt { get; set; }

    public DateTime? DecisionDueAt { get; set; }

    public bool Overdue { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public int AmountCents { get; set; }

    public int NetCents { get; set; }

    public int RefundedCents { get; set; }

    public int RefundableCents { get; set; }

    public int? DecidedRefundCents { get; set; }

    public bool Settled { get; set; }

    public static AdminServiceDisputeDto From(AdminServiceDisputeSummary summary) => Fill(new AdminServiceDisputeDto(), summary);

    protected static T Fill<T>(T dto, AdminServiceDisputeSummary summary)
        where T : AdminServiceDisputeDto
    {
        dto.Id = summary.Id;
        dto.ServiceRequestId = summary.ServiceRequestId;
        dto.PaymentId = summary.ServiceRequestPaymentId;
        dto.SupplierOrgId = summary.SupplierOrgId;
        dto.SupplierName = summary.SupplierName;
        dto.Status = summary.Status.ToString();
        dto.Reason = ServiceDisputeRules.ReasonName(summary.Reason);
        dto.OpenedBy = ServiceDisputeRules.OpenerName(summary.OpenedBy);
        dto.NoShow = summary.NoShow;
        dto.OpenedAt = AsUtc(summary.OpenedAt);
        dto.SupplierReplyDueAt = AsUtc(summary.SupplierReplyDueAt);
        dto.SupplierRepliedAt = summary.SupplierRepliedAt is { } repliedAt ? AsUtc(repliedAt) : null;
        dto.DecisionDueAt = summary.DecisionDueAt is { } dueAt ? AsUtc(dueAt) : null;
        dto.Overdue = summary.Overdue;
        dto.ResolvedAt = summary.ResolvedAt is { } resolvedAt ? AsUtc(resolvedAt) : null;
        dto.AmountCents = summary.AmountCents;
        dto.NetCents = summary.NetCents;
        dto.RefundedCents = summary.RefundedCents;
        dto.RefundableCents = summary.RefundableCents;
        dto.DecidedRefundCents = summary.DecidedRefundCents;
        dto.Settled = summary.Settled;
        return dto;
    }

    protected static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

public sealed class AdminServiceDisputeDetailDto : AdminServiceDisputeDto
{
    public string PaymentStatus { get; set; } = string.Empty;

    public int CommissionCents { get; set; }

    public string ServiceName { get; set; } = string.Empty;

    public string RequestStatus { get; set; } = string.Empty;

    public DateTime? ScheduledEndUtc { get; set; }

    public string? CustomerText { get; set; }

    public string? SupplierReply { get; set; }

    public string? AdminNote { get; set; }

    public string? ResolvedByUserId { get; set; }

    public static AdminServiceDisputeDetailDto From(AdminServiceDisputeDetail detail)
    {
        var dto = Fill(new AdminServiceDisputeDetailDto(), detail.Summary);
        dto.PaymentStatus = detail.PaymentStatus.ToString();
        dto.CommissionCents = detail.CommissionCents;
        dto.ServiceName = detail.ServiceName;
        dto.RequestStatus = detail.RequestStatus.ToString();
        dto.ScheduledEndUtc = detail.ScheduledEndUtc is { } end ? AsUtc(end) : null;
        dto.CustomerText = detail.CustomerText;
        dto.SupplierReply = detail.SupplierReply;
        dto.AdminNote = detail.AdminNote;
        dto.ResolvedByUserId = detail.ResolvedByUserId;
        return dto;
    }
}

public sealed class ResolveServiceDisputeBody
{
    [Required(ErrorMessage = ServiceDisputeErrors.OutcomeInvalidMessageKey)]
    [MaxLength(32, ErrorMessage = ServiceDisputeErrors.OutcomeInvalidMessageKey)]
    public string? Outcome { get; set; }

    [Range(1, ServiceRequestLimits.MaxAmountCents, ErrorMessage = ServiceDisputeErrors.RefundInvalidMessageKey)]
    public int? RefundCents { get; set; }

    /// <summary>The admin's internal note, at most 1000 characters. It is kept encrypted, never shown to the parties and never sent to Stripe.</summary>
    [MaxLength(ServiceDisputeLimits.TextMaxLength + 200, ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    public string? Note { get; set; }

    public ResolveServiceDisputeRequest ToRequest() => new(Outcome, RefundCents, Note);
}
