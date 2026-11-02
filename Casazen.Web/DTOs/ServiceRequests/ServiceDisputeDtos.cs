using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.ServiceRequests;

// The complaint about a job paid through CasaZen and its dispute (SP-15c-c, decisions VT-D1 and VT-D16), for the people who are in it: the host that
// paid (and the private customer of a showcase, whose own DTO is in PublicSupplierBookingManageDtos), and the supplier that is paid. One DTO per
// audience, made only from the fields that audience may read: the host reads its own words, the state and the decision; the supplier reads the payer's
// words and its own reply. The note of the admin who decided is in neither, and nothing here carries a name, an e-mail address or a phone. Text only
// (no photos, VT-D12).

/// <summary>
/// Body of <c>POST api/service-requests/{id}/report-problem</c> and its long-rent twin (SP-15c-c): what is wrong and, in the host's words, why. A
/// reason that is not one of the five, or a text that is too long, is 422 <c>service_dispute_invalid</c> naming the field.
/// </summary>
public sealed class ReportServiceProblemRequest
{
    /// <summary><c>not_done</c>, <c>incomplete</c>, <c>poor_quality</c>, <c>not_as_described</c> or <c>other</c>.</summary>
    [Required(ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    [MaxLength(32, ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    public string? Reason { get; set; }

    /// <summary>What the host writes, at most 1000 characters; required when the reason is <c>other</c>.</summary>
    [MaxLength(ServiceDisputeLimits.TextMaxLength + 200, ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    public string? Text { get; set; }
}

/// <summary>Body of <c>POST api/supplier/requests/{id}/dispute/reply</c> (SP-15c-c): the supplier's one reply, in its own words.</summary>
public sealed class ReplyToServiceDisputeRequest
{
    /// <summary>The reply, at most 1000 characters. Plain text.</summary>
    [Required(ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    [MaxLength(ServiceDisputeLimits.TextMaxLength + 200, ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    public string? Text { get; set; }
}

/// <summary>
/// The complaint as the payer reads it: the host here (the private customer of a showcase has <c>payment.dispute</c> in its booking). Its own words, the
/// state and the decision; never the supplier's words nor the admin's note.
/// </summary>
public sealed class ServiceDisputePayerDto
{
    public Guid Id { get; set; }

    /// <summary><c>AwaitingSupplier</c>, <c>AwaitingDecision</c>, <c>ResolvedForSupplier</c>, <c>ResolvedForCustomer</c> or <c>Withdrawn</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary><c>not_done</c>, <c>incomplete</c>, <c>poor_quality</c>, <c>not_as_described</c> or <c>other</c>.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary><c>customer</c>, <c>host</c> or <c>system</c> (CasaZen opened it for a job nobody marked completed).</summary>
    public string OpenedBy { get; set; } = string.Empty;

    /// <summary>The supplier never marked the work completed and the time agreed for it ended.</summary>
    public bool NoShow { get; set; }

    /// <summary>What the payer wrote.</summary>
    public string? Text { get; set; }

    public DateTime OpenedAt { get; set; }

    /// <summary>When the supplier's time to reply ends; then the complaint goes to the CasaZen team anyway.</summary>
    public DateTime SupplierReplyDueAt { get; set; }

    /// <summary>The supplier replied; what it wrote is not shown.</summary>
    public bool SupplierReplied { get; set; }

    public DateTime? ResolvedAt { get; set; }

    /// <summary>What goes back to the payer, in cents, when it was decided for the customer.</summary>
    public int? RefundCents { get; set; }

    /// <summary>The refund that was decided has been made.</summary>
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

/// <summary>
/// The complaint as the supplier reads it (<c>GET api/supplier/requests/{id}/dispute</c>): the payer's words, its own reply, the state and the decision.
/// Never the admin's note. The payer's words are the payer's, about a job in its home: for the supplier's eyes, in the console only.
/// </summary>
public sealed class ServiceDisputeSupplierDto
{
    public Guid Id { get; set; }

    public Guid ServiceRequestId { get; set; }

    /// <summary><c>AwaitingSupplier</c>, <c>AwaitingDecision</c>, <c>ResolvedForSupplier</c>, <c>ResolvedForCustomer</c> or <c>Withdrawn</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary><c>not_done</c>, <c>incomplete</c>, <c>poor_quality</c>, <c>not_as_described</c> or <c>other</c>.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary><c>customer</c>, <c>host</c> or <c>system</c>.</summary>
    public string OpenedBy { get; set; } = string.Empty;

    /// <summary>The supplier never marked the work completed and the time agreed for it ended.</summary>
    public bool NoShow { get; set; }

    /// <summary>What the payer wrote.</summary>
    public string? CustomerText { get; set; }

    public DateTime OpenedAt { get; set; }

    /// <summary>When the supplier's time to reply ends; a later reply is still taken while the dispute is open.</summary>
    public DateTime ReplyDueAt { get; set; }

    /// <summary>The supplier's reply.</summary>
    public string? Reply { get; set; }

    public DateTime? RepliedAt { get; set; }

    /// <summary>The supplier can still reply: the dispute is open and it has not replied.</summary>
    public bool CanReply { get; set; }

    public DateTime? ResolvedAt { get; set; }

    /// <summary>What went back to the payer, in cents, when it was decided for the customer (the supplier loses it, and CasaZen keeps its commission).</summary>
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
