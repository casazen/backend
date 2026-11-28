using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.ServiceRequests;

public sealed class ReportServiceProblemRequest
{
    [Required(ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    [MaxLength(32, ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    public string? Reason { get; set; }

    [MaxLength(ServiceDisputeLimits.TextMaxLength + 200, ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    public string? Text { get; set; }
}

public sealed class ReplyToServiceDisputeRequest
{
    [Required(ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    [MaxLength(ServiceDisputeLimits.TextMaxLength + 200, ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    public string? Text { get; set; }
}

public sealed class ServiceDisputePayerDto
{
    public Guid Id { get; set; }

    public string Status { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string OpenedBy { get; set; } = string.Empty;

    public bool NoShow { get; set; }

    public string? Text { get; set; }

    public DateTime OpenedAt { get; set; }

    public DateTime SupplierReplyDueAt { get; set; }

    public bool SupplierReplied { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public int? RefundCents { get; set; }

    public bool Settled { get; set; }

    public static ServiceDisputePayerDto From(ServiceDisputePayerView view) => new()
    {
        Id = view.Id,
        Status = view.Status.ToString(),
        Reason = ServiceDisputeRules.ReasonName(view.Reason),
        OpenedBy = ServiceDisputeRules.OpenerName(view.OpenedBy),
        NoShow = view.NoShow,
        Text = view.Text,
        OpenedAt = AsUtc(view.OpenedAt),
        SupplierReplyDueAt = AsUtc(view.SupplierReplyDueAt),
        SupplierReplied = view.SupplierReplied,
        ResolvedAt = view.ResolvedAt is { } at ? AsUtc(at) : null,
        RefundCents = view.RefundCents,
        Settled = view.Settled,
    };

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

public sealed class ServiceDisputeSupplierDto
{
    public Guid Id { get; set; }

    public Guid ServiceRequestId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string OpenedBy { get; set; } = string.Empty;

    public bool NoShow { get; set; }

    public string? CustomerText { get; set; }

    public DateTime OpenedAt { get; set; }

    public DateTime ReplyDueAt { get; set; }

    public string? Reply { get; set; }

    public DateTime? RepliedAt { get; set; }

    public bool CanReply { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public int? RefundCents { get; set; }

    public static ServiceDisputeSupplierDto From(ServiceDisputeSupplierView view) => new()
    {
        Id = view.Id,
        ServiceRequestId = view.ServiceRequestId,
        Status = view.Status.ToString(),
        Reason = ServiceDisputeRules.ReasonName(view.Reason),
        OpenedBy = ServiceDisputeRules.OpenerName(view.OpenedBy),
        NoShow = view.NoShow,
        CustomerText = view.CustomerText,
        OpenedAt = AsUtc(view.OpenedAt),
        ReplyDueAt = AsUtc(view.ReplyDueAt),
        Reply = view.Reply,
        RepliedAt = view.RepliedAt is { } repliedAt ? AsUtc(repliedAt) : null,
        CanReply = view.CanReply,
        ResolvedAt = view.ResolvedAt is { } resolvedAt ? AsUtc(resolvedAt) : null,
        RefundCents = view.RefundCents,
    };

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
